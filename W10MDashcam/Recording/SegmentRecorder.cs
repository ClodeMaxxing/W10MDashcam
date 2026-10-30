using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using W10MDashcam.Streaming;

namespace W10MDashcam.Recording
{
    /// <summary>A recording on storage, for the list on the web page.</summary>
    internal sealed class RecordingInfo
    {
        public string Name;
        public long Size;
        public bool Saved;
        public bool Recording; // the file being written right now
    }

    /// <summary>
    /// The dashcam recorder. Takes the same encoded frames the RTSP server sends (no second encode) and writes
    /// them to fragmented MP4 files in segments, in the chosen place (StorageMode): the SD card (W10MDashcam folder),
    /// the app's own storage, or a folder picked by the user:
    ///   2026-10-30_13-40-05.mp4      loop recordings; the oldest are deleted to stay within the limit
    ///   Saved\...                    clips kept with "Save clip"; never deleted automatically
    /// Only files named like the app's own recordings are ever counted or deleted, so a picked folder that also
    /// holds other videos is safe.
    ///
    /// The camera thread only queues frames (cheap, never throws, never waits). One worker writes them out, so a slow
    /// or full card can never stall the camera: if the queue grows too big, frames are dropped up to the next keyframe.
    /// </summary>
    internal sealed class SegmentRecorder
    {
        public Action<string> Log = _ => { };

        // ---- settings (read by the worker as they change)
        public volatile bool Enabled = true;
        public volatile int SegmentSeconds = 180;
        /// <summary>Most space the loop recordings may use (saved clips are not counted).</summary>
        public long LimitBytes = 16L << 30;
        /// <summary>Always leave this much free on the storage.</summary>
        public long MinFreeBytes = 1L << 30;
        /// <summary>Where to record: "auto" (SD card if present, else phone), "phone", "sd", or "custom" (the picked folder).</summary>
        public volatile string StorageMode = "auto";
        /// <summary>Key of the picked folder in the app's future-access list.</summary>
        public const string CustomFolderToken = "RecordingFolder";
        /// <summary>The picked folder's path, for display (empty until one is picked).</summary>
        public string CustomFolderPath { get; private set; } = "";

        private const long MaxQueueBytes = 48L << 20;     // ~16 s at 24 Mbps; ~100 s at 4 Mbps
        private const long Second = 10000000L;            // 100 ns units
        private const long MaxFragment = 2 * Second;      // a fragment is written at least this often
        private static readonly Regex SafeName = new Regex(@"^[0-9A-Za-z_\-\. ()]{1,80}\.mp4$");
        /// <summary>The app's own loop files: 2026-10-30_13-40-05.mp4 (or "... (2).mp4"). Nothing else is ever deleted.</summary>
        private static readonly Regex OwnName = new Regex(@"^\d{4}-\d\d-\d\d_\d\d-\d\d-\d\d( \(\d+\))?\.mp4$");

        // ---- queue (camera thread -> worker)
        private readonly object _lock = new object();
        private readonly Queue<object> _queue = new Queue<object>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private long _queuedBytes;
        private bool _dropping;
        private long _dropped;
        private Task _worker;

        private sealed class ParamSets { public byte[] Sps, Pps; }
        private sealed class AudioFormatItem { public AudioFormat Format; }
        private enum Cmd { End, Save, Shutdown, Housekeep, Relocate }
        private sealed class Command { public Cmd What; public TaskCompletionSource<bool> Done; }

        // ---- worker state
        private byte[] _sps, _pps;
        private AudioFormat _audio;
        private StorageFolder _root, _loop, _saved;
        private readonly List<RecordingInfo> _segments = new List<RecordingInfo>(); // closed loop files, oldest first
        private Segment _seg;
        private long _lastVideoTime = -1;
        private DateTime _retryAfter = DateTime.MinValue;
        private DateTime _nextHousekeep = DateTime.MinValue;
        private DateTime _lastFrameAt = DateTime.MinValue;
        private bool _waitedForKey;
        private List<AudioSample> _carryAudio; // audio after the last file's end, for the next file
        private string _lastClosed;            // the loop file that ended right before the current one (Save clip keeps it once)
        private bool _lastClosedKnown;         // set once: after an app restart the newest loop file counts as the previous one

