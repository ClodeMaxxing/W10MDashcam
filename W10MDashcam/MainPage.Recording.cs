using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Foundation.Metadata;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using W10MDashcam.Recording;

namespace W10MDashcam
{
    /// <summary>
    /// Dashcam recording: the Record / Live buttons, the start-up switches, where recordings go, the camera button,
    /// web/USB config keys and status.
    ///
    /// Recording and live view are separate: the camera runs while either is on (CameraWanted) and stops when both are off.
    /// </summary>
    public sealed partial class MainPage
    {
        private readonly SegmentRecorder _recorder = new SegmentRecorder();
        private bool _loadingRecUi = true;
        /// <summary>Live view (RTSP) wanted. Off: the camera may still run for recording, but nothing is streamed.</summary>
        private bool _wantLive = true;

        private static readonly int[] SegmentMinutesOptions = { 1, 3, 5, 10 };
        private static readonly int[] LimitGbOptions = { 4, 8, 16, 32, 64, 0 }; // 0 = all free space (keeping 1 GB)
        private static readonly string[] LocationModes = { "auto", "phone", "sd", "custom" };

        private sealed class ChoiceOption
        {
            public int Value;
            public string Text;
            public override string ToString() => Text;
        }

        private bool CameraWanted => _wantLive || _recorder.Enabled;
        private bool RecordOnOpen => !(_settings.Values["RecAutoStart"] is bool r) || r;
        private bool LiveOnOpen => !(_settings.Values["AutoStart"] is bool a) || a;

        /// <summary>Constructor part: saved settings into the recorder and the controls.</summary>
        private void LoadRecordingSettings()
        {
            _recorder.Log = Log;
            _recorder.Enabled = false; // switched on by the start-up logic (Record when the app opens) or the button
            int seg = _settings.Values["RecSegmentMin"] is int sm ? sm : 3;
            int gb = _settings.Values["RecLimitGB"] is int lg ? lg : 16;
            SetRecorderLimits(seg, gb);
            string mode = _settings.Values["RecLocation"] as string;
            _recorder.StorageMode = Array.IndexOf(LocationModes, mode) >= 0 ? mode : "auto";

            foreach (var m in SegmentMinutesOptions) SegmentBox.Items.Add(new ChoiceOption { Value = m, Text = m + " min" });
            foreach (var g in LimitGbOptions) LimitBox.Items.Add(new ChoiceOption { Value = g, Text = g == 0 ? "All free space (keep 1 GB)" : g + " GB" });
            RebuildLocationBox();
            RecordOnOpenToggle.IsOn = RecordOnOpen;
            SelectChoice(SegmentBox, seg);
            SelectChoice(LimitBox, gb);
            _loadingRecUi = false;
            HookCameraButton();
            _recorder.ClipResult += (ok, text) => { var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => OnClipResult(ok, text)); };
            LoadSafetySettings();
        }

        private void SetRecorderLimits(int segmentMin, int limitGb)
        {
            _recorder.SegmentSeconds = Math.Max(1, Math.Min(30, segmentMin)) * 60;
            _recorder.LimitBytes = limitGb <= 0 ? long.MaxValue : (long)limitGb << 30;
        }

        private static void SelectChoice(ComboBox box, int value)
        {
            foreach (var o in box.Items)
                if (o is ChoiceOption c && c.Value == value) { box.SelectedItem = o; return; }
        }

        private int SegmentMinutes => _recorder.SegmentSeconds / 60;
        private int LimitGb => _recorder.LimitBytes == long.MaxValue ? 0 : (int)(_recorder.LimitBytes >> 30);

        /// <summary>The camera's encoded output goes to the recorder as well as to the RTSP server.</summary>
        private void WireRecorder(Streaming.CameraEngine cam)
        {
            cam.AccessUnitReady += _recorder.OnAccessUnit;
            cam.ParameterSetsReady += _recorder.OnParameterSets;
            cam.AudioFrameReady += _recorder.OnAudio;
            cam.AudioFormatReady += _recorder.OnAudioFormat;
        }

        /// <summary>At app start: what the two start-up switches ask for (nothing after a crash while starting).</summary>
        private void ApplyStartupWishes(bool crashedLastTime)
        {
            _recorder.Enabled = RecordOnOpen && !crashedLastTime;
            SetLive(LiveOnOpen && !crashedLastTime);
            if (_gpsOn && !_gps.Running) { var _ = _gps.StartAsync(); } // e.g. location was just allowed in Settings
        }

        // ---------------------------------------------------------------- record / live

