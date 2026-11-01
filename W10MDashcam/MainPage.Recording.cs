using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Foundation.Metadata;
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
        private static readonly int[] LimitGbOptions = { -1, 4, 8, 16, 32, 64, 0 }; // -1 = automatic, 0 = all free space (keeping the reserve)
        private static readonly string[] LocationModes = { "auto", "phone", "sd", "custom" };

        private sealed class ChoiceOption
        {
            public int Value;
            public string Text;
            public override string ToString() => Text;
        }

        private bool CameraWanted => _wantLive || _recorder.Enabled;
        private bool RecordOnOpen => !(_settings.Values["RecAutoStart"] is bool r) || r;
        private bool LiveOnOpen => _settings.Values[LiveOnOpenKey] is bool a && a; // off by default: the app is a dashcam first
        /// <summary>"Live stream when the app opens". Saved as "AutoStart" before 0.8.1 (a name from W10MSecurityCam, where
        /// it meant streaming on open); MigrateSettingKeys moves it once.</summary>
        private const string LiveOnOpenKey = "LiveOnOpen";

        /// <summary>Constructor part: saved settings into the recorder and the controls.</summary>
        /// <summary>Renamed settings keys, moved once: "AutoStart" (W10MSecurityCam's name) is now "LiveOnOpen".</summary>
        private void MigrateSettingKeys()
        {
            if (_settings.Values["AutoStart"] is bool old)
            {
                if (!(_settings.Values[LiveOnOpenKey] is bool)) _settings.Values[LiveOnOpenKey] = old;
                _settings.Values.Remove("AutoStart");
            }
        }

        private void LoadRecordingSettings()
        {
            MigrateSettingKeys();
            _recorder.Log = Log;
            _recorder.Enabled = false; // switched on by the start-up logic (Record when the app opens) or the button
            int seg = _settings.Values["RecSegmentMin"] is int sm ? sm : 3;
            int gb = _settings.Values["RecLimitGB"] is int lg ? lg : -1; // new installs: automatic
            SetRecorderLimits(seg, gb);
            string mode = _settings.Values["RecLocation"] as string;
            _recorder.StorageMode = Array.IndexOf(LocationModes, mode) >= 0 ? mode : "auto";

            foreach (var m in SegmentMinutesOptions) SegmentBox.Items.Add(new ChoiceOption { Value = m, Text = m + " min" });
            foreach (var g in LimitGbOptions)
                LimitBox.Items.Add(new ChoiceOption { Value = g, Text = g < 0 ? "Automatic (fits the storage)" : g == 0 ? "All free space (keep a reserve)" : g + " GB" });
            RebuildLocationBox();
            RecordOnOpenToggle.IsOn = RecordOnOpen;
            SelectChoice(SegmentBox, seg);
            SelectChoice(LimitBox, gb);
            _loadingRecUi = false;
            HookCameraButton();
            _recorder.ClipResult += (ok, text) => { var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => OnClipResult(ok, text)); };
            LoadSafetySettings();
            LoadPowerLossSettings();
            LoadServerSettings();
        }

        private void SetRecorderLimits(int segmentMin, int limitGb)
        {
            _recorder.SegmentSeconds = Math.Max(1, Math.Min(30, segmentMin)) * 60;
            _recorder.AutoLoop = limitGb < 0; // automatic: the recorder works the limit out from the storage
            if (limitGb >= 0) _recorder.LimitBytes = limitGb == 0 ? long.MaxValue : (long)limitGb << 30;
        }

        private static void SelectChoice(ComboBox box, int value)
        {
            foreach (var o in box.Items)
                if (o is ChoiceOption c && c.Value == value) { box.SelectedItem = o; return; }
        }

        private int SegmentMinutes => _recorder.SegmentSeconds / 60;
        private int LimitGb => _recorder.AutoLoop ? -1 : _recorder.LimitBytes == long.MaxValue ? 0 : (int)(_recorder.LimitBytes >> 30);

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
            if (on && _recorder.Halted) { _recorder.Resume(); return; } // recording had stopped by itself: Start means try again
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

        /// <summary>The recording row (the status card's header) and the Live view bar: edge lights and state texts.</summary>
        private void UpdateRunButtons()
        {
            if (StreamBar == null) return;
            bool rec = _recorder.State == "recording";
            StreamBar.Background = rec ? GreenFill : Brush(0x16, 0x30, 0x1E);
            UpdateStreamButton(_wantLive);
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

        private void UpdateSaveButton(bool recording)
        {
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
            Log("Recording: " + SegmentMinutes + " min files, keep " + (LimitGb < 0 ? "what the storage allows (automatic)" : LimitGb == 0 ? "all free space (a reserve kept)" : "up to " + LimitGb + " GB"));
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
            UpdateIncidentUi();
            CheckPowerLoss();
            SampleStats();
            if (_clipSaving && (DateTime.Now - _clipSavingSince).TotalSeconds > 8) _clipSaving = false; // never stick
            CheckStorageWarning();
            // Nobody sees the rest under the black overlay: it is filled in again on the first tick after the screen wakes.
            if (_blackedOut && BlackoutOverlay.Visibility == Visibility.Visible) return;
            RecDot.Fill = rec || r.Halted ? Brush(0xFF, 0x30, 0x30) : r.State == "error" ? Brush(0xFF, 0xA0, 0x20) : Brush(0x3A, 0x20, 0x20);
            RecStateText.Text = rec ? "RECORDING"
                : r.Halted ? "RECORDING STOPPED"
                : !r.Enabled ? "START RECORDING" : r.State.ToUpperInvariant();
            RecTimeText.Text = rec ? ((int)r.CurrentSeconds / 60) + ":" + ((int)r.CurrentSeconds % 60).ToString("00") : "";
            RecStateText.Foreground = rec ? Brush(0xFF, 0xFF, 0xFF) : r.Halted || r.State == "error" ? Brush(0xFF, 0x6B, 0x6B) : Brush(0xCF, 0xE8, 0xD4);
            RecInfoText.Text = DateTime.Now < _clipShowUntil ? _clipText : RecordingSummary();
            UpdateSaveButton(rec);
            LocationText.Text = string.IsNullOrEmpty(r.Location) ? "Decided when recording starts" : "Now: " + r.Location;
            if (StoragePanel.Visibility == Visibility.Visible) StorageText.Text = DateTime.Now < _storageNoteUntil ? _storageNote : StorageDetail();
            UpdateRunButtons();
            UpdateSafetyUi();
        }

        private bool _incidentShown;

        /// <summary>
        /// Recording stopped by itself (a storage problem only the user can fix): a red banner with the reason and a
        /// Resume button, a buzz and a tone once, and the black screen is lifted and kept off until it is resumed.
        /// </summary>
        private void UpdateIncidentUi()
        {
            bool halted = _recorder.Halted;
            IncidentBorder.Visibility = halted ? Visibility.Visible : Visibility.Collapsed;
            if (halted)
            {
                IncidentText.Text = "RECORDING IS NOT WORKING\n" + _recorder.Incident;
                if (_blackedOut && BlackoutOverlay.Visibility == Visibility.Visible) { var lift = ApplyDisplayAsync(); } // the warning must be seen (PanelOnScreen)
                if (!_incidentShown)
                {
                    _incidentShown = true;
                    PlayChime(false);
                    var buzz = BuzzAsync(3);
                    var match = MatchCameraAsync(); // the camera stops too, unless live view is on
                }
            }
            else if (_incidentShown)
            {
                _incidentShown = false;
                var back = ApplyDisplayAsync(); // black out again (MainPage.Display.cs)
            }
        }

        private async void IncidentResumeButton_Click(object sender, RoutedEventArgs e)
        {
            SetRecording(true);
            await MatchCameraAsync();
        }

        /// <summary>Minutes of footage the loop space holds at the current quality (-1 until known).</summary>
        private double FootageMinutes()
        {
            var r = _recorder;
            double rate = r.EstimatedBytesPerSecond;
            if (rate <= 0) return -1;
            long space = r.LimitBytes;
            if (space == long.MaxValue) // "all free space": what the loop has plus the free space beyond the reserve
                space = r.FreeBytes >= 0 ? r.LoopBytes + Math.Max(0, r.FreeBytes - r.MinFreeBytes) : -1;
            return space < 0 ? -1 : space / rate / 60.0;
        }

        /// <summary>"25 min" / "3.2 h" (empty until known).</summary>
        private string FootageShort()
        {
            double m = FootageMinutes();
            if (m < 0) return "";
            return m >= 120 ? Math.Round(m / 60.0, 1).ToString("0.#", CultureInfo.InvariantCulture) + " h"
                            : Math.Round(m).ToString("0", CultureInfo.InvariantCulture) + " min";
        }

        private static string Gb(long bytes) => (bytes / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";

        /// <summary>Where the recordings are, in a word or two: "Phone storage", "SD card", "Chosen folder".</summary>
        private string PlaceShort()
        {
            string l = _recorder.Location ?? "";
            int cut = l.IndexOf(" (", StringComparison.Ordinal);
            if (cut > 0) l = l.Substring(0, cut);
            return l.Length > 0 ? char.ToUpperInvariant(l[0]) + l.Substring(1) : l;
        }

        /// <summary>The one line under Save clip: the place, free space and how much footage the loop keeps.</summary>
        private string RecordingSummary()
        {
            var r = _recorder;
            if (r.Halted) return r.Incident;
            if (_powerStopped) return "Stopped: no power. Recording starts again when the power returns.";
            if (!string.IsNullOrEmpty(r.LastError) && r.State == "error") return r.LastError;
            if (!string.IsNullOrEmpty(r.Warning)) return "WARNING: " + r.Warning;
            if (string.IsNullOrEmpty(r.Location)) return r.Enabled ? "Starts with the camera" : "Recording is stopped";
            string s = PlaceShort();
            if (r.FreeBytes >= 0) s += " · " + Gb(r.FreeBytes) + " free";
            string f = FootageShort();
            if (f.Length > 0) s += " · keeps ~" + f;
            if (r.DroppedFrames > 0) s += " · " + r.DroppedFrames + " frames dropped";
            return s;
        }

        /// <summary>The details at the top of STORAGE.</summary>
        private string StorageDetail()
        {
            var r = _recorder;
            if (string.IsNullOrEmpty(r.Location)) return "The place is decided when recording starts.";
            string s = string.Format(CultureInfo.InvariantCulture, "Loop: {0} file{1}, {2} of {3}{4}\nSaved clips: {5}, {6} of {7}{8}",
                r.LoopCount, r.LoopCount == 1 ? "" : "s", Gb(r.LoopBytes),
                r.LimitBytes == long.MaxValue ? "all free space" : Gb(r.LimitBytes), r.AutoLoop ? " (auto)" : "",
                r.SavedClips, Gb(r.SavedBytes), r.SavedLimitBytes == long.MaxValue ? "no limit" : Gb(r.SavedLimitBytes), r.AutoSaved ? " (auto)" : "");
            if (r.FreeBytes >= 0) s += "\n" + Gb(r.FreeBytes) + " free, " + Gb(r.MinFreeBytes) + " always kept free";
            double m = FootageMinutes();
            if (m >= 0) s += "\nThe loop keeps about " + FootageShort() + " of footage at this quality" + (m < 30 ? " - very little" : "");
            return s;
        }

        /// <summary>The SPEED and STORAGE tiles of the status card (from UpdateTiles, once a second).</summary>
        private void UpdateDashTiles()
        {
            var f = _gps.Running ? _gps.Latest : null;
            if (!_gpsOn) { SpeedValText.Text = "GPS off"; SetBar(SpeedBar, SpeedBarFill, 0, OffFill); }
            else if (f.HasValue && f.Value.SpeedMs.HasValue)
            {
                double ms = f.Value.SpeedMs.Value;
                SpeedValText.Text = SpeedText(ms);
                SetBar(SpeedBar, SpeedBarFill, 100 * ms / 36.0, GreenFill); // full bar: 130 km/h (80 mph)
            }
            else { SpeedValText.Text = "N/A"; SetBar(SpeedBar, SpeedBarFill, 0, AmberFill); } // no fix (yet), or no speed in it

            var r = _recorder;
            long usable = r.FreeBytes >= 0 ? r.FreeBytes + r.LoopBytes + r.SavedBytes : -1;
            double m = FootageMinutes();
            StoreText.Text = r.Halted ? "STOPPED" : m >= 0 ? "~" + FootageShort() : r.FreeBytes >= 0 ? Gb(r.FreeBytes) : "n/a";
            SetBar(StoreBar, StoreBarFill, usable > 0 ? 100.0 * (r.LoopBytes + r.SavedBytes) / usable : 0,
                r.Halted ? RedFill : (m >= 0 && m < 30) || !string.IsNullOrEmpty(r.Warning) ? AmberFill : GreenFill);

            // The notice under the default-login one: recording to the phone's own storage (small, shared with the system)
            string note = InternalStorageNotice();
            StorageNotice.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (note.Length > 0) StorageNoticeText.Text = note;
            ChargerNoticeBorder.Visibility = _chargerNotice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_chargerNotice.Length > 0) ChargerNoticeText.Text = _chargerNotice;
        }

        /// <summary>Text of the "recording to internal storage" notice (empty when recording elsewhere).</summary>
        private string InternalStorageNotice()
        {
            var r = _recorder;
            if (r.OnSd || !(r.Location ?? "").StartsWith("phone storage", StringComparison.Ordinal)) return "";
            string f = FootageShort();
            return "Recording to the phone's internal storage" + (r.FreeBytes >= 0 ? " (" + Gb(r.FreeBytes) + " free" + (f.Length > 0 ? ", keeps ~" + f : "") + ")" : "")
                + ". An SD card is highly recommended: constant recording wears out the phone's internal storage.";
        }

        // ---------------------------------------------------------------- delete all / format the SD card

        private string _storageNote = "";
        private DateTime _storageNoteUntil = DateTime.MinValue;

        private void ShowStorageNote(string text)
        {
            _storageNote = text;
            _storageNoteUntil = DateTime.Now.AddSeconds(8);
            StorageText.Text = text;
        }

        private async void DeleteAllButton_Click(object sender, RoutedEventArgs e)
        {
            var alsoSaved = new ToggleSwitch { Header = "Also delete the saved clips", Margin = new Thickness(0, 8, 0, 0) };
            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Deletes every loop recording in " + (string.IsNullOrEmpty(_recorder.Location) ? "the recordings folder" : PlaceShort().ToLowerInvariant())
                    + ". Recording carries on in a new file. This cannot be undone."
            });
            body.Children.Add(alsoSaved);
            var dlg = new ContentDialog { Title = "Delete all recordings?", Content = body, PrimaryButtonText = "Delete", SecondaryButtonText = "Cancel" };
            try { if (await dlg.ShowAsync() != ContentDialogResult.Primary) return; }
            catch (Exception ex) { Log("Delete all: " + ex.Message); return; } // another dialog is open
            DeleteAllButton.IsEnabled = false;
            ShowStorageNote("Deleting...");
            string result = await _recorder.DeleteAllAsync(alsoSaved.IsOn);
            DeleteAllButton.IsEnabled = true;
            ShowStorageNote(result);
        }

        /// <summary>
        /// Apps cannot format a card, so: record to the phone's storage, then open the phone's Storage settings
        /// (SD card > Format). Coming back to the app uses the card again (ReturnToSd on resume / visible).
        /// </summary>
        private async void FormatSdButton_Click(object sender, RoutedEventArgs e)
        {
            bool card = false;
            try { card = (await Windows.Storage.KnownFolders.RemovableDevices.GetFoldersAsync()).Count > 0; } catch { }
            if (!card) { ShowStorageNote("No SD card found."); return; }
            var dlg = new ContentDialog
            {
                Title = "Format the SD card?",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "Formatting erases everything on the card, recordings included. Apps can't format a card, so recording moves to "
                         + "the phone's storage and the phone's Storage settings open: tap the SD card, then Format. Come back to this app afterwards and it uses the card again."
                },
                PrimaryButtonText = "Open Storage settings",
                SecondaryButtonText = "Cancel"
            };
            try { if (await dlg.ShowAsync() != ContentDialogResult.Primary) return; }
            catch (Exception ex) { Log("Format SD: " + ex.Message); return; }
            _recorder.ReleaseSd();
            await Task.Delay(1500); // the file on the card is finished first
            bool opened = false;
            try { opened = await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:storagesense")); } catch { }
            if (!opened) ShowStorageNote("Could not open the settings: open Settings > System > Storage on the phone. Recording stays on the phone's storage until the app is opened again.");
        }

        // ---------------------------------------------------------------- web / USB config

        /// <summary>
        /// record=on|off (now), recordonopen=on|off, live=on|off (now), liveonopen=on|off, segment=MINUTES,
        /// storage=GB|all, location=auto|phone|sd|custom, saveclip=1. Runs on a network thread.
        /// </summary>
        private async Task ApplyRecordingConfigAsync(IDictionary<string, string> form, List<string> done)
        {
            await ApplySafetyConfigAsync(form, done);
            await ApplyPowerLossConfigAsync(form, done);
            await ApplyServerConfigAsync(form, done);
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
                _settings.Values[LiveOnOpenKey] = IsOn(loo);
                done.Add("live view when the app opens " + (LiveOnOpen ? "on" : "off"));
                uiChange = true;
            }
            int seg = SegmentMinutes, gb = LimitGb;
            bool limits = false;
            if (form.TryGetValue("segment", out var sg) && int.TryParse(sg, out int sgv) && sgv >= 1 && sgv <= 30) { seg = sgv; limits = true; }
            if (form.TryGetValue("storage", out var st))
            {
                if (st.Equals("auto", StringComparison.OrdinalIgnoreCase)) { gb = -1; limits = true; }
                else if (st.Equals("all", StringComparison.OrdinalIgnoreCase) || st == "0") { gb = 0; limits = true; }
                else if (int.TryParse(st, out int gbv) && gbv >= 1 && gbv <= 1024) { gb = gbv; limits = true; }
            }
            if (limits)
            {
                ApplyRecLimits(seg, gb);
                done.Add("recording files " + SegmentMinutes + " min, storage " + (LimitGb < 0 ? "automatic" : LimitGb == 0 ? "all" : LimitGb + " GB"));
                uiChange = true;
            }
            if (form.TryGetValue("location", out var loc))
            {
                loc = loc.Trim().ToLowerInvariant();
                if (Array.IndexOf(LocationModes, loc) < 0) done.Add("location: use auto, phone, sd or custom");
                else if (loc == "custom" && !HasCustomFolder) done.Add("location: choose a folder on the phone first (Recording > Choose folder)");
                else { SetLocation(loc); done.Add("recordings go to " + loc); uiChange = true; }
            }
            if (form.TryGetValue("resume", out var rsm) && IsOn(rsm))
            {
                SetRecording(true); // after "recording stopped" (storage problem): try again
                done.Add("recording resumed");
                matchCamera = true;
            }
            if (form.TryGetValue("testincident", out var ti) && IsOn(ti)) // shows the "recording stopped" warning on purpose
            {
                _recorder.TestIncident();
                done.Add("test: the recording-stopped warning is shown (resume=1, or Resume on the phone, clears it)");
                matchCamera = true;
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
            "storage=" + (LimitGb < 0 ? "auto" : LimitGb == 0 ? "all" : LimitGb.ToString()) + " (GB the loop recordings may use; auto = what the storage allows; all = all free space but a reserve)\n" +
            (_recorder.Halted ? "STOPPED: " + _recorder.Incident + " (resume=1 to try again)\n" : "") +
            PowerLossConfigText() +
            ServerConfigText() +
            SafetyConfigText();

        private const string RecordingConfigHelp = ", record=on|off, recordonopen=on|off, live=on|off, liveonopen=on|off, location=auto|phone|sd|custom, segment=MINUTES, storage=auto|GB|all, saveclip=1, resume=1, powerloss=SECONDS|off, log=on|off, impact=off|low|medium|high, keepnext=on|off, savedlimit=auto|GB|none, replacesaved=on|off, gps=on|off, units=auto|kmh|mph, subtitles=on|off";

        private void AddRecordingJson(JsonObject o, bool position = true)
        {
            var r = _recorder;
            o["live"] = JsonValue.CreateBooleanValue(_wantLive);
            o["liveOnOpen"] = JsonValue.CreateBooleanValue(LiveOnOpen);
            AddServerJson(o);
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
            j["halted"] = JsonValue.CreateBooleanValue(r.Halted);
            j["incident"] = JsonValue.CreateStringValue(r.Incident ?? "");
            j["loopLimitGB"] = r.LimitBytes == long.MaxValue ? JsonValue.CreateNullValue() : JsonValue.CreateNumberValue(Math.Round(r.LimitBytes / 1073741824.0, 1));
            j["savedLimitNowGB"] = r.SavedLimitBytes == long.MaxValue ? JsonValue.CreateNullValue() : JsonValue.CreateNumberValue(Math.Round(r.SavedLimitBytes / 1073741824.0, 1));
            j["reserveMB"] = JsonValue.CreateNumberValue(Math.Round(r.MinFreeBytes / 1048576.0));
            j["savedUsedMB"] = JsonValue.CreateNumberValue(Math.Round(r.SavedBytes / 1048576.0));
            double fm = FootageMinutes();
            j["footageMin"] = fm >= 0 ? JsonValue.CreateNumberValue(Math.Round(fm)) : JsonValue.CreateNullValue();
            j["place"] = JsonValue.CreateStringValue(PlaceShort());
            AddPowerLossJson(j);
            AddSafetyJson(j, position);
            o["recording"] = j;
        }
    }
}