        private sealed class Segment
        {
            public StorageFile File;
            public Stream Stream;
            public FragmentedMp4Writer Writer;
            public string Name;
            public long Start;
            public bool Protect;
            public readonly List<VideoFrame> Pending = new List<VideoFrame>();
            public readonly List<AudioSample> Audio = new List<AudioSample>();
        }

        // ---- status (read from any thread)
        public string State { get; private set; } = "off";
        public string Location { get; private set; } = "";
        public string CurrentName => _seg?.Name ?? "";
        public double CurrentSeconds { get; private set; }
        public int LoopCount { get; private set; }
        public long LoopBytes { get; private set; }
        public long FreeBytes { get; private set; } = -1;
        public long DroppedFrames => Interlocked.Read(ref _dropped);
        public double WriteMBps { get; private set; }
        public long MaxWriteMs { get; private set; }
        public int SavedClips { get; private set; }
        public string LastError { get; private set; } = "";
        /// <summary>
        /// Result of Save clip (raised on the recorder's thread): true + what was kept once the clip's file is finished,
        /// or false + why nothing could be saved.
        /// </summary>
        public event Action<bool, string> ClipResult;
        private bool _cutRequested;      // Save clip: finish the current file at the next keyframe
        private string _clipText;        // what the pending clip keeps, for ClipResult

        private void RaiseClip(bool ok, string text)
        {
            try { ClipResult?.Invoke(ok, text); } catch { }
        }
        private long _statBytes, _statTicks;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        // ================================================================ camera-thread entry points (never throw)

        public void OnParameterSets(byte[] sps, byte[] pps) => Enqueue(new ParamSets { Sps = sps, Pps = pps }, 0);
        public void OnAudioFormat(AudioFormat f) => Enqueue(new AudioFormatItem { Format = f }, 0);

        public void OnAccessUnit(IList<byte[]> nals, long time, bool key)
        {
            try
            {
                if (!Enabled) return;
                int bytes = 0;
                foreach (var n in nals) bytes += n.Length;
                lock (_lock)
                {
                    if (_dropping)
                    {
                        // resume at a keyframe once the writer has caught up
                        if (!key || _queuedBytes > MaxQueueBytes / 2) { _dropped++; return; }
                        _dropping = false;
                        _queue.Enqueue(Cmd.End); // the gap ends this file; the next one starts at this keyframe
                    }
                    else if (_queuedBytes + bytes > MaxQueueBytes)
                    {
                        _dropping = true;
                        _dropped++;
                        return;
                    }
                    _queue.Enqueue(new VideoFrame { Nals = nals, Time = time, Key = key, Bytes = bytes });
                    _queuedBytes += bytes;
                }
                Wake();
            }
            catch { }
        }

        public void OnAudio(byte[] data, long time)
        {
            try
            {
                if (!Enabled || data == null) return;
                lock (_lock)
                {
                    if (_dropping || _queuedBytes + data.Length > MaxQueueBytes) return;
                    _queue.Enqueue(new AudioSample { Data = data, Time = time });
                    _queuedBytes += data.Length;
                }
                Wake();
            }
            catch { }
        }

        // ================================================================ commands (UI / web)

        /// <summary>The stream stopped: finish the current file.</summary>
        public void EndSegment() => Enqueue(new Command { What = Cmd.End }, 0);

        /// <summary>Keep the current recording and the one before it (moved to Saved, never loop-deleted).</summary>
        public void SaveClip() => Enqueue(new Command { What = Cmd.Save }, 0);

        /// <summary>Apply changed limits now (delete old recordings if needed).</summary>
        public void Housekeep() => Enqueue(new Command { What = Cmd.Housekeep }, 0);