        /// <summary>Starts or stops the camera so it runs exactly while recording or live view is wanted.</summary>
        private async Task MatchCameraAsync()
        {
            // a start/stop already in progress: wait for it (up to 30 s), then check again
            for (int i = 0; i < 60 && _busy; i++) await Task.Delay(500);
            if (_camera == null || _busy) return;
            if (CameraWanted != _camera.IsStreaming) await ToggleStreamingAsync();
            UpdateRunButtons();
        }

        private void SetRecording(bool on)
        {
            if (_recorder.Enabled == on) return;
            _recorder.Enabled = on;
            if (!on) _recorder.EndSegment();
            Log("Recording " + (on ? "started" : "stopped"));
        }

        private void SetLive(bool on)
        {
            _wantLive = on;
            _server.LiveEnabled = on;
            if (!on) _server.DropViewers();
        }

        private async void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            SetRecording(!_recorder.Enabled);
            await MatchCameraAsync();
        }

        private async void LiveButton_Click(object sender, RoutedEventArgs e)
        {
            SetLive(!_wantLive);
            Log("Live view " + (_wantLive ? "on" : "off") + (_recorder.Enabled ? " (recording continues)" : ""));
            await MatchCameraAsync();
        }

        private void UpdateRunButtons()
        {
            RecordButton.Content = _recorder.Enabled ? "Stop recording" : "Start recording";
            StartButton.Content = _wantLive ? "Stop streaming" : "Start streaming";
        }

        // ---------------------------------------------------------------- camera button

        /// <summary>The phone's camera button saves the clip (full press), with a short buzz to confirm.</summary>
        private void HookCameraButton()
        {
            try
            {
                if (!ApiInformation.IsTypePresent("Windows.Phone.UI.Input.HardwareButtons")) return;
                Windows.Phone.UI.Input.HardwareButtons.CameraPressed += OnCameraButton;
                Log("Camera button: press it to save the clip");
            }
            catch (Exception ex) { Log("Camera button not available: " + ex.Message); }
        }

        private void OnCameraButton(object sender, Windows.Phone.UI.Input.CameraEventArgs e)
        {
            var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => RequestSaveClip("camera button"));
        }

        // ---------------------------------------------------------------- Save clip feedback

        private bool _clipSaving;
        private DateTime _clipSavingSince;
        private bool _clipOk;
        private string _clipText = "";
        private DateTime _clipShowUntil = DateTime.MinValue;
        private MediaPlayer _chime;

        /// <summary>
        /// Save clip from the on-screen button or the camera button: a buzz at once, "SAVING..." on the button, then -
        /// when the current file has been finished (next keyframe, within ~2 s) - a chime and "CLIP SAVED".
        /// The camera keeps running: the next file starts with no gap.
        /// </summary>
        private string _clipReason = "button";

        private async void RequestSaveClip(string from, string reason = "button", bool forceKeepNext = false)
        {
            try
            {
                if (_recorder.State != "recording")
                {
                    Log("Save clip (" + from + "): not recording - nothing to save");
                    OnClipResult(false, "Not recording - nothing to save");
                    await BuzzAsync(2);
                    return;
                }
                if (!_clipSaving || forceKeepNext) // an impact during a pending save still adds "keep the next file"
                {
                    _clipSaving = true;
                    _clipSavingSince = DateTime.Now;
                    _clipReason = reason;
                    _recorder.SaveClip(reason, forceKeepNext);
                }
                UpdateRecordingUi();
                await BuzzAsync(1);
            }
            catch (Exception ex) { Log("Save clip: " + ex.Message); }
        }

        private void OnClipResult(bool ok, string text)
        {
            _clipSaving = false;
            _clipOk = ok;
            bool impact = _clipReason.StartsWith("impact", StringComparison.Ordinal);
            _clipText = ok ? (impact ? "IMPACT (" + _clipReason.Substring(7) + ") - " : "") + "Clip saved (" + text + ") - a new file has started" : text;
            _clipReason = "button";
            _clipShowUntil = DateTime.Now.AddSeconds(5);
            PlayChime(ok);
            UpdateRecordingUi();
        }

        /// <summary>A short sound from the app's own files (media volume).</summary>
        private void PlayChime(bool ok)
        {
            try
            {
                if (_chime == null) _chime = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.SoundEffects, Volume = 1.0 };
                _chime.Source = MediaSource.CreateFromUri(new Uri(ok ? "ms-appx:///Assets/ClipSaved.wav" : "ms-appx:///Assets/ClipNone.wav"));
                _chime.Play();
            }
            catch (Exception ex) { Log("Chime: " + ex.Message); }
        }

        private void UpdateSaveButton(bool recording)
        {
            if (_clipSaving && (DateTime.Now - _clipSavingSince).TotalSeconds > 8) _clipSaving = false; // never stick
            bool showing = DateTime.Now < _clipShowUntil;
            if (_clipSaving)
            {
                SaveClipButton.Content = "SAVING...";
                SaveClipButton.Background = Brush(0x5A, 0x4A, 0x10);
                SaveClipButton.IsEnabled = false;
            }
            else if (showing)
            {
                SaveClipButton.Content = _clipOk ? "CLIP SAVED \u2713" : "NOTHING SAVED";
                SaveClipButton.Background = _clipOk ? Brush(0x14, 0x5A, 0x20) : Brush(0x33, 0x33, 0x33);
                SaveClipButton.IsEnabled = recording;
            }
            else
            {
                SaveClipButton.Content = "SAVE CLIP";
                SaveClipButton.Background = Brush(0x5A, 0x10, 0x10);
                SaveClipButton.IsEnabled = recording;
            }
        }

        private static async Task BuzzAsync(int times)
        {
            try
            {
                if (!ApiInformation.IsTypePresent("Windows.Phone.Devices.Notification.VibrationDevice")) return;
                var v = Windows.Phone.Devices.Notification.VibrationDevice.GetDefault();
                if (v == null) return;
                for (int i = 0; i < times; i++)
                {
                    v.Vibrate(TimeSpan.FromMilliseconds(times == 1 ? 250 : 120));
                    await Task.Delay(times == 1 ? 250 : 260);
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- controls

        private void SaveClipButton_Click(object sender, RoutedEventArgs e) => RequestSaveClip("button");

        private void RecordOnOpenToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingRecUi) return;
            _settings.Values["RecAutoStart"] = RecordOnOpenToggle.IsOn;
        }

        private void RecSetting_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingRecUi) return;
            int seg = (SegmentBox.SelectedItem as ChoiceOption)?.Value ?? SegmentMinutes;
            int gb = (LimitBox.SelectedItem as ChoiceOption)?.Value ?? LimitGb;
            ApplyRecLimits(seg, gb);
        }

        private void ApplyRecLimits(int seg, int gb)
        {
            SetRecorderLimits(seg, gb);
            _settings.Values["RecSegmentMin"] = SegmentMinutes;
            _settings.Values["RecLimitGB"] = LimitGb;
            _recorder.Housekeep();
            Log("Recording: " + SegmentMinutes + " min files, keep " + (LimitGb == 0 ? "all free space (1 GB spare)" : "up to " + LimitGb + " GB"));
        }

        // ---------------------------------------------------------------- where recordings go

        private bool HasCustomFolder => StorageApplicationPermissions.FutureAccessList.ContainsItem(SegmentRecorder.CustomFolderToken);

        private void RebuildLocationBox()
        {
            bool was = _loadingRecUi;
            _loadingRecUi = true;
            LocationBox.Items.Clear();
            LocationBox.Items.Add(new ChoiceOption { Value = 0, Text = "Automatic (SD card if present)" });
            LocationBox.Items.Add(new ChoiceOption { Value = 1, Text = "Phone storage" });
            LocationBox.Items.Add(new ChoiceOption { Value = 2, Text = "SD card" });
            if (HasCustomFolder) LocationBox.Items.Add(new ChoiceOption { Value = 3, Text = "Chosen folder" });
            SelectChoice(LocationBox, Array.IndexOf(LocationModes, _recorder.StorageMode));
            _loadingRecUi = was;
        }

        private void LocationBox_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingRecUi || !(LocationBox.SelectedItem is ChoiceOption c)) return;
            SetLocation(LocationModes[c.Value]);
        }

        private void SetLocation(string mode)
        {
            if (_recorder.StorageMode == mode) return;
            _recorder.StorageMode = mode;
            _settings.Values["RecLocation"] = mode;
            _recorder.Relocate();
            Log("Recordings will be saved to: " + mode);
        }

        /// <summary>Opens the phone's folder picker; the folder is remembered (future-access list) and used from now on.</summary>
        private async void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
                picker.FileTypeFilter.Add(".mp4");
                var folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;
                StorageApplicationPermissions.FutureAccessList.AddOrReplace(SegmentRecorder.CustomFolderToken, folder);
                Log("Recording folder chosen: " + folder.Path);
                RebuildLocationBox();
                _recorder.StorageMode = "custom"; // force the move even if "custom" was already selected
                _settings.Values["RecLocation"] = "custom";
                _recorder.Relocate();
                RebuildLocationBox();
            }
            catch (Exception ex) { Log("Choosing a folder failed: " + ex.Message); }
        }

        /// <summary>Once a second, with the other stats.</summary>
        private void UpdateRecordingUi()
        {
            var r = _recorder;
            bool rec = r.State == "recording";
            RecDot.Fill = new Windows.UI.Xaml.Media.SolidColorBrush(rec ? Windows.UI.Color.FromArgb(255, 0xFF, 0x30, 0x30)
                : r.State == "error" ? Windows.UI.Color.FromArgb(255, 0xFF, 0xA0, 0x20) : Windows.UI.Color.FromArgb(255, 0x55, 0x55, 0x55));
            RecStateText.Text = rec ? "REC " + TimeSpan.FromSeconds(Math.Floor(r.CurrentSeconds)).ToString(@"m\:ss")
                : !r.Enabled ? "NOT RECORDING" : r.State.ToUpperInvariant();
            RecInfoText.Text = DateTime.Now < _clipShowUntil ? _clipText : RecordingSummary();
            UpdateSaveButton(rec);
            LocationText.Text = string.IsNullOrEmpty(r.Location) ? "Decided when recording starts" : "Now: " + r.Location;
            UpdateRunButtons();
            UpdateSafetyUi();
        }

        private string RecordingSummary()
        {
            var r = _recorder;
            if (!string.IsNullOrEmpty(r.LastError) && r.State == "error") return r.LastError;
            if (!string.IsNullOrEmpty(r.Warning)) return "WARNING: " + r.Warning;
            if (string.IsNullOrEmpty(r.Location)) return r.Enabled ? "Starts with the camera" : "Recording is stopped";
            string s = string.Format(CultureInfo.InvariantCulture, "Saved clips: {0}  -  loop: {1} older file{2} ({3:0.0} GB)",
                r.SavedClips, r.LoopCount, r.LoopCount == 1 ? "" : "s", r.LoopBytes / 1073741824.0);
            if (r.FreeBytes >= 0) s += string.Format(CultureInfo.InvariantCulture, "  -  {0:0.0} GB free", r.FreeBytes / 1073741824.0);
            if (r.DroppedFrames > 0) s += " - " + r.DroppedFrames + " frames dropped (storage too slow)";
            return s;
        }

        // ---------------------------------------------------------------- web / USB config

        /// <summary>
        /// record=on|off (now), recordonopen=on|off, live=on|off (now), liveonopen=on|off, segment=MINUTES,
        /// storage=GB|all, location=auto|phone|sd|custom, saveclip=1. Runs on a network thread.
        /// </summary>
        private async Task ApplyRecordingConfigAsync(IDictionary<string, string> form, List<string> done)
        {
            await ApplySafetyConfigAsync(form, done);
            bool uiChange = false, matchCamera = false;
            if (form.TryGetValue("record", out var rec))
            {
                SetRecording(IsOn(rec));
                done.Add("recording " + (_recorder.Enabled ? "on" : "off"));
                matchCamera = true;
            }
            if (form.TryGetValue("live", out var live))
            {
                SetLive(IsOn(live));
                done.Add("live view " + (_wantLive ? "on" : "off"));
                matchCamera = true;
            }
            if (form.TryGetValue("recordonopen", out var roo))
            {
                _settings.Values["RecAutoStart"] = IsOn(roo);
                done.Add("record when the app opens " + (RecordOnOpen ? "on" : "off"));
                uiChange = true;
            }
            if (form.TryGetValue("liveonopen", out var loo))
            {
                _settings.Values["AutoStart"] = IsOn(loo);
                done.Add("live view when the app opens " + (LiveOnOpen ? "on" : "off"));
                uiChange = true;
            }
            int seg = SegmentMinutes, gb = LimitGb;
            bool limits = false;
            if (form.TryGetValue("segment", out var sg) && int.TryParse(sg, out int sgv) && sgv >= 1 && sgv <= 30) { seg = sgv; limits = true; }
            if (form.TryGetValue("storage", out var st))
            {
                if (st.Equals("all", StringComparison.OrdinalIgnoreCase) || st == "0") { gb = 0; limits = true; }
                else if (int.TryParse(st, out int gbv) && gbv >= 1 && gbv <= 1024) { gb = gbv; limits = true; }
            }
            if (limits)
            {
                ApplyRecLimits(seg, gb);
                done.Add("recording files " + SegmentMinutes + " min, storage " + (LimitGb == 0 ? "all" : LimitGb + " GB"));
                uiChange = true;
            }
            if (form.TryGetValue("location", out var loc))
            {
                loc = loc.Trim().ToLowerInvariant();
                if (Array.IndexOf(LocationModes, loc) < 0) done.Add("location: use auto, phone, sd or custom");
                else if (loc == "custom" && !HasCustomFolder) done.Add("location: choose a folder on the phone first (Recording > Choose folder)");
                else { SetLocation(loc); done.Add("recordings go to " + loc); uiChange = true; }
            }
            if (form.TryGetValue("saveclip", out var sc) && IsOn(sc))
            {
                _recorder.SaveClip();
                done.Add("clip saved");
            }
            if (uiChange || matchCamera)
                await RunUiAsync(async () =>
                {
                    _loadingRecUi = true;
                    RecordOnOpenToggle.IsOn = RecordOnOpen;
                    AutoStartToggle.IsOn = LiveOnOpen;
                    SelectChoice(SegmentBox, SegmentMinutes);
                    SelectChoice(LimitBox, LimitGb);
                    _loadingRecUi = false;
                    RebuildLocationBox();
                    if (matchCamera) await MatchCameraAsync();
                    else UpdateRunButtons();
                });
        }

        private string RecordingConfigText() =>
            "record=" + (_recorder.Enabled ? "on" : "off") + " (" + _recorder.State + "), recordonopen=" + (RecordOnOpen ? "on" : "off") + "\n" +
            "live=" + (_wantLive ? "on" : "off") + ", liveonopen=" + (LiveOnOpen ? "on" : "off") + "\n" +
            "location=" + _recorder.StorageMode + " (now: " + _recorder.Location + ")\n" +
            "segment=" + SegmentMinutes + " (minutes per file)\n" +
            "storage=" + (LimitGb == 0 ? "all" : LimitGb.ToString()) + " (GB the loop recordings may use; all = all free space but 1 GB)\n" +
            SafetyConfigText();

        private const string RecordingConfigHelp = ", record=on|off, recordonopen=on|off, live=on|off, liveonopen=on|off, location=auto|phone|sd|custom, segment=MINUTES, storage=GB|all, saveclip=1, impact=off|low|medium|high, keepnext=on|off, savedlimit=GB|none, replacesaved=on|off, gps=on|off, units=auto|kmh|mph, subtitles=on|off";

        private void AddRecordingJson(JsonObject o)
        {
            var r = _recorder;
            o["live"] = JsonValue.CreateBooleanValue(_wantLive);
            o["liveOnOpen"] = JsonValue.CreateBooleanValue(LiveOnOpen);
            var j = new JsonObject();
            j["enabled"] = JsonValue.CreateBooleanValue(r.Enabled);
            j["onOpen"] = JsonValue.CreateBooleanValue(RecordOnOpen);
            j["state"] = JsonValue.CreateStringValue(r.State);
            j["mode"] = JsonValue.CreateStringValue(r.StorageMode);
            j["customFolder"] = JsonValue.CreateBooleanValue(HasCustomFolder);
            j["location"] = JsonValue.CreateStringValue(r.Location);
            j["current"] = JsonValue.CreateStringValue(r.CurrentName);
            j["seconds"] = JsonValue.CreateNumberValue(Math.Floor(r.CurrentSeconds));
            j["segmentMin"] = JsonValue.CreateNumberValue(SegmentMinutes);
            j["limitGB"] = JsonValue.CreateNumberValue(LimitGb);
            j["files"] = JsonValue.CreateNumberValue(r.LoopCount);
            j["usedMB"] = JsonValue.CreateNumberValue(Math.Round(r.LoopBytes / 1048576.0));
            j["freeMB"] = r.FreeBytes >= 0 ? JsonValue.CreateNumberValue(Math.Round(r.FreeBytes / 1048576.0)) : JsonValue.CreateNullValue();
            j["saved"] = JsonValue.CreateNumberValue(r.SavedClips);
            j["dropped"] = JsonValue.CreateNumberValue(r.DroppedFrames);
            j["writeMBps"] = JsonValue.CreateNumberValue(Math.Round(r.WriteMBps, 2));
            j["maxWriteMs"] = JsonValue.CreateNumberValue(r.MaxWriteMs);
            j["error"] = JsonValue.CreateStringValue(r.LastError);
            AddSafetyJson(j);
            o["recording"] = j;
        }
    }
}
