using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace W10MDashcam
{
    /// <summary>
    /// Stop when the power is lost: when the charger goes (the car is switched off), a countdown shows on the screen;
    /// when it ends, recording and live view stop so the battery is not run flat. "Keep recording" skips it this time.
    /// When power returns while the app is still open (or when it is reopened), they start again by themselves.
    /// Heat pauses ("Idle" on a charger) are not a power loss: only "Discharging" is.
    /// </summary>
    public sealed partial class MainPage
    {
        private static readonly int[] PowerLossOptions = { 30, 60, 120, 300, 0 }; // seconds; 0 = never
        private int _powerLossSec = 60;
        private DateTime _powerLostAt = DateTime.MinValue; // counting down since then (MinValue: not counting)
        private bool _powerLossKept;                        // "Keep recording" pressed for this power loss
        private bool _powerStopped;                         // stopped by the countdown; starts again with power
        private bool _stoppedWasRecording, _stoppedWasLive;
        private volatile bool _testPowerLoss;               // testpowerloss=1: act as if the charger were unplugged
        private bool _loadingPowerLossUi = true;

        private bool PowerLossCounting => _powerLostAt != DateTime.MinValue;
        /// <summary>The power-loss banner is up (it keeps the black screen away, like a recording problem).</summary>
        private bool PowerLossShowing => PowerLossCounting || _powerStopped;
        private int PowerLossLeft => PowerLossCounting ? Math.Max(0, _powerLossSec - (int)(DateTime.Now - _powerLostAt).TotalSeconds) : -1;

        /// <summary>From LoadRecordingSettings (constructor).</summary>
        private void LoadPowerLossSettings()
        {
            _powerLossSec = _settings.Values["PowerLossStopSec"] is int s && s >= 0 ? s : 60;
            foreach (var v in PowerLossOptions) PowerLossBox.Items.Add(new ChoiceOption { Value = v, Text = PowerLossText(v) });
            SelectChoice(PowerLossBox, _powerLossSec);
            _loadingPowerLossUi = false;
        }

        private static string PowerLossText(int sec) =>
            sec <= 0 ? "Never (keep recording on battery)" : sec < 120 ? "After " + sec + " s" : "After " + sec / 60 + " min";

        private void PowerLossBox_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingPowerLossUi || !(PowerLossBox.SelectedItem is ChoiceOption c)) return;
            SetPowerLoss(c.Value);
        }

        private void SetPowerLoss(int sec)
        {
            _powerLossSec = Math.Max(0, sec);
            _settings.Values["PowerLossStopSec"] = _powerLossSec;
            if (_powerLossSec == 0 && PowerLossCounting) { _powerLostAt = DateTime.MinValue; }
            Log("Stop when the power is lost: " + PowerLossText(_powerLossSec).ToLowerInvariant());
        }

        private bool OnExternalPower()
        {
            if (_testPowerLoss) return false;
            var r = TryBattery();
            return r == null || r.Status != Windows.System.Power.BatteryStatus.Discharging;
        }

        /// <summary>Once a second (UpdateRecordingUi).</summary>
        private void CheckPowerLoss()
        {
            bool power = OnExternalPower();
            if (power)
            {
                if (PowerLossCounting) { _powerLostAt = DateTime.MinValue; Log("Power: back - countdown cancelled"); }
                _powerLossKept = false;
                if (_powerStopped) RestartAfterPowerLoss("Power: back - recording again");
            }
            else if (_powerLossSec > 0 && !_powerLossKept && !_powerStopped && (_recorder.Enabled || _wantLive))
            {
                if (!PowerLossCounting)
                {
                    _powerLostAt = DateTime.Now;
                    Log("Power: lost - recording stops in " + _powerLossSec + " s unless it comes back");
                    PlayChime(false);
                    var buzz = BuzzAsync(2);
                }
                else if (PowerLossLeft <= 0) StopForPowerLoss();
            }
            UpdatePowerLossUi();
        }

        private void StopForPowerLoss()
        {
            _powerLostAt = DateTime.MinValue;
            _stoppedWasRecording = _recorder.Enabled;
            _stoppedWasLive = _wantLive;
            _powerStopped = true;
            Log("Power: none for " + _powerLossSec + " s - recording and live view stopped to save the battery (they start again with power)");
            SetRecording(false);
            SetLive(false);
            var match = MatchCameraAsync(); // the camera stops; the screen may then lock as usual
        }

        private void RestartAfterPowerLoss(string why)
        {
            _powerStopped = false;
            Log(why);
            if (_stoppedWasRecording) SetRecording(true);
            if (_stoppedWasLive) SetLive(true);
            var match = MatchCameraAsync();
        }

        private void UpdatePowerLossUi()
        {
            bool show = PowerLossShowing;
            PowerLossBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            if (PowerLossCounting)
            {
                PowerLossMessage.Text = "POWER LOST\nRecording stops in " + PowerLossLeft + " s unless the power comes back.";
                PowerLossButton.Content = "KEEP RECORDING";
            }
            else
            {
                PowerLossMessage.Text = "STOPPED - NO POWER\nRecording and live view stopped to save the battery. They start again when the power returns.";
                PowerLossButton.Content = "START AGAIN NOW";
            }
            if (_blackedOut && BlackoutOverlay.Visibility == Visibility.Visible) // the countdown must be seen
            {
                _wakeTimer.Stop();
                BlackoutOverlay.Visibility = Visibility.Collapsed;
                if (_brightness != null && _brightness.IsSupported) _brightness.StopOverride();
            }
        }

        private void PowerLossButton_Click(object sender, RoutedEventArgs e)
        {
            if (PowerLossCounting)
            {
                _powerLostAt = DateTime.MinValue;
                _powerLossKept = true;
                Log("Power: keep recording on battery (chosen on the phone)");
                if (_blackedOut) { _wakeTimer.Stop(); _wakeTimer.Start(); } // black out again after the usual delay
            }
            else if (_powerStopped) RestartAfterPowerLoss("Power: started again by hand (on battery)");
            UpdatePowerLossUi();
        }

        // ---------------------------------------------------------------- web / USB config

        /// <summary>powerloss=SECONDS|off, testpowerloss=1|0. Network thread.</summary>
        private async Task ApplyPowerLossConfigAsync(IDictionary<string, string> form, List<string> done)
        {
            bool ui = false;
            if (form.TryGetValue("powerloss", out var pl))
            {
                pl = pl.Trim().ToLowerInvariant();
                if (pl == "off" || pl == "never" || pl == "0") { await RunUiAsync(() => { SetPowerLoss(0); return Task.CompletedTask; }); done.Add("stop on power loss: never"); ui = true; }
                else if (int.TryParse(pl, out int sec) && sec >= 10 && sec <= 3600) { await RunUiAsync(() => { SetPowerLoss(sec); return Task.CompletedTask; }); done.Add("stop on power loss after " + sec + " s"); ui = true; }
                else done.Add("powerloss: use seconds (10-3600) or off");
            }
            if (form.TryGetValue("testpowerloss", out var tp))
            {
                _testPowerLoss = IsOn(tp);
                done.Add("test: " + (_testPowerLoss ? "acting as if the charger were unplugged" : "power as reported by the phone"));
            }
            if (ui)
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    _loadingPowerLossUi = true;
                    if (Array.IndexOf(PowerLossOptions, _powerLossSec) < 0)
                        PowerLossBox.Items.Insert(PowerLossBox.Items.Count - 1, new ChoiceOption { Value = _powerLossSec, Text = PowerLossText(_powerLossSec) });
                    SelectChoice(PowerLossBox, _powerLossSec);
                    _loadingPowerLossUi = false;
                });
        }

        private string PowerLossConfigText() =>
            "powerloss=" + (_powerLossSec <= 0 ? "off" : _powerLossSec.ToString(CultureInfo.InvariantCulture)) + " (seconds without power before recording stops)\n";

        private void AddPowerLossJson(JsonObject j)
        {
            j["powerLossSec"] = JsonValue.CreateNumberValue(_powerLossSec);
            j["powerLossLeft"] = PowerLossCounting ? JsonValue.CreateNumberValue(PowerLossLeft) : JsonValue.CreateNullValue();
            j["powerStopped"] = JsonValue.CreateBooleanValue(_powerStopped);
        }
    }
}
