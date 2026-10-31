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
    /// GPS speed and position: shown on the phone and the web page, and written into every recording as a subtitle
    /// track (date/time, speed, position, IMPACT markers) that VLC and most players show over the video.
    /// </summary>
    public sealed partial class MainPage
    {
        private readonly GpsTracker _gps = new GpsTracker();
        private bool _gpsOn = true;
        private string _units = "auto";            // auto | kmh | mph
        private static readonly string[] UnitModes = { "auto", "kmh", "mph" };
        private bool _loadingGpsUi = true;

        /// <summary>From LoadSafetySettings (constructor, UI thread).</summary>
        private void LoadGpsSettings()
        {
            _gpsOn = !(_settings.Values["GpsOn"] is bool g) || g;
            string u = _settings.Values["SpeedUnits"] as string;
            _units = Array.IndexOf(UnitModes, u) >= 0 ? u : "auto";
            _recorder.Subtitles = !(_settings.Values["RecSubtitles"] is bool st) || st;
            _recorder.SubtitleText = SubtitleFor;

            UnitsBox.Items.Add(new ChoiceOption { Value = 0, Text = "Automatic (" + (Mph ? "mph" : "km/h") + " here)" });
            UnitsBox.Items.Add(new ChoiceOption { Value = 1, Text = "km/h" });
            UnitsBox.Items.Add(new ChoiceOption { Value = 2, Text = "mph" });
            SelectChoice(UnitsBox, Array.IndexOf(UnitModes, _units));
            GpsToggle.IsOn = _gpsOn;
            SubtitlesToggle.IsOn = _recorder.Subtitles;
            _loadingGpsUi = false;

            _gps.Log = Log;
            if (_gpsOn) { var _ = _gps.StartAsync(); }
        }

        /// <summary>Miles per hour: chosen, or automatic in the countries that sign roads in mph.</summary>
        private bool Mph
        {
            get
            {
                if (_units == "mph") return true;
                if (_units == "kmh") return false;
                try
                {
                    string r = Windows.System.UserProfile.GlobalizationPreferences.HomeGeographicRegion ?? "";
                    return r == "US" || r == "GB" || r == "LR" || r == "MM";
                }
                catch { return false; }
            }
        }

        private string SpeedText(double ms) => Mph
            ? Math.Round(ms * 2.236936).ToString(CultureInfo.InvariantCulture) + " mph"
            : Math.Round(ms * 3.6).ToString(CultureInfo.InvariantCulture) + " km/h";

        private static string Compass(double deg)
        {
            string[] p = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            return p[(int)Math.Round(((deg % 360) + 360) % 360 / 45.0) % 8];
        }

        private static string Coords(GpsFix f) => string.Format(CultureInfo.InvariantCulture, "{0:0.00000} {1}  {2:0.00000} {3}",
            Math.Abs(f.Lat), f.Lat >= 0 ? "N" : "S", Math.Abs(f.Lon), f.Lon >= 0 ? "E" : "W");

        /// <summary>
        /// The subtitle for one second of a recording (runs on the recorder's thread):
        ///   2026-10-30  21:57:43   52 km/h   IMPACT 2.5 g
        ///   47.61234 N  122.33456 W   heading W
        /// </summary>
        private string SubtitleFor(DateTime utc)
        {
            var local = utc.ToLocalTime();
            string line1 = local.ToString("yyyy-MM-dd  HH:mm:ss", CultureInfo.InvariantCulture);
            string line2 = null;
            if (_gps.Running)
            {
                var f = _gps.At(utc);
                if (f.HasValue)
                {
                    var fx = f.Value;
                    if (fx.SpeedMs.HasValue) line1 += "   " + SpeedText(fx.SpeedMs.Value);
                    line2 = Coords(fx);
                    if (fx.Heading.HasValue && fx.SpeedMs.HasValue && fx.SpeedMs.Value > 1.5) line2 += "   heading " + Compass(fx.Heading.Value);
                }
                else line1 += "   GPS: no fix";
            }
            var since = (local - _impact.LastImpact).TotalSeconds;
            if (since >= -0.5 && since < 6) line1 += "   IMPACT " + _impact.LastImpactG.ToString("0.0", CultureInfo.InvariantCulture) + " g";
            return line2 == null ? line1 : line1 + "\n" + line2;
        }

        // ---------------------------------------------------------------- controls

        private void GpsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingGpsUi) return;
            SetGps(GpsToggle.IsOn);
        }

        private void SetGps(bool on)
        {
            _gpsOn = on;
            _settings.Values["GpsOn"] = on;
            if (on) { var _ = _gps.StartAsync(); } else { _gps.Stop(); Log("GPS: off"); }
        }

        private void UnitsBox_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingGpsUi || !(UnitsBox.SelectedItem is ChoiceOption c)) return;
            _units = UnitModes[c.Value];
            _settings.Values["SpeedUnits"] = _units;
        }

        private void SubtitlesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingGpsUi) return;
            _recorder.Subtitles = SubtitlesToggle.IsOn;
            _settings.Values["RecSubtitles"] = SubtitlesToggle.IsOn;
            Log("Subtitles in recordings " + (SubtitlesToggle.IsOn ? "on" : "off") + " (from the next file)");
        }

        /// <summary>Once a second (from UpdateSafetyUi): speed next to the REC timer, and the GPS line.</summary>
        private void UpdateGpsUi()
        {
            var f = _gps.Running ? _gps.Latest : null;
            if (f.HasValue && f.Value.SpeedMs.HasValue && _recorder.State == "recording")
                RecStateText.Text += "   " + SpeedText(f.Value.SpeedMs.Value).ToUpperInvariant();
            GpsText.Text = !_gpsOn ? "GPS is off"
                : f.HasValue ? string.Format(CultureInfo.InvariantCulture, "GPS: {0}{1}  (within {2} m)",
                    f.Value.SpeedMs.HasValue ? SpeedText(f.Value.SpeedMs.Value) + "  -  " : "", Coords(f.Value), Math.Round(f.Value.Accuracy))
                : "GPS: " + _gps.Status;
        }

        // ---------------------------------------------------------------- web / USB config

        /// <summary>gps=on|off, units=auto|kmh|mph, subtitles=on|off. Network thread.</summary>
        private async Task ApplyGpsConfigAsync(IDictionary<string, string> form, List<string> done)
        {
            bool ui = false;
            if (form.TryGetValue("gps", out var g))
            {
                bool on = IsOn(g);
                await RunUiAsync(() => { SetGps(on); return Task.CompletedTask; }); // location access is asked on the UI thread
                done.Add("GPS " + (on ? "on" : "off"));
                ui = true;
            }
            if (form.TryGetValue("units", out var u))
            {
                u = u.Trim().ToLowerInvariant();
                if (Array.IndexOf(UnitModes, u) < 0) done.Add("units: use auto, kmh or mph");
                else { _units = u; _settings.Values["SpeedUnits"] = u; done.Add("speed in " + (Mph ? "mph" : "km/h")); ui = true; }
            }
            if (form.TryGetValue("subtitles", out var st))
            {
                _recorder.Subtitles = IsOn(st);
                _settings.Values["RecSubtitles"] = _recorder.Subtitles;
                done.Add("subtitles " + (_recorder.Subtitles ? "on" : "off") + " (from the next file)");
                ui = true;
            }
            if (ui)
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    _loadingGpsUi = true;
                    GpsToggle.IsOn = _gpsOn;
                    SubtitlesToggle.IsOn = _recorder.Subtitles;
                    SelectChoice(UnitsBox, Array.IndexOf(UnitModes, _units));
                    _loadingGpsUi = false;
                });
        }

        private string GpsConfigText() =>
            "gps=" + (_gpsOn ? "on" : "off") + " (" + _gps.Status + "), units=" + _units + " (" + (Mph ? "mph" : "km/h") + "), subtitles=" + (_recorder.Subtitles ? "on" : "off") + "\n";

        private void AddGpsJson(JsonObject j)
        {
            var f = _gps.Running ? _gps.Latest : null;
            var g = new JsonObject();
            g["on"] = JsonValue.CreateBooleanValue(_gpsOn);
            g["status"] = JsonValue.CreateStringValue(_gps.Status);
            g["fix"] = JsonValue.CreateBooleanValue(f.HasValue);
            g["units"] = JsonValue.CreateStringValue(_units);
            g["unitText"] = JsonValue.CreateStringValue(Mph ? "mph" : "km/h");
            g["subtitles"] = JsonValue.CreateBooleanValue(_recorder.Subtitles);
            if (f.HasValue)
            {
                var fx = f.Value;
                g["speed"] = fx.SpeedMs.HasValue ? JsonValue.CreateNumberValue(Math.Round(fx.SpeedMs.Value * (Mph ? 2.236936 : 3.6))) : JsonValue.CreateNullValue();
                g["lat"] = JsonValue.CreateNumberValue(Math.Round(fx.Lat, 6));
                g["lon"] = JsonValue.CreateNumberValue(Math.Round(fx.Lon, 6));
                g["accuracy"] = JsonValue.CreateNumberValue(Math.Round(fx.Accuracy));
                g["heading"] = fx.Heading.HasValue ? JsonValue.CreateStringValue(Compass(fx.Heading.Value)) : JsonValue.CreateNullValue();
            }
            j["gps"] = g;
        }
    }
}