        /// <summary>Record somewhere else from now on (StorageMode changed or a new folder picked): the current file ends here.</summary>
        public void Relocate() => Enqueue(new Command { What = Cmd.Relocate }, 0);

        /// <summary>Finish the current file and wait (at most a few seconds) - for app suspend.</summary>
        public async Task ShutdownAsync()
        {
            var done = new TaskCompletionSource<bool>();
            Enqueue(new Command { What = Cmd.Shutdown, Done = done }, 0);
            await Task.WhenAny(done.Task, Task.Delay(3000));
        }

        private void Enqueue(object item, int bytes)
        {
            try
            {
                lock (_lock) { _queue.Enqueue(item); _queuedBytes += bytes; }
                Wake();
            }
            catch { }
        }

        private void Wake()
        {
            if (_worker == null)
                lock (_lock)
                    if (_worker == null) _worker = Task.Run(RunAsync);
            _signal.Release();
        }

        // ================================================================ worker

        private async Task RunAsync()
        {
            while (true)
            {
                await _signal.WaitAsync(1000);
                while (true)
                {
                    object item;
                    lock (_lock)
                    {
                        if (_queue.Count == 0) break;
                        item = _queue.Dequeue();
                        if (item is VideoFrame vf) _queuedBytes -= vf.Bytes;
                        else if (item is AudioSample au) _queuedBytes -= au.Data.Length;
                    }
                    try { await ProcessAsync(item); }
                    catch (Exception ex) { await FailAsync("Recording error: " + ex.Message); }
                }
                try { await IdleChecksAsync(); }
                catch (Exception ex) { Log("Recording housekeeping: " + ex.Message); }
            }
        }

        private async Task ProcessAsync(object item)
        {
            switch (item)
            {
                case VideoFrame f: await OnVideoAsync(f); break;
                case AudioSample a: OnAudioSample(a); break;
                case Cmd c: await RunCommandAsync(new Command { What = c }); break;
                case Command c: await RunCommandAsync(c); break;
                case ParamSets p:
                    bool changed = _sps != null && (!_sps.SequenceEqual(p.Sps) || !_pps.SequenceEqual(p.Pps));
                    _sps = p.Sps; _pps = p.Pps;
                    if (changed && _seg != null) { Log("Recording: new video format - starting a new file"); await CloseAsync(-1); }
                    break;
                case AudioFormatItem af:
                    bool audioChanged = (_audio == null) != (af.Format == null) ||
                        (_audio != null && (_audio.SampleRate != af.Format.SampleRate || _audio.Channels != af.Format.Channels));
                    _audio = af.Format;
                    if (audioChanged && _seg != null) await CloseAsync(-1);
                    break;
            }
        }

        private async Task RunCommandAsync(Command c)
        {
            try
            {
                switch (c.What)
                {
                    case Cmd.End:
                        if (_seg != null) await CloseAsync(-1);
                        _lastVideoTime = -1;
                        break;
                    case Cmd.Shutdown:
                        if (_seg != null) await CloseAsync(-1);
                        _lastVideoTime = -1;
                        break;
                    case Cmd.Save:
                        await SaveAsync();
                        break;
                    case Cmd.Relocate:
                        if (_seg != null) await CloseAsync(-1);
                        _lastVideoTime = -1;
                        _root = null;
                        await EnsureFoldersAsync();
                        break;
                    case Cmd.Housekeep:
                        _root = null; // re-read the folder (a file may have been deleted from the web page)
                        await EnsureFoldersAsync();
                        await HousekeepAsync();
                        break;
                }
            }
            finally { c.Done?.TrySetResult(true); }
        }

