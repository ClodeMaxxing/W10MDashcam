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
    /// <summary>
    /// Crash detection (G-sensor auto-save), "keep the next file", and the space for saved clips.
    /// </summary>
    public sealed partial class MainPage
    {
        private readonly ImpactDetector _impact = new ImpactDetector();
        private string _impactSens = "medium";
        private static readonly string[] ImpactLevels = { "off", "low", "medium", "high" };
        private static readonly int[] SavedLimitOptions = { -1, 2, 4, 8, 16, 32, 0 }; // -1 = automatic, 0 = no limit
        private bool _loadingSafetyUi = true;
        private string _lastWarning = "";
        private DateTime _lastTouch = DateTime.MinValue; // set by AnyTouch (MainPage.xaml.cs)

        private int SavedLimitGb => _recorder.AutoSaved ? -1 : _recorder.SavedLimitBytes == long.MaxValue ? 0 : (int)(_recorder.SavedLimitBytes >> 30);

        /// <summary>From LoadRecordingSettings (constructor).</summary>
        private void LoadSafetySettings()
        {
            string sens = _settings.Values["RecImpact"] as string;
            _impactSens = Array.IndexOf(ImpactLevels, sens) >= 0 ? sens : "medium";
            _impact.Threshold = ImpactDetector.ThresholdFor(_impactSens);
            _recorder.KeepNextFile = !(_settings.Values["RecKeepNext"] is bool kn) || kn;
            int sl = _settings.Values["RecSavedLimitGB"] is int s ? s : -1; // new installs: automatic
            _recorder.AutoSaved = sl < 0;
            if (sl >= 0) _recorder.SavedLimitBytes = sl == 0 ? long.MaxValue : (long)sl << 30;
            _recorder.ReplaceOldSaved = _settings.Values["RecReplaceSaved"] is bool rs && rs;

            ImpactBox.Items.Add(new ChoiceOption { Value = 0, Text = "Off" });
            ImpactBox.Items.Add(new ChoiceOption { Value = 1, Text = "Low - big impacts only (2.5 g)" });
            ImpactBox.Items.Add(new ChoiceOption { Value = 2, Text = "Medium (1.6 g)" });
            ImpactBox.Items.Add(new ChoiceOption { Value = 3, Text = "High - also hard knocks (1 g)" });
            foreach (var g in SavedLimitOptions) SavedLimitBox.Items.Add(new ChoiceOption { Value = g, Text = g < 0 ? "Automatic (about a fifth of the storage)" : g == 0 ? "No limit" : g + " GB" });
            SelectChoice(ImpactBox, Array.IndexOf(ImpactLevels, _impactSens));
            SelectChoice(SavedLimitBox, SavedLimitGb);
            KeepNextToggle.IsOn = _recorder.KeepNextFile;
            ReplaceSavedToggle.IsOn = _recorder.ReplaceOldSaved;
            _loadingSafetyUi = false;

            _impact.Log = Log;
            _impact.Impact += g => { var _ = Dispatcher.RunAsync(CoreDispatcherPriority.High, () => OnImpact(g)); };
            _impact.NearMiss += g => Log("G-sensor: jolt " + g.ToString("0.00", CultureInfo.InvariantCulture) + " g (saves at "
                + _impact.Threshold.ToString("0.0", CultureInfo.InvariantCulture) + " g)");
            _impact.Start();
            LoadGpsSettings();
            _recorder.Prepare(); // find and list the recordings folder now, before the camera is ready
        }

        private void OnImpact(double g)
        {
            string gs = g.ToString("0.0", CultureInfo.InvariantCulture) + " g";
            if ((DateTime.Now - _lastTouch).TotalSeconds < 2)
            {
                Log("G-sensor: " + gs + " while the screen was being touched - taken as handling the phone, not saved");
                return;
            }
            if (_recorder.State != "recording")
            {
                Log("IMPACT detected (" + gs + ") - not recording, nothing saved");
                return;
            }
            Log("IMPACT detected (" + gs + ") - saving the clip and the next file");
            RequestSaveClip("G-sensor", "impact " + gs, true);
        }

        private void ImpactBox_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingSafetyUi || !(ImpactBox.SelectedItem is ChoiceOption c)) return;
            SetImpact(ImpactLevels[c.Value]);
        }

        private void SetImpact(string level)
        {
            _impactSens = level;
            _impact.Threshold = ImpactDetector.ThresholdFor(level);
            _settings.Values["RecImpact"] = level;
            Log("Crash detection: " + level + (_impact.Threshold > 0 ? " (" + _impact.Threshold.ToString("0.0", CultureInfo.InvariantCulture) + " g)" : ""));
        }

        private void KeepNextToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingSafetyUi) return;
            _recorder.KeepNextFile = KeepNextToggle.IsOn;
            _settings.Values["RecKeepNext"] = KeepNextToggle.IsOn;
        }

        private void SavedLimitBox_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingSafetyUi || !(SavedLimitBox.SelectedItem is ChoiceOption c)) return;
            SetSavedLimit(c.Value);
        }

        private void SetSavedLimit(int gb)
        {
            _recorder.AutoSaved = gb < 0; // automatic: the recorder works it out from the storage
            if (gb >= 0) _recorder.SavedLimitBytes = gb == 0 ? long.MaxValue : (long)gb << 30;
            _settings.Values["RecSavedLimitGB"] = gb;
            _recorder.Housekeep();
        }

        private void ReplaceSavedToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingSafetyUi) return;
            SetReplaceSaved(ReplaceSavedToggle.IsOn);
        }

        private void SetReplaceSaved(bool on)
        {
            _recorder.ReplaceOldSaved = on;
            _settings.Values["RecReplaceSaved"] = on;
            _recorder.Housekeep();
            Log("Saved clips: " + (on ? "the oldest are replaced when their space is full" : "never replaced (a warning when their space is full)"));
        }

        /// <summary>Once a second: the live G-sensor reading, and a warning tone when a storage warning appears.</summary>
        private void UpdateSafetyUi()
        {
            if (RecordingPanel.Visibility == Visibility.Visible) // nobody reads it while the section is folded
                ImpactText.Text = !_impact.Available ? "No G-sensor found on this phone"
                    : string.Format(CultureInfo.InvariantCulture, "Now {0:0.00} g · 10 s peak {1:0.00} g{2}",
                        _impact.Now, _impact.Peak,
                        _impact.LastImpact > DateTime.MinValue ? " · last impact " + _impact.LastImpact.ToString("HH:mm") + " (" + _impact.LastImpactG.ToString("0.0", CultureInfo.InvariantCulture) + " g)" : "");
            string w = _recorder.Warning ?? "";
            if (w.Length > 0 && w != _lastWarning) { Log("Storage warning: " + w); PlayChime(false); }
            _lastWarning = w;
            UpdateGpsUi();
        }

        // ---------------------------------------------------------------- web / USB config

        /// <summary>impact=off|low|medium|high, keepnext=on|off, savedlimit=GB|none, replacesaved=on|off. Network thread.</summary>
        private async Task ApplySafetyConfigAsync(IDictionary<string, string> form, List<string> done)
        {
            await ApplyGpsConfigAsync(form, done);
            bool ui = false;
            if (form.TryGetValue("impact", out var im))
            {
                im = im.Trim().ToLowerInvariant();
                if (Array.IndexOf(ImpactLevels, im) < 0) done.Add("impact: use off, low, medium or high");
                else { SetImpact(im); done.Add("crash detection " + im); ui = true; }
            }
            if (form.TryGetValue("keepnext", out var kn))
            {
                _recorder.KeepNextFile = IsOn(kn);
                _settings.Values["RecKeepNext"] = _recorder.KeepNextFile;
                done.Add("keep the next file " + (_recorder.KeepNextFile ? "on" : "off"));
                ui = true;
            }
            if (form.TryGetValue("savedlimit", out var sl))
            {
                if (sl.Equals("auto", StringComparison.OrdinalIgnoreCase)) { SetSavedLimit(-1); done.Add("saved clips: automatic space"); ui = true; }
                else if (sl.Equals("none", StringComparison.OrdinalIgnoreCase) || sl == "0") { SetSavedLimit(0); done.Add("saved clips: no space limit"); ui = true; }
                else if (int.TryParse(sl, out int gb) && gb >= 1 && gb <= 1024) { SetSavedLimit(gb); done.Add("saved clips limit " + gb + " GB"); ui = true; }
                else done.Add("savedlimit: use GB or none");
            }
            if (form.TryGetValue("replacesaved", out var rs))
            {
                SetReplaceSaved(IsOn(rs));
                done.Add("replace oldest saved clips " + (_recorder.ReplaceOldSaved ? "on" : "off"));
                ui = true;
            }
            if (ui)
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    _loadingSafetyUi = true;
                    SelectChoice(ImpactBox, Array.IndexOf(ImpactLevels, _impactSens));
                    SelectChoice(SavedLimitBox, SavedLimitGb);
                    KeepNextToggle.IsOn = _recorder.KeepNextFile;
                    ReplaceSavedToggle.IsOn = _recorder.ReplaceOldSaved;
                    _loadingSafetyUi = false;
                });
        }

        private string SafetyConfigText() =>
            "impact=" + _impactSens + " (crash detection; low 2.5 g, medium 1.6 g, high 1 g)\n" +
            "keepnext=" + (_recorder.KeepNextFile ? "on" : "off") + " (Save clip also keeps the next file; impacts always do)\n" +
            "savedlimit=" + (SavedLimitGb < 0 ? "auto" : SavedLimitGb == 0 ? "none" : SavedLimitGb.ToString()) + " (GB for saved clips), replacesaved=" + (_recorder.ReplaceOldSaved ? "on" : "off") + "\n" +
            GpsConfigText();

        private void AddSafetyJson(JsonObject j, bool position = true)
        {
            j["impact"] = JsonValue.CreateStringValue(_impactSens);
            j["impactAvailable"] = JsonValue.CreateBooleanValue(_impact.Available);
            j["gNow"] = JsonValue.CreateNumberValue(Math.Round(_impact.Now, 2));
            j["gPeak"] = JsonValue.CreateNumberValue(Math.Round(_impact.Peak, 2));
            j["lastImpact"] = JsonValue.CreateStringValue(_impact.LastImpact > DateTime.MinValue
                ? _impact.LastImpact.ToString("HH:mm:ss") + " (" + _impact.LastImpactG.ToString("0.0", CultureInfo.InvariantCulture) + " g)" : "");
            j["keepNext"] = JsonValue.CreateBooleanValue(_recorder.KeepNextFile);
            j["savedMB"] = JsonValue.CreateNumberValue(Math.Round(_recorder.SavedBytes / 1048576.0));
            j["savedLimitGB"] = JsonValue.CreateNumberValue(SavedLimitGb);
            j["replaceSaved"] = JsonValue.CreateBooleanValue(_recorder.ReplaceOldSaved);
            j["warning"] = JsonValue.CreateStringValue(_recorder.Warning ?? "");
            AddGpsJson(j, position);
        }
    }
}
