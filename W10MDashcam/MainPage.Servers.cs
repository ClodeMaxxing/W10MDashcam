using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace W10MDashcam
{
    /// <summary>
    /// The web page and the log pages can be switched off (the app is a dashcam first: a fresh install starts with both
    /// off and records only). The stats page (/stats) shows the last 12 hours of status samples as graphs, like the
    /// PC's soak report. (The "charger insufficient" notice lives in MainPage.Power.cs.)
    /// </summary>
    public sealed partial class MainPage
    {
        private bool _webOn = true, _logOn = true, _loadingServerUi = true;
        private List<string> _ips = new List<string>();

        /// <summary>From LoadRecordingSettings (constructor, before the servers start).</summary>
        private void LoadServerSettings()
        {
            // Off until switched on. (Before 0.8 an install made before these switches existed had them switched on
            // automatically; only development builds before 0.7.6 were affected, but nothing opens by itself now.)
            _webOn = _settings.Values["WebServerOn"] is bool w && w;
            _logOn = _settings.Values["LogServerOn"] is bool l && l;
            bool deleteSaved = _settings.Values["WebDeleteSaved"] is bool d && d;
            _api.WebEnabled = _webOn;
            _api.LogEnabled = _logOn;
            _api.AllowDeleteSaved = deleteSaved;
            _api.GetStatsRows = StatsRows;
            WebServerToggle.IsOn = _webOn;
            LogServerToggle.IsOn = _logOn;
            WebDeleteSavedToggle.IsOn = deleteSaved;
            LogServerToggle.IsEnabled = WebDeleteSavedToggle.IsEnabled = _webOn;
            _loadingServerUi = false;
        }

        private async void WebServerToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingServerUi) return;
            await SetWebAsync(WebServerToggle.IsOn);
        }

        private async Task SetWebAsync(bool on)
        {
            _webOn = on;
            _settings.Values["WebServerOn"] = on;
            _api.WebEnabled = on;
            if (on) { try { await _logServer.StartAsync(LogPort); } catch (Exception ex) { Log("Web server: " + ex.Message); } }
            else _logServer.Stop();
            Log("Web page " + (on ? "on" : "off"));
            UpdateServerUi();
        }

        private void WebDeleteSavedToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingServerUi) return;
            bool on = WebDeleteSavedToggle.IsOn;
            _settings.Values["WebDeleteSaved"] = on;
            _api.AllowDeleteSaved = on;
            Log("Deleting saved clips from the web page " + (on ? "allowed" : "not allowed"));
        }

        private void LogServerToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingServerUi) return;
            SetLog(LogServerToggle.IsOn);
        }

        private void SetLog(bool on)
        {
            _logOn = on;
            _settings.Values["LogServerOn"] = on;
            _api.LogEnabled = on;
            Log("Log and stats pages " + (on ? "on" : "off"));
            UpdateServerUi();
        }

        /// <summary>The web page addresses under Network and login (or that it is off).</summary>
        private void UpdateServerUi()
        {
            LogServerToggle.IsEnabled = WebDeleteSavedToggle.IsEnabled = _webOn;
            if (!_webOn) { WebAddressText.Text = "Off - switch it on below"; return; }
            var lines = _ips.Select(ip => "http://" + ip + ":" + LogPort + "/camera").ToList();
            if (lines.Count == 0) lines.Add("(not on Wi-Fi)");
            lines.Add("USB: http://127.0.0.1:" + UsbPort + "/camera");
            if (_logOn && _ips.Count > 0) lines.Add("Log: http://" + _ips[0] + ":" + LogPort + "/log  ·  stats: /stats");
            WebAddressText.Text = string.Join("\n", lines);
        }

        /// <summary>log=on|off (the web page can switch the log pages; the web page itself only on the phone). Network thread.</summary>
        private async Task ApplyServerConfigAsync(IDictionary<string, string> form, List<string> done)
        {
            if (!form.TryGetValue("log", out var lg)) return;
            bool on = IsOn(lg);
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                _loadingServerUi = true;
                LogServerToggle.IsOn = on;
                _loadingServerUi = false;
                SetLog(on);
            });
            done.Add("log and stats pages " + (on ? "on" : "off"));
        }

        private string ServerConfigText() => "log=" + (_logOn ? "on" : "off") + " (the log and stats pages; the web page itself is switched on the phone)\n";

        private void AddServerJson(JsonObject o)
        {
            o["webOn"] = JsonValue.CreateBooleanValue(_webOn);
            o["logOn"] = JsonValue.CreateBooleanValue(_logOn);
            o["statsOn"] = JsonValue.CreateBooleanValue(_logOn); // one switch for both pages in the dashcam
        }

        /// <summary>
        /// Once a second (UpdateStatusCard): the four ways in. Green = reachable now, grey = not, with the reason on the right.
        /// The RTSP rows are green only while the live view is on. USB is the honest case: the phone cannot see the cable as a
        /// network link (IP over USB arrives as loopback), so a USB row is green once a PC has talked to it (an RTSP viewer, or
        /// a web request in the last 30 s), amber "cable?" on external power with nothing connected, grey "no USB" on battery.
        /// </summary>
        private void UpdateServicePills(bool streaming)
        {
            var grey = Brush(0x55, 0x55, 0x55); var amber = AmberFill;
            var litText = Brush(0x9C, 0xF0, 0xB0); var dimText = Brush(0x7F, 0x9A, 0x86);
            string ip = _ips.Count > 0 ? _ips[0] : null;
            bool live = streaming && _wantLive;
            int usbViewers = _server.ViewerCount(true), wifiViewers = _server.ViewerCount(false);
            bool usbSeen = usbViewers > 0 || (DateTime.Now - _api.LastUsbRequest).TotalSeconds < 30;
            bool cable = OnExternalPower(_battery);
            string usbNote = usbSeen ? "" : cable ? "cable?" : "no USB";
            var usbDot = usbSeen ? GreenFill : cable ? amber : grey;
            string vt(int n) => n > 0 ? n + (n == 1 ? " viewer" : " viewers") : "";

            SvcWifiDot.Fill = live && ip != null ? GreenFill : grey;
            SvcWifiText.Foreground = live && ip != null ? litText : dimText;
            SvcWifiText.Text = ip != null ? "rtsp://" + ip + ":" + RtspPort + StreamPath : "";
            SvcWifiNote.Text = ip == null ? "no Wi-Fi" : !_wantLive ? "live view off" : !streaming ? "starting" : vt(wifiViewers);

            SvcUsbDot.Fill = live ? usbDot : grey;
            SvcUsbText.Foreground = live && usbSeen ? litText : dimText;
            SvcUsbText.Text = "rtsp://127.0.0.1:" + UsbPort + StreamPath;
            SvcUsbNote.Text = !_wantLive ? "live view off" : !streaming ? "starting" : usbViewers > 0 ? vt(usbViewers) : usbNote;

            SvcWebDot.Fill = _webOn && ip != null ? GreenFill : grey;
            SvcWebText.Foreground = _webOn && ip != null ? litText : dimText;
            SvcWebText.Text = ip != null ? "http://" + ip + ":" + LogPort + "/camera" : "";
            SvcWebNote.Text = !_webOn ? "off" : ip == null ? "no Wi-Fi" : "";

            SvcWebUsbDot.Fill = _webOn ? usbDot : grey;
            SvcWebUsbText.Foreground = _webOn && usbSeen ? litText : dimText;
            SvcWebUsbText.Text = "http://127.0.0.1:" + UsbPort + "/camera";
            SvcWebUsbNote.Text = !_webOn ? "off" : usbNote;
        }

        /// <summary>A tap on a service row opens the Network and login drawer, where the addresses and switches are.</summary>
        private void ServicePill_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e) => SetFold(NetworkFold, true);
        // ---------------------------------------------------------------- stats samples (for /stats)

        private const int StatsKeep = 1440; // 12 hours at one sample every 30 s
        private readonly Queue<string> _statRows = new Queue<string>();
        private readonly object _statLock = new object();
        private DateTime _lastStatSample = DateTime.MinValue;

        private static string F(double? v, string fmt = "0.##") => v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "";

        /// <summary>Once a second (UpdateRecordingUi): one row every 30 s, the same columns as the stats page, only while that page is on.</summary>
        private void SampleStats()
        {
            if (!_logOn || (DateTime.Now - _lastStatSample).TotalSeconds < 30) return;
            _lastStatSample = DateTime.Now;
            try
            {
                var r = _battery;
                bool charging = IsCharging(r);
                bool streaming = _camera != null && _camera.IsStreaming;
                var g = _gps.Running ? _gps.Latest : null;
                double? speed = g.HasValue && g.Value.SpeedMs.HasValue ? (double?)Math.Round(g.Value.SpeedMs.Value * (Mph ? 2.236936 : 3.6)) : null;
                var rec = _recorder;
                // time,ok,version,recording,live,fps,mbps,format,bitrate,battery_pct,charging,net_w,draw_w,heat_warn,protect,
                // cpu_phone,cpu_app,memory_mb,wifi_bars,uptime_s,write_mbps,dropped,free_gb,speed
                string row = string.Join(",", new[]
                {
                    DateTime.Now.ToString("s", CultureInfo.InvariantCulture), "1", AppVersion,
                    rec.State == "recording" ? "1" : "0", streaming && _wantLive ? "1" : "0",
                    streaming ? F(_camera.Fps, "0.0") : "", streaming ? F(_camera.Mbps, "0.0") : "",
                    _currentFormatKey ?? "", _currentBitrateKey ?? "",
                    F(Percent(r)), charging ? "1" : "0",
                    F(_power.NetWatts), charging ? "" : F(_power.DrawWatts),
                    _heatWarn ? "1" : "0", _protect ? "1" : "0",
                    F(_power.SysCpuPct, "0"), F(_power.ProcCpuPct, "0"),
                    F(AppMemory() / 1048576.0, "0"), F(WifiBars() >= 0 ? (double?)WifiBars() : null),
                    F((DateTime.Now - _appStarted).TotalSeconds, "0"),
                    rec.State == "recording" ? F(rec.WriteMBps) : "", F(rec.DroppedFrames),
                    rec.FreeBytes >= 0 ? F(rec.FreeBytes / 1073741824.0) : "", F(speed)
                }.Select(x => (x ?? "").Replace(",", ";").Replace("\"", ""))); // a comma inside a field would shift the columns
                lock (_statLock)
                {
                    _statRows.Enqueue(row);
                    while (_statRows.Count > StatsKeep) _statRows.Dequeue();
                }
            }
            catch (Exception ex) { Log("Stats sample: " + ex.Message); }
        }

        /// <summary>The rows for the stats page (network thread).</summary>
        private string StatsRows()
        {
            lock (_statLock) return string.Join(",\n", _statRows.Select(x => "\"" + x + "\""));
        }
    }
}