        private async Task OnVideoAsync(VideoFrame f)
        {
            _lastFrameAt = DateTime.UtcNow;
            if (!Enabled)
            {
                if (_seg != null) { await CloseAsync(-1); Log("Recording turned off"); }
                State = "off";
                return;
            }
            if (_sps == null) return; // no decoder setup yet

            // A jump in the encoder clock means the stream restarted: start a new file.
            if (_lastVideoTime >= 0 && (f.Time <= _lastVideoTime || f.Time - _lastVideoTime > 3 * Second) && _seg != null)
                await CloseAsync(-1);
            _lastVideoTime = f.Time;

            if (_seg == null)
            {
                if (!f.Key) { if (!_waitedForKey) { _waitedForKey = true; State = "waiting for a keyframe"; } return; }
                if (DateTime.UtcNow < _retryAfter) return;
                _waitedForKey = false;
                if (!await OpenAsync(f.Time)) return;
            }
            else if (f.Key)
            {
                await FlushAsync(f.Time);
                if (_cutRequested || f.Time - _seg.Start >= SegmentSeconds * Second)
                {
                    await CloseAsync(f.Time);
                    if (!await OpenAsync(f.Time)) return;
                }
            }
            else if (_seg.Pending.Count > 0 && f.Time - _seg.Pending[0].Time >= MaxFragment)
            {
                await FlushAsync(f.Time); // long keyframe interval: still write at least every 2 s
            }
            _seg.Pending.Add(f);
            CurrentSeconds = (f.Time - _seg.Start) / (double)Second;
        }

        private void OnAudioSample(AudioSample a)
        {
            if (_seg == null || !_seg.Writer.HasAudio || a.Time < _seg.Start) return;
            _seg.Audio.Add(a);
        }

        /// <summary>Writes the pending frames as one fragment. nextTime = time of the frame after them (-1: unknown).</summary>
        private Task FlushAsync(long nextTime)
        {
            var s = _seg;
            if (s == null || s.Pending.Count == 0) return Task.CompletedTask;
            var audio = new List<AudioSample>();
            int keep = 0;
            foreach (var a in s.Audio)
            {
                if (nextTime < 0 || a.Time < nextTime) audio.Add(a);
                else s.Audio[keep++] = a;
            }
            s.Audio.RemoveRange(keep, s.Audio.Count - keep);

            long t = _clock.ElapsedMilliseconds;
            long before = s.Writer.BytesWritten;
            s.Writer.WriteFragment(s.Pending, audio, nextTime);
            s.Pending.Clear();
            long ms = _clock.ElapsedMilliseconds - t;
            if (ms > MaxWriteMs) MaxWriteMs = ms;
            if (ms > 2000) Log("Recording: slow write (" + ms + " ms) - the storage may be too slow for this bitrate");
            _statBytes += s.Writer.BytesWritten - before;
            if (_statTicks == 0) _statTicks = _clock.ElapsedMilliseconds;
            long span = _clock.ElapsedMilliseconds - _statTicks;
            if (span >= 10000)
            {
                WriteMBps = _statBytes / 1048576.0 / (span / 1000.0);
                _statBytes = 0; _statTicks = _clock.ElapsedMilliseconds;
            }
            return Task.CompletedTask;
        }

        private async Task<bool> OpenAsync(long firstTime)
        {
            try
            {
                await EnsureFoldersAsync();
                string name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4";
                var file = await _loop.CreateFileAsync(name, CreationCollisionOption.GenerateUniqueName);
                // Others may read the file while it is written (downloads of the current recording).
                var ras = await file.OpenAsync(FileAccessMode.ReadWrite, StorageOpenOptions.AllowOnlyReaders);
                var stream = ras.AsStream(0); // no extra buffering: each fragment is one write
                var a = _audio;
                var writer = a != null && a.Config != null
                    ? new FragmentedMp4Writer(stream, _sps, _pps, firstTime, a.Config, a.SampleRate, a.Channels)
                    : new FragmentedMp4Writer(stream, _sps, _pps, firstTime);
                writer.WriteHeader();
                _seg = new Segment { File = file, Stream = stream, Writer = writer, Name = file.Name, Start = firstTime };
                if (_carryAudio != null && writer.HasAudio) _seg.Audio.AddRange(_carryAudio.Where(x => x.Time >= firstTime));
                _carryAudio = null;
                State = "recording";
                LastError = "";
                CurrentSeconds = 0;
                Log("Recording to " + Location + ": " + file.Name + " (" + writer.Width + " x " + writer.Height + (writer.HasAudio ? ", with sound" : "") + ")");
                if (DateTime.UtcNow >= _nextHousekeep) await HousekeepAsync();
                return true;
            }
            catch (Exception ex)
            {
                await FailAsync("Recording could not start a file: " + ex.Message);
                return false;
            }
        }

