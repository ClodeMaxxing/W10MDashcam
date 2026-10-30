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
    /// them to fragmented MP4 files in segments, on the SD card when there is one (otherwise the app's own storage):
    ///   W10MDashcam\2026-10-30_13-40-05.mp4        loop recordings; the oldest are deleted to stay within the limit
    ///   W10MDashcam\Saved\...                      clips kept with "Save clip"; never deleted automatically
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

        private const long MaxQueueBytes = 48L << 20;     // ~16 s at 24 Mbps; ~100 s at 4 Mbps
        private const long Second = 10000000L;            // 100 ns units
        private const long MaxFragment = 2 * Second;      // a fragment is written at least this often
        private static readonly Regex SafeName = new Regex(@"^[0-9A-Za-z_\-\. ()]{1,80}\.mp4$");

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
        private enum Cmd { End, Save, Shutdown, Housekeep }
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
                if (f.Time - _seg.Start >= SegmentSeconds * Second)
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
            if (s.Protect) await MoveToSavedAsync(s.File);
            else
            {
                _segments.Add(new RecordingInfo { Name = s.Name, Size = size });
                UpdateTotals();
            }
        }

        private async Task SaveAsync()
        {
            await EnsureFoldersAsync();
            var kept = new List<string>();
            if (_segments.Count > 0)
            {
                // the previous file (the moments before) - unless it ended long ago
                var prev = _segments[_segments.Count - 1];
                try
                {
                    var f = await _loop.GetFileAsync(prev.Name);
                    var props = await f.GetBasicPropertiesAsync();
                    if (_seg != null || (DateTimeOffset.Now - props.DateModified).TotalMinutes < 10)
                    {
                        if (await MoveToSavedAsync(f)) { _segments.Remove(prev); UpdateTotals(); kept.Add(prev.Name); }
                    }
                }
                catch (Exception ex) { Log("Save clip: " + prev.Name + ": " + ex.Message); }
            }
            if (_seg != null) { _seg.Protect = true; kept.Add(_seg.Name + " (kept when it ends)"); }
            Log(kept.Count > 0 ? "Clip saved: " + string.Join(", ", kept) : "Save clip: nothing recorded yet");
        }

        private async Task<bool> MoveToSavedAsync(StorageFile f)
        {
            try
            {
                await f.MoveAsync(_saved, f.Name, NameCollisionOption.GenerateUniqueName);
                SavedClips++;
                return true;
            }
            catch (Exception ex) { Log("Moving " + f.Name + " to Saved failed: " + ex.Message); return false; }
        }

        private async Task FailAsync(string message)
        {
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
            if (_root != null && DateTime.UtcNow >= _nextHousekeep) await HousekeepAsync();
        }

        // ================================================================ storage

        private async Task EnsureFoldersAsync()
        {
            if (_root != null) return;
            StorageFolder root = null;
            string where = null;
            try
            {
                var cards = await KnownFolders.RemovableDevices.GetFoldersAsync();
                if (cards.Count > 0)
                {
                    root = await cards[0].CreateFolderAsync("W10MDashcam", CreationCollisionOption.OpenIfExists);
                    where = "SD card (" + root.Path + ")";
                }
            }
            catch (Exception ex) { Log("SD card not usable: " + ex.Message); }
            if (root == null)
            {
                root = await ApplicationData.Current.LocalFolder.CreateFolderAsync("Recordings", CreationCollisionOption.OpenIfExists);
                where = "phone storage (no SD card)";
            }
            _loop = root;
            _saved = await root.CreateFolderAsync("Saved", CreationCollisionOption.OpenIfExists);
            _segments.Clear();
            foreach (var f in (await _loop.GetFilesAsync()).Where(x => x.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Name))
            {
                if (_seg != null && f.Name == _seg.Name) continue;
                var p = await f.GetBasicPropertiesAsync();
                _segments.Add(new RecordingInfo { Name = f.Name, Size = (long)p.Size });
            }
            SavedClips = (await _saved.GetFilesAsync()).Count;
            _root = root;
            if (Location != where) Log("Recordings: " + where + ", " + _segments.Count + " loop files, " + SavedClips + " saved");
            Location = where;
            UpdateTotals();
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
                    if (!f.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) continue;
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
