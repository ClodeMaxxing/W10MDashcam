using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using W10MDashcam.Recording;

namespace W10MDashcam
{
    /// <summary>Dashcam recording: settings, the on-screen controls, web/USB config keys and status.</summary>
    public sealed partial class MainPage
    {
        private readonly SegmentRecorder _recorder = new SegmentRecorder();
        private bool _loadingRecUi = true;

        private static readonly int[] SegmentMinutesOptions = { 1, 3, 5, 10 };
        private static readonly int[] LimitGbOptions = { 4, 8, 16, 32, 64, 0 }; // 0 = all free space (keeping 1 GB)

        private sealed class ChoiceOption
        {
            public int Value;
            public string Text;
            public override string ToString() => Text;
        }

        /// <summary>Constructor part: saved settings into the recorder and the controls.</summary>
        private void LoadRecordingSettings()
        {
            _recorder.Log = Log;
            _recorder.Enabled = !(_settings.Values["RecEnabled"] is bool re) || re;
            int seg = _settings.Values["RecSegmentMin"] is int sm ? sm : 3;
            int gb = _settings.Values["RecLimitGB"] is int lg ? lg : 16;
            SetRecorderLimits(seg, gb);

            foreach (var m in SegmentMinutesOptions) SegmentBox.Items.Add(new ChoiceOption { Value = m, Text = m + " min" });
            foreach (var g in LimitGbOptions) LimitBox.Items.Add(new ChoiceOption { Value = g, Text = g == 0 ? "All free space (keep 1 GB)" : g + " GB" });
            RecordToggle.IsOn = _recorder.Enabled;
            SelectChoice(SegmentBox, seg);
            SelectChoice(LimitBox, gb);
            _loadingRecUi = false;
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

        // ---------------------------------------------------------------- controls

        private void SaveClipButton_Click(object sender, RoutedEventArgs e) => _recorder.SaveClip();

        private void RecordToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingRecUi) return;
            SetRecording(RecordToggle.IsOn);
        }

        private void SetRecording(bool on)
        {
            _recorder.Enabled = on;
            _settings.Values["RecEnabled"] = on;
            if (!on) _recorder.EndSegment();
            Log("Recording " + (on ? "on" : "off"));
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

        /// <summary>Once a second, with the other stats.</summary>
        private void UpdateRecordingUi()
        {
            var r = _recorder;
            bool rec = r.State == "recording";
            RecDot.Fill = new Windows.UI.Xaml.Media.SolidColorBrush(rec ? Windows.UI.Color.FromArgb(255, 0xFF, 0x30, 0x30)
                : r.State == "error" ? Windows.UI.Color.FromArgb(255, 0xFF, 0xA0, 0x20) : Windows.UI.Color.FromArgb(255, 0x55, 0x55, 0x55));
            RecStateText.Text = rec ? "REC " + TimeSpan.FromSeconds(Math.Floor(r.CurrentSeconds)).ToString(@"m\:ss") : r.State.ToUpperInvariant();
            RecInfoText.Text = RecordingSummary();
            SaveClipButton.IsEnabled = rec;
        }

        private string RecordingSummary()
        {
            var r = _recorder;
            if (!r.Enabled) return "Recording is off";
            if (!string.IsNullOrEmpty(r.LastError) && r.State == "error") return r.LastError;
            if (string.IsNullOrEmpty(r.Location)) return "Starts with the camera";
            string s = string.Format(CultureInfo.InvariantCulture, "{0} files, {1:0.0} GB", r.LoopCount, r.LoopBytes / 1073741824.0);
            if (r.FreeBytes >= 0) s += string.Format(CultureInfo.InvariantCulture, ", {0:0.0} GB free", r.FreeBytes / 1073741824.0);
            if (r.SavedClips > 0) s += ", " + r.SavedClips + " saved";
            s += " - " + r.Location;
            if (r.DroppedFrames > 0) s += " - " + r.DroppedFrames + " frames dropped (storage too slow)";
            return s;
        }

        // ---------------------------------------------------------------- web / USB config

        /// <summary>record=on|off, segment=MINUTES, storage=GB|all, saveclip=1. Runs on a network thread.</summary>
        private async Task ApplyRecordingConfigAsync(IDictionary<string, string> form, List<string> done)
        {
            bool uiChange = false;
            if (form.TryGetValue("record", out var rec))
            {
                SetRecording(IsOn(rec));
                done.Add("recording " + (_recorder.Enabled ? "on" : "off"));
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
            if (form.TryGetValue("saveclip", out var sc) && IsOn(sc))
            {
                _recorder.SaveClip();
                done.Add("clip saved");
            }
            if (uiChange)
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    _loadingRecUi = true;
                    RecordToggle.IsOn = _recorder.Enabled;
                    SelectChoice(SegmentBox, SegmentMinutes);
                    SelectChoice(LimitBox, LimitGb);
                    _loadingRecUi = false;
                });
        }

        private string RecordingConfigText() =>
            "record=" + (_recorder.Enabled ? "on" : "off") + " (" + _recorder.State + ")\n" +
            "segment=" + SegmentMinutes + " (minutes per file)\n" +
            "storage=" + (LimitGb == 0 ? "all" : LimitGb.ToString()) + " (GB the loop recordings may use; all = all free space but 1 GB)\n";

        private const string RecordingConfigHelp = ", record=on|off, segment=MINUTES, storage=GB|all, saveclip=1";

        private void AddRecordingJson(JsonObject o)
        {
            var r = _recorder;
            var j = new JsonObject();
            j["enabled"] = JsonValue.CreateBooleanValue(r.Enabled);
            j["state"] = JsonValue.CreateStringValue(r.State);
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
            o["recording"] = j;
        }
    }
}