        /// <summary>Finishes the current file (writing what is pending) and adds it to the loop list or moves it to Saved.</summary>
        private async Task CloseAsync(long nextTime)
        {
            var s = _seg;
            if (s == null) return;
            _seg = null;
            try
            {
                await FlushAsync(nextTime);
                s.Writer.Finish();
            }
            catch (Exception ex) { Log("Recording: finishing " + s.Name + " failed: " + ex.Message); }
            try { s.Stream.Dispose(); } catch { }
            _carryAudio = nextTime >= 0 && s.Audio.Count > 0 ? new List<AudioSample>(s.Audio) : null;
            long size = s.Writer.BytesWritten;
            Log(string.Format("Recording: {0} done ({1:0.0} min, {2:0.0} MB)", s.Name, s.Writer.Duration / (double)Second / 60, size / 1048576.0));
            if (State == "recording") State = Enabled ? "starting the next file" : "off";
            if (s.Protect)
            {
                await SaveOrQueueAsync(s.File);
                if (_cutRequested)
                {
                    _cutRequested = false;
                    RaiseClip(true, _clipText ?? s.Name);
                }
            }
            else
            {
                _segments.Add(new RecordingInfo { Name = s.Name, Size = size });
                UpdateTotals();
            }
            _lastClosed = s.Protect ? null : s.Name;
            _lastClosedKnown = true;
        }

        /// <summary>2026-10-30_15-23-42.mp4 -> 15:23:42</summary>
        private static string ShortName(string name)
        {
            var m = Regex.Match(name ?? "", @"_(\d\d)-(\d\d)-(\d\d)");
            return m.Success ? m.Groups[1].Value + ":" + m.Groups[2].Value + ":" + m.Groups[3].Value : name;
        }

        private async Task SaveAsync()
        {
            if (_cutRequested && _seg != null) { Log("Save clip: already saving"); return; }
            await EnsureFoldersAsync();
            var kept = new List<string>();
            // The file just before the current one (the moments before) - only that one, and only once:
            // pressing again must not walk back through older files.
            var prev = _lastClosed == null ? null : _segments.FirstOrDefault(x => x.Name == _lastClosed);
            if (prev != null)
            {
                _lastClosed = null;
                try
                {
                    var f = await _loop.GetFileAsync(prev.Name);
                    var props = await f.GetBasicPropertiesAsync();
                    if (_seg != null || (DateTimeOffset.Now - props.DateModified).TotalMinutes < 10)
                    {
                        await SaveOrQueueAsync(f);
                        _segments.Remove(prev); // kept either way: moved now, or protected until the move succeeds
                        UpdateTotals();
                        kept.Add(prev.Name);
                    }
                }
                catch (Exception ex) { Log("Save clip: " + prev.Name + ": " + ex.Message); }
            }
            if (_seg != null)
            {
                // Finish the current file at the next keyframe (within ~2 s): it is saved, and a new file starts with no gap.
                _seg.Protect = true;
                _cutRequested = true;
                kept.Add(_seg.Name);
                _clipText = string.Join(" + ", kept.Select(ShortName));
                Log("Save clip: keeping " + string.Join(", ", kept) + " - the current file ends now and a new one starts");
            }
            else if (kept.Count > 0)
            {
                Log("Clip saved: " + string.Join(", ", kept) + " (not recording now)");
                RaiseClip(true, string.Join(" + ", kept.Select(ShortName)));
            }
            else
            {
                Log("Save clip: nothing recorded yet");
                RaiseClip(false, "Not recording - nothing to save");
            }
        }

        // ---- saved clips whose move is still pending: right after a file closes, the phone's media indexer
        // (library folders such as Videos) or a download can still have it open. Such a clip stays where it is,
        // is never loop-deleted, and the move is retried (also after an app restart: kept in the settings).
        private sealed class PendingSave { public string Folder, Name; public int Tries; public DateTime Next; }
        private List<PendingSave> _pending;
        private const string PendingKey = "RecPendingSaves";

        private void LoadPending()
        {
            if (_pending != null) return;
            _pending = new List<PendingSave>();
            try
            {
                var text = ApplicationData.Current.LocalSettings.Values[PendingKey] as string ?? "";
                foreach (var line in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int bar = line.LastIndexOf('|');
                    if (bar > 0) _pending.Add(new PendingSave { Folder = line.Substring(0, bar), Name = line.Substring(bar + 1), Next = DateTime.UtcNow });
                }
            }
            catch { }
        }

        private void StorePending()
        {
            try { ApplicationData.Current.LocalSettings.Values[PendingKey] = string.Join("\n", _pending.Select(x => x.Folder + "|" + x.Name)); }
            catch { }
        }

        private bool IsPending(string name) => _pending != null && _loop != null && _pending.Any(x => x.Name == name && x.Folder == _loop.Path);

        /// <summary>Moves a clip to Saved now, or queues the move if the file is still busy.</summary>
        private async Task SaveOrQueueAsync(StorageFile f)
        {
            LoadPending();
            try
            {
                await f.MoveAsync(_saved, f.Name, NameCollisionOption.GenerateUniqueName);
                SavedClips++;
            }
            catch (FileNotFoundException) { }
            catch (Exception ex)
            {
                Log("Saved clip " + f.Name + " is busy (" + ex.Message.Split('\r', '\n')[0] + ") - it is kept, and moved to Saved as soon as it is free");
                _pending.Add(new PendingSave { Folder = _loop.Path, Name = f.Name, Tries = 1, Next = DateTime.UtcNow.AddSeconds(2) });
                StorePending();
            }
        }

        private async Task RetryPendingAsync()
        {
            if (_pending == null || _pending.Count == 0 || _loop == null) return;
            var now = DateTime.UtcNow;
            foreach (var p in _pending.Where(x => x.Folder == _loop.Path && x.Next <= now).ToList())
            {
                try
                {
                    var f = await _loop.GetFileAsync(p.Name);
                    await f.MoveAsync(_saved, f.Name, NameCollisionOption.GenerateUniqueName);
                    SavedClips++;
                    _pending.Remove(p);
                    Log("Saved clip " + p.Name + " moved to Saved" + (p.Tries > 1 ? " (after " + p.Tries + " tries)" : ""));
                }
                catch (FileNotFoundException) { _pending.Remove(p); }
                catch (Exception)
                {
                    p.Tries++;
                    p.Next = now.AddSeconds(Math.Min(60, 2 << Math.Min(p.Tries, 5)));
                    if (p.Tries == 10) Log("Saved clip " + p.Name + " is still busy - keeps trying every minute (it stays protected)");
                }
            }
            StorePending();
        }

        private async Task FailAsync(string message)
        {
            if (_cutRequested) { _cutRequested = false; RaiseClip(false, "Clip not saved: " + message); }
            LastError = message;
            State = "error";
            Log(message);
            var s = _seg;
            _seg = null;
            if (s != null) { try { s.Stream.Dispose(); } catch { } _segments.Add(new RecordingInfo { Name = s.Name, Size = s.Writer.BytesWritten }); }
            _root = null; // look for the storage again (the card may have been removed)
            _retryAfter = DateTime.UtcNow.AddSeconds(10);
            await Task.CompletedTask;
        }

        private async Task IdleChecksAsync()
        {
            // No frames for a while (camera stopped or taken away): finish the file so it is complete.
            if (_seg != null && (DateTime.UtcNow - _lastFrameAt).TotalSeconds > 4)
            {
                Log("Recording: no video for 4 s - finishing the file");
                await CloseAsync(-1);
                _lastVideoTime = -1;
                State = Enabled ? "waiting for video" : "off";
            }
            if (_seg == null && _lastVideoTime < 0) State = Enabled ? (string.IsNullOrEmpty(LastError) ? "waiting for video" : "error") : "off";
            if (_root != null) await RetryPendingAsync();
            if (_root != null && DateTime.UtcNow >= _nextHousekeep) await HousekeepAsync();
        }

        // ================================================================ storage

        private async Task EnsureFoldersAsync()
        {
            if (_root != null) return;
            string mode = StorageMode ?? "auto";
            StorageFolder root = null;
            string where = null, note = "";
            if (mode == "custom")
            {
                try
                {
                    var list = Windows.Storage.AccessCache.StorageApplicationPermissions.FutureAccessList;
                    if (list.ContainsItem(CustomFolderToken))
                    {
                        root = await list.GetFolderAsync(CustomFolderToken);
                        CustomFolderPath = root.Path;
                        where = "chosen folder (" + root.Path + ")";
                    }
                    else note = " - no folder chosen yet";
                }
                catch (Exception ex) { note = " - the chosen folder is not available (" + ex.Message + ")"; }
            }
            if (root == null && (mode == "auto" || mode == "sd"))
            {
                try
                {
                    var cards = await KnownFolders.RemovableDevices.GetFoldersAsync();
                    if (cards.Count > 0)
                    {
                        root = await cards[0].CreateFolderAsync("W10MDashcam", CreationCollisionOption.OpenIfExists);
                        where = "SD card (" + root.Path + ")";
                    }
                    else if (mode == "sd") note = " - no SD card found";
                }
                catch (Exception ex) { note = " - SD card not usable (" + ex.Message + ")"; }
            }
            if (root == null)
            {
                root = await ApplicationData.Current.LocalFolder.CreateFolderAsync("Recordings", CreationCollisionOption.OpenIfExists);
                where = "phone storage" + (mode == "phone" ? "" : note.Length > 0 ? note : " (no SD card)");
                if (note.Length > 0) Log("Recordings:" + note + " - using phone storage instead");
            }
            _loop = root;
            _saved = await root.CreateFolderAsync("Saved", CreationCollisionOption.OpenIfExists);
            _segments.Clear();
            LoadPending();
            foreach (var f in (await _loop.GetFilesAsync()).Where(x => OwnName.IsMatch(x.Name)).OrderBy(x => x.Name))
            {
                if (_seg != null && f.Name == _seg.Name) continue;
                if (IsPending(f.Name)) continue; // a saved clip waiting to be moved: never loop-deleted
                var p = await f.GetBasicPropertiesAsync();
                _segments.Add(new RecordingInfo { Name = f.Name, Size = (long)p.Size });
            }
            if (!_lastClosedKnown) { _lastClosedKnown = true; _lastClosed = _segments.LastOrDefault()?.Name; }
            SavedClips = (await _saved.GetFilesAsync()).Count(x => x.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
            _root = root;
            if (Location != where) Log("Recordings: " + where + ", " + _segments.Count + " loop files, " + SavedClips + " saved");
            Location = where;
            UpdateTotals();
            FreeBytes = await GetFreeSpaceAsync(_root);
        }

        private void UpdateTotals()
        {
            LoopCount = _segments.Count;
            LoopBytes = _segments.Sum(x => x.Size);
        }

        /// <summary>Loop recording: delete the oldest files while over the limit or short of free space.</summary>
        private async Task HousekeepAsync()
        {
            _nextHousekeep = DateTime.UtcNow.AddSeconds(60);
            FreeBytes = await GetFreeSpaceAsync(_root);
            long current = _seg?.Writer.BytesWritten ?? 0;
            long used = LoopBytes + current;
            // keep room for the file being written to reach its full length
            long headroom = _seg != null ? Math.Max(0, _seg.Writer.BytesWritten * SegmentSeconds / Math.Max(1, (long)CurrentSeconds + 1) - current) : 0;
            int deleted = 0;
            long freed = 0;
            while (_segments.Count > 0 && (used > LimitBytes || (FreeBytes >= 0 && FreeBytes - headroom < MinFreeBytes)))
            {
                var old = _segments[0];
                _segments.RemoveAt(0);
                try
                {
                    var f = await _loop.GetFileAsync(old.Name);
                    await f.DeleteAsync(StorageDeleteOption.PermanentDelete);
                }
                catch (FileNotFoundException) { }
                catch (Exception ex) { Log("Loop delete of " + old.Name + " failed: " + ex.Message); }
                used -= old.Size;
                if (FreeBytes >= 0) FreeBytes += old.Size;
                freed += old.Size;
                deleted++;
            }
            UpdateTotals();
            if (deleted > 0) Log(string.Format("Loop recording: deleted {0} oldest file(s), {1:0} MB", deleted, freed / 1048576.0));
            if (FreeBytes >= 0 && FreeBytes < MinFreeBytes / 2 && _segments.Count == 0 && _seg != null)
            {
                await CloseAsync(-1);
                await FailAsync("Recording stopped: the storage is full (only saved clips left). Delete some saved clips.");
                _retryAfter = DateTime.UtcNow.AddSeconds(60);
            }
        }

        private static async Task<long> GetFreeSpaceAsync(StorageFolder folder)
        {
            try
            {
                var props = await folder.Properties.RetrievePropertiesAsync(new[] { "System.FreeSpace" });
                if (props.TryGetValue("System.FreeSpace", out object v) && v != null) return (long)Convert.ToUInt64(v);
            }
            catch { }
            return -1;
        }

        // ================================================================ web access (any thread)

        public async Task<IList<RecordingInfo>> ListAsync()
        {
            var list = new List<RecordingInfo>();
            var loop = _loop; var saved = _saved;
            if (loop == null) return list;
            string current = CurrentName;
            foreach (var pair in new[] { Tuple.Create(loop, false), Tuple.Create(saved, true) })
            {
                foreach (var f in await pair.Item1.GetFilesAsync())
                {
                    if (!f.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || (!pair.Item2 && !OwnName.IsMatch(f.Name))) continue;
                    BasicProperties p = await f.GetBasicPropertiesAsync();
                    list.Add(new RecordingInfo { Name = f.Name, Size = (long)p.Size, Saved = pair.Item2, Recording = !pair.Item2 && f.Name == current });
                }
            }
            return list.OrderByDescending(x => x.Name).ToList();
        }

        /// <summary>Opens a recording for reading (also the one being written). Null if the name is unknown.</summary>
        public async Task<Stream> OpenReadAsync(string name, bool saved)
        {
            if (!SafeName.IsMatch(name ?? "")) return null;
            var folder = saved ? _saved : _loop;
            if (folder == null) return null;
            try
            {
                var f = await folder.GetFileAsync(name);
                var ras = await f.OpenAsync(FileAccessMode.Read, StorageOpenOptions.AllowReadersAndWriters);
                return ras.AsStream(0);
            }
            catch (FileNotFoundException) { return null; }
        }

        /// <summary>Deletes a recording (not the one being written). Returns an error text, or null when done.</summary>
        public async Task<string> DeleteAsync(string name, bool saved)
        {
            if (!SafeName.IsMatch(name ?? "")) return "bad name";
            if (!saved && name == CurrentName) return "that file is being recorded";
            if (!saved && !OwnName.IsMatch(name)) return "not a recording";
            var folder = saved ? _saved : _loop;
            if (folder == null) return "no recordings";
            try
            {
                var f = await folder.GetFileAsync(name);
                await f.DeleteAsync(StorageDeleteOption.PermanentDelete);
                if (saved) SavedClips = Math.Max(0, SavedClips - 1);
                else Housekeep(); // the worker refreshes its list
                Log("Deleted recording " + (saved ? "Saved\\" : "") + name);
                return null;
            }
            catch (FileNotFoundException) { return "not found"; }
        }
    }
}
