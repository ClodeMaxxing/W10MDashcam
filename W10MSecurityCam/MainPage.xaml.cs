using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using W10MSecurityCam.Rtsp;
using W10MSecurityCam.Streaming;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Devices.Power;
using Windows.Graphics.Display;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Storage;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace W10MSecurityCam
{
    /// <summary>
    /// The single screen of the app: camera control, settings, blackout mode, crash guard and logging.
    /// The camera/encoder work lives in Streaming\CameraEngine.cs; the RTSP server in Rtsp\.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private const int RtspPort = 8554;  // rtsp://PHONE-IP:8554/live over Wi-Fi
        private const int UsbPort = 8020;   // same RTSP server over USB; Microsoft's IP-over-USB service maps PC 127.0.0.1:8020 to it
        private const int LogPort = 5001;   // http://PHONE-IP:5001 shows the log (the RTSP ports also answer an HTTP "GET" with the log)
        private const string StreamPath = "/live";

        private CameraEngine _camera;
        private readonly RtspServer _server = new RtspServer(RtspPort, UsbPort);
        private readonly LogServer _logServer;
        private readonly HttpApi _api = new HttpApi();
        private readonly CameraTuning _tuning = new CameraTuning();
        private readonly DispatcherTimer _tuningTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        // True until the camera's ranges are on screen: sliders fire ValueChanged while the page is built
        // (the white-balance slider snaps to its minimum), which must not overwrite the saved values.
        private bool _loadingTuningUi = true;
        private DateTime _lastTuningLog = DateTime.MinValue;
        private readonly DispatcherTimer _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        private readonly DateTime _appStarted = DateTime.Now;
        private string _lastRestartReason = "";
        private int _restarts;
        private bool _safeMode;
        private DateTime _lastPowerWarning = DateTime.MinValue;
        private readonly DisplayRequest _displayRequest = new DisplayRequest();
        private readonly DispatcherTimer _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _wakeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        private readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;

        private bool _busy;
        private DateTime _busySince;
        private bool _keepAwake;
        private BrightnessOverride _brightness;
        private bool _blackedOut;

        // crash guard + stall watchdog
        private DateTime _startedAt = DateTime.MinValue;
        private int _nativeStalls;
        private DateTime _lastStall = DateTime.MinValue;

        private string _currentFormat, _currentBitrate, _currentFormatKey = "", _currentBitrateKey = "";
        private volatile string _formatList = "";

        private sealed class KeyframeOption
        {
            public double Seconds;
            public override string ToString() => Seconds + " s";
        }

        private sealed class BitrateOption
        {
            public uint Bps; // 0 = auto
            public override string ToString() => Bps == 0 ? "Auto" : (Bps / 1e6).ToString("0.#") + " Mbps";
        }

        /// <summary>
        /// Startup self-check. A launch that never reached 20 s of healthy running (or a clean suspend) counts as a failed launch.
        /// After two failed launches in a row the saved settings are wiped so the app starts with defaults instead of crashing again.
        /// The stream login is kept (it lives in the password vault, not in these settings).
        /// </summary>
        private void LaunchSelfCheck()
        {
            try
            {
                int fails = _settings.Values["LaunchFailures"] is int lf ? lf : 0;
                if (_settings.Values["LaunchInProgress"] is bool lip && lip) fails++; else fails = 0;
                if (fails >= 2)
                {
                    _settings.Values.Clear();
                    fails = 0;
                    _selfCheckReset = true;
                }
                _settings.Values["LaunchFailures"] = fails;
                _settings.Values["LaunchInProgress"] = true;
            }
            catch { }
        }

        private bool _selfCheckReset, _launchCleared;

        private void MarkLaunchHealthy()
        {
            if (_launchCleared) return;
            _launchCleared = true;
            try { _settings.Values["LaunchInProgress"] = false; _settings.Values["LaunchFailures"] = 0; } catch { }
        }

        public MainPage()
        {
            RotateLogFile();
            LaunchSelfCheck();
            InitializeComponent();

            var v = Package.Current.Id.Version;
            TitleText.Text = "W10MSecurityCam " + v.Major + "." + v.Minor;

            Application.Current.UnhandledException += (s, e) => Log("CRASH (unhandled): " + e.Exception);
            _server.Log = Log;
            _server.Auth.Load();
            _server.Auth.AllowBasic = _settings.Values["AllowBasic"] is bool ab && ab;
            _server.AllowUdp = _settings.Values["AllowUdp"] is bool au && au;
            _api.GetLog = GetFullLog;
            _api.GetStatusJson = GetStatusJson;
            _api.GetConfig = GetConfigText;
            _api.GetCameraJson = GetCameraJson;
            _api.ApplyConfig = ApplyConfigAsync;
            _api.Auth = _server.Auth;
            _api.GetSnapshotJpeg = GetSnapshotAsync;
            _snapshotsOn = _settings.Values["Snapshots"] is bool sn && sn;
            _audioOn = _settings.Values["Audio"] is bool ao && ao;
            _tuning.Log = Log;
            if (_settings.Values["CamAuto"] is bool ca) _tuning.Auto = ca; // 1.6.0 / 1.6.1: one switch for all three
            if (_settings.Values["CamFocusAuto"] is bool cfa) _tuning.FocusAuto = cfa;
            if (_settings.Values["CamExposureAuto"] is bool cea) _tuning.ExposureAuto = cea;
            if (_settings.Values["CamWbAuto"] is bool cwa) _tuning.WbAuto = cwa;
            if (_settings.Values["CamFocus"] is double cf) _tuning.FocusPercent = cf;
            if (_settings.Values["CamExposureMs"] is double ce) _tuning.ExposureMs = ce;
            if (_settings.Values["CamWbK"] is int cw) _tuning.WhiteBalanceKelvin = (uint)cw;
            _tuningTimer.Tick += async (s, e) => { _tuningTimer.Stop(); await ApplyTuningAsync(); };
            _server.Http = _api;
            _logServer = new LogServer(_api);
            _healthTimer.Tick += (s, e) => { CheckPower(); CheckMemory(); };
            _statsTimer.Tick += (s, e) => UpdateStats();
            _wakeTimer.Tick += WakeTimer_Tick;
            Window.Current.VisibilityChanged += (s, e) => { if (!e.Visible) Log("App hidden (screen locked or switched away)"); };

            _highBw = _settings.Values["HighBandwidth"] is bool hb0 && hb0;
            HighBwToggle.IsOn = _highBw;
            RebuildBitrateList(); // selects 4 Mbps: plenty for 1080p of a mostly still scene
            foreach (var k in new[] { 0.5, 1.0, 2.0, 3.0, 4.0 }) KeyframeBox.Items.Add(new KeyframeOption { Seconds = k });
            KeyframeBox.SelectedIndex = 2; // 2 s: NVRs cut recordings at keyframes; longer saves bandwidth
            UpdateLoginUi();
            UdpToggle.IsOn = _server.AllowUdp;
            SnapshotToggle.IsOn = _snapshotsOn;
            AudioToggle.IsOn = _audioOn;

            Application.Current.Suspending += OnSuspending;
            Application.Current.Resuming += OnResuming;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            DisplayInformation.AutoRotationPreferences = DisplayOrientations.Landscape;
            await StartCameraAsync();
        }

        // ------------------------------------------------------------------ startup

        private async Task StartCameraAsync()
        {
            try
            {
                // Servers first, so the log is reachable even if the camera fails.
                try { await _server.StartAsync(); } catch (Exception ex) { Log("Video server: " + ex.Message); }
                try { await _logServer.StartAsync(LogPort); } catch (Exception ex) { Log("Log server: " + ex.Message); }

                if (_selfCheckReset) { _selfCheckReset = false; Log("SELF-CHECK: the app failed to start twice in a row, so its saved settings were reset to defaults (the stream login is kept)."); }
                Log("Starting camera...");
                _camera = new CameraEngine { Log = Log, AudioRequested = _audioOn };
                _camera.AccessUnitReady += _server.SendAccessUnit;
                _camera.ParameterSetsReady += _server.SetParameterSets;
                _camera.AudioFrameReady += _server.SendAudio;
                _camera.AudioFormatReady += _server.SetAudioFormat;
                await _camera.InitializeAsync();

                var formats = _camera.GetRecordFormats();
                ResolutionBox.Items.Clear();
                foreach (var f in formats) ResolutionBox.Items.Add(f);
                Log(formats.Count + " recording formats, up to " + (formats.Count > 0 ? formats[0].ToString() : "none"));

                Log("Formats: " + string.Join(", ", formats.Select(f => f.ToString().Replace(" fps", ""))));
                _formatList = string.Join(", ", formats.Select(FormatKey));

                // Security-camera defaults (v1.2+): 1080p at the lowest offered rate of 15 fps or more, 4 Mbps, keyframe every 2 s.
                // Settings saved by older versions used different lists, so they are reset once.
                if (!(_settings.Values["DefaultsVersion"] is int dv) || dv < 2)
                {
                    _settings.Values.Remove("Format");
                    _settings.Values.Remove("Bitrate");
                    _settings.Values.Remove("Keyframe");
                    _settings.Values["DefaultsVersion"] = 2;
                    Log("Applied security-camera defaults (1080p, 4 Mbps, keyframe every 2 s)");
                }
                string savedFormat = _settings.Values["Format"] as string;
                ResolutionBox.SelectedItem = formats.FirstOrDefault(f => f.ToString() == savedFormat)
                    ?? formats.Where(f => f.Width == 1920 && f.Height == 1080 && f.Fps >= 14.5).OrderBy(f => f.Fps).FirstOrDefault()
                    ?? formats.FirstOrDefault();
                if (_settings.Values["BitrateBps"] is int savedBps && savedBps >= 0)
                {
                    _applyingSettings = true; // restoring, not a user change
                    ApplyVideoSettings(null, savedBps == 0 ? "auto" : (savedBps / 1e6).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    _applyingSettings = false;
                }
                else if (_settings.Values["Bitrate"] is int savedBitrate && savedBitrate >= 0 && savedBitrate < BitrateBox.Items.Count)
                    BitrateBox.SelectedIndex = savedBitrate;
                if (_settings.Values["Keyframe"] is int kf && kf >= 0 && kf < KeyframeBox.Items.Count)
                    KeyframeBox.SelectedIndex = kf;
                AutoStartToggle.IsOn = !(_settings.Values["AutoStart"] is bool b0) || b0;
                PowerSaveToggle.IsOn = !(_settings.Values["PowerSave"] is bool b1) || b1;

                bool crashedLastTime = _settings.Values["StartInProgress"] is bool sip && sip;
                bool autoStarting = AutoStartToggle.IsOn && !crashedLastTime;

                // Crash guard for camera settings: if the app died while applying them last time, a saved
                // manual value may be the cause, so go back to automatic instead of crashing again.
                if (_settings.Values["TuningInProgress"] is bool tip && tip)
                {
                    _settings.Values["TuningInProgress"] = false;
                    _tuning.Auto = true;
                    SaveTuning();
                    Log("Camera settings reset to automatic: the app closed while applying them last time.");
                }

                PreviewElement.Source = _camera.Capture;
                // Don't spin the preview up just to switch it off again: starting 4K recording while the
                // preview is toggled right after camera power-up crashes the camera pipeline.
                if (PreviewWanted && !(autoStarting && PowerSaveToggle.IsOn && _tuning.Auto)) await _camera.SetPreviewAsync(true);

                _tuning.Describe(_camera.Capture);
                LoadTuningUi();
                PowerSaveToggle.IsEnabled = PreviewToggle.IsEnabled = _tuning.Auto;
                await ApplyTuningAsync(true);

                ShowAddresses();
                StartButton.IsEnabled = formats.Count > 0;
                _statsTimer.Start();
                _healthTimer.Start();
                CheckPower();
                Log("Ready.");

                if (crashedLastTime)
                {
                    // Crash guard: the last launch died while starting a stream. Don't auto-start, and use the
                    // proven MP4 engine for this launch only.
                    _settings.Values["StartInProgress"] = false;
                    _camera.UseNativeSink = false;
                    _safeMode = true;
                    _lastRestartReason = "safe mode after a crash while starting";
                    Log("SAFE MODE: the app closed unexpectedly while starting the stream last time.");
                    Log("Auto-start skipped; the standard (MP4) engine will be used until the app is reopened.");
                }
                else if (autoStarting && StartButton.IsEnabled)
                {
                    Log("Auto-start: letting the camera settle for 2 s");
                    await Task.Delay(2000);
                    await ToggleStreamingAsync();
                }
            }
            catch (Exception ex)
            {
                Log("ERROR starting camera: " + ex.Message);
            }
        }

        private void ShowAddresses()
        {
            var ips = NetworkInformation.GetHostNames()
                .Where(h => h.Type == HostNameType.Ipv4 && h.IPInformation != null)
                .Select(h => h.CanonicalName)
                .Where(ip => !ip.StartsWith("169.254.")) // link-local addresses aren't reachable from the PC
                .ToList();
            var lines = ips.Select(ip => "rtsp://" + ip + ":" + RtspPort + StreamPath).ToList();
            lines.Add("USB: rtsp://127.0.0.1:" + UsbPort + StreamPath);
            lines.Add("USB settings page: http://127.0.0.1:" + UsbPort + "/camera");
            AddressText.Text = string.Join("\n", lines);
            UpdateLoginUi();
            Log("Addresses: " + string.Join("  ", lines) + (ips.Count > 0 ? "  (log: http://" + ips[0] + ":" + LogPort + ")" : ""));
        }

        // ------------------------------------------------------------------ start / stop

        private async void StartButton_Click(object sender, RoutedEventArgs e) => await ToggleStreamingAsync();

        private async void Setting_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_applyingSettings) SyncVideoKeys();
            if (_applyingSettings) return; // a USB config change restarts the stream once, itself
            if (_camera == null || !_camera.IsStreaming || _busy) return;
            Log("Setting changed - restarting stream");
            await ToggleStreamingAsync(); // stop
            await ToggleStreamingAsync(); // start with the new setting
        }

        /// <summary>Keeps the format/bitrate the web page shows in step with the dropdowns (not only when a stream starts).</summary>
        private void SyncVideoKeys()
        {
            if (ResolutionBox.SelectedItem is FormatOption f) _currentFormatKey = FormatKey(f);
            if (BitrateBox.SelectedItem is BitrateOption b)
                _currentBitrateKey = b.Bps == 0 ? "auto" : (b.Bps / 1e6).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }

        private async Task ToggleStreamingAsync()
        {
            if (_busy && (DateTime.Now - _busySince).TotalSeconds > 30)
            {
                Log("Previous start/stop never finished - unlocking controls");
                _busy = false;
            }
            if (_busy || _camera == null) return;
            _busy = true;
            _busySince = DateTime.Now;
            StartButton.IsEnabled = false;
            try
            {
                if (!_camera.IsStreaming)
                {
                    var f = ResolutionBox.SelectedItem as FormatOption;
                    if (f == null) { Log("Pick a resolution first"); return; }
                    if (!_camera.UseNativeSink && (long)f.Width * f.Height > 1920L * 1080)
                    {
                        // The MP4 fallback engine ignores the bitrate setting, so keep it at 1080p to stay within Wi-Fi limits.
                        var capped = ResolutionBox.Items.OfType<FormatOption>()
                            .Where(o => o.Width == 1920 && o.Height == 1080).OrderBy(o => Math.Abs(o.Fps - f.Fps)).FirstOrDefault();
                        if (capped != null) { Log("Standard (MP4) engine: using " + capped + " instead of " + f); f = capped; }
                    }
                    var b = BitrateBox.SelectedItem as BitrateOption;
                    uint bps = (b == null || b.Bps == 0) ? CameraEngine.AutoBitrate(f, _highBw) : b.Bps;
                    if (KeyframeBox.SelectedItem is KeyframeOption ko) _camera.KeyframeSeconds = ko.Seconds;
                    _settings.Values["Format"] = f.ToString();
                    _currentFormat = f.ToString();
                    _currentFormatKey = FormatKey(f);
                    _currentBitrateKey = (b == null || b.Bps == 0) ? "auto" : (b.Bps / 1e6).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                    _currentBitrate = (b == null || b.Bps == 0) ? "Auto (" + (bps / 1e6).ToString("0.#") + " Mbps)" : b.ToString();
                    _settings.Values["BitrateBps"] = (int)(b?.Bps ?? 0);
                    _settings.Values["Bitrate"] = BitrateBox.SelectedIndex;
                    _settings.Values["Keyframe"] = KeyframeBox.SelectedIndex;

                    _settings.Values["StartInProgress"] = true; // cleared after 15 s of healthy streaming
                    _startedAt = DateTime.Now;
                    _streamBeganAt = DateTime.Now;
                    _targetFps = f.Fps;
                    // Switch the preview off BEFORE recording starts; changing it mid-recording stalls the 4K pipeline.
                    if (PowerSaveToggle.IsOn && _tuning.Auto) await _camera.SetPreviewAsync(false);
                    _server.ResetStream(); // connected viewers wait for the new stream's first keyframe
                    await _camera.StartStreamingAsync(f, bps);
                    _tuning.FrameLimitMs = 1000.0 / Math.Max(1, f.Fps);
                    await ApplyTuningAsync(true);
                    StartButton.Content = "Stop streaming";
                    await ApplyBlackoutAsync();
                    if (!_keepAwake) { _displayRequest.RequestActive(); _keepAwake = true; }
                }
                else
                {
                    await _camera.StopStreamingAsync();
                    ClearStartFlag();
                    StopUi();
                    await ApplyBlackoutAsync();
                }
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                try { await _camera.StopStreamingAsync(); } catch { }
                ClearStartFlag();
                StopUi();
            }
            finally
            {
                StartButton.IsEnabled = true;
                _busy = false;
            }
        }

        private void ClearStartFlag()
        {
            _settings.Values["StartInProgress"] = false;
            _startedAt = DateTime.MinValue;
        }

        private void StopUi()
        {
            StartButton.Content = "Start streaming";
            if (_keepAwake) { _displayRequest.RequestRelease(); _keepAwake = false; }
        }

        // ------------------------------------------------------------------ blackout (battery saver)

        /// <summary>While streaming: preview off, screen at minimum brightness, full black overlay (tap to wake for 15 s).</summary>
        /// <summary>
        /// While any camera setting is manual the person is adjusting it and needs to see the picture, so the
        /// blackout is paused and the preview forced on (their saved toggles are left alone and come back
        /// when everything is automatic again).
        /// </summary>
        // The three display rules live here and nowhere else:
        //  - Adjusting: a camera setting is manual and the user has not asked for a blackout -> they need to see the picture.
        //  - Blackout: streaming, the power-save switch is on, and (everything automatic, or a blackout was asked for explicitly).
        //  - Preview: the user's switch, or forced on while adjusting.
        private bool Adjusting => !_tuning.Auto && !_blackoutForced;
        private bool BlackoutWanted => _camera != null && _camera.IsStreaming && PowerSaveToggle.IsOn && !Adjusting;
        private bool PreviewWanted => PreviewToggle.IsOn || Adjusting;

        /// <summary>One-click power save (phone button and web button): brightness 0, black screen, preview off.</summary>
        private async Task SetBlackoutAsync(bool on)
        {
            _blackoutForced = on;
            if (PowerSaveToggle.IsOn != on) PowerSaveToggle.IsOn = on; // its handler saves the setting and applies it
            else await ApplyBlackoutAsync();
            await UpdateDisplayForTuningAsync();
            UpdatePowerButton();
        }

        private async void PowerSaveButton_Click(object sender, RoutedEventArgs e)
        {
            await SetBlackoutAsync(!_blackedOut);
            if (!(_camera != null && _camera.IsStreaming)) Log("Power save is on; the screen goes black when streaming starts");
        }

        private void UpdatePowerButton()
        {
            PowerSaveButton.Content = _blackedOut ? "Wake the screen (blackout off)" : "Save power now: black out the screen";
        }

        private async Task UpdateDisplayForTuningAsync()
        {
            try
            {
                bool adjusting = Adjusting;
                PowerSaveToggle.IsEnabled = !adjusting;
                PreviewToggle.IsEnabled = !adjusting;
                await ApplyBlackoutAsync();
                if (!_blackedOut && _camera != null)
                {
                    await _camera.SetPreviewAsync(PreviewWanted);
                    PreviewOffText.Text = "Preview off";
                    PreviewOffText.Visibility = PreviewWanted ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            catch (Exception ex) { Log("Display update: " + ex.Message); }
        }

        private async Task ApplyBlackoutAsync()
        {
            bool want = BlackoutWanted;
            if (want == _blackedOut) { UpdatePowerButton(); return; }
            _blackedOut = want;
            try
            {
                if (_brightness == null) _brightness = BrightnessOverride.GetForCurrentView();
                if (want)
                {
                    if (_brightness.IsSupported)
                    {
                        _brightness.SetBrightnessLevel(0.0, DisplayBrightnessOverrideOptions.None);
                        _brightness.StartOverride();
                    }
                    if (_camera != null) await _camera.SetPreviewAsync(false);
                    BlackoutOverlay.Visibility = Visibility.Visible;
                }
                else
                {
                    _wakeTimer.Stop();
                    BlackoutOverlay.Visibility = Visibility.Collapsed;
                    if (_brightness.IsSupported) _brightness.StopOverride();
                    if (_camera != null && PreviewWanted) await _camera.SetPreviewAsync(true);
                    PreviewOffText.Text = "Preview off";
                    PreviewOffText.Visibility = PreviewWanted ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            catch (Exception ex) { Log("Blackout: " + ex.Message); }
            UpdatePowerButton();
        }

        private void BlackoutOverlay_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            BlackoutOverlay.Visibility = Visibility.Collapsed;
            if (_brightness != null && _brightness.IsSupported) _brightness.StopOverride();
            PreviewOffText.Text = "Streaming - preview off (blackout mode)";
            PreviewOffText.Visibility = Visibility.Visible;
            _wakeTimer.Stop();
            _wakeTimer.Start();
        }

        private void WakeTimer_Tick(object sender, object e)
        {
            _wakeTimer.Stop();
            if (!_blackedOut) return;
            BlackoutOverlay.Visibility = Visibility.Visible;
            if (_brightness != null && _brightness.IsSupported)
            {
                _brightness.SetBrightnessLevel(0.0, DisplayBrightnessOverrideOptions.None);
                _brightness.StartOverride();
            }
        }

        private async void PowerSaveToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _settings.Values["PowerSave"] = PowerSaveToggle.IsOn;
            await ApplyBlackoutAsync();
        }

        private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _settings.Values["AutoStart"] = AutoStartToggle.IsOn;
        }

        private async void PreviewToggle_Toggled(object sender, RoutedEventArgs e)
        {
            PreviewOffText.Visibility = PreviewWanted ? Visibility.Collapsed : Visibility.Visible;
            if (_camera != null && !_blackedOut) await _camera.SetPreviewAsync(PreviewWanted);
        }

        // ------------------------------------------------------------------ stats + watchdog

        private void UpdateStats()
        {
            if ((DateTime.Now - _appStarted).TotalSeconds > 20) MarkLaunchHealthy();
            if (DateTime.Now.Second % 2 == 0) UpdateStatusText();
            if (_camera == null) return;
            if (!_camera.IsStreaming)
            {
                StatsText.Text = "Idle · viewers: " + _server.ClientCount;
                UpdateHeatHint();
                return;
            }
            UpdateHeatHint();
            _camera.UpdateStats();
            CheckStall();
            if (DateTime.Now.Second % 5 == 0)
            {
                _tuning.Read(_camera.Capture);
                TuningStateText.Text = _tuning.State;
                if ((DateTime.Now - _lastTuningLog).TotalMinutes >= 10)
                {
                    // Lets you see exposure and ISO follow the daylight in the log.
                    _lastTuningLog = DateTime.Now;
                    Log("Camera now: " + _tuning.State);
                }
            }
            if (_startedAt != DateTime.MinValue && (DateTime.Now - _startedAt).TotalSeconds > 15)
                ClearStartFlag(); // ran fine for 15 s

            StatsText.Text = string.Format("{0:0} fps · {1:0.0} Mbps · viewers: {2}\nEngine: {3}{4}",
                _camera.Fps, _camera.Mbps, _server.ClientCount, _camera.PathName,
                _camera.LastLatencyMs >= 0 ? " · sensor→encoded " + _camera.LastLatencyMs + " ms" : "");
        }

        /// <summary>If the low-latency engine stops delivering frames, restart; after two stalls, use the MP4 engine.</summary>
        private async void CheckStall()
        {
            if (_busy || _camera == null || !_camera.IsStreaming || _camera.PathName != "low-latency") return;
            if (_camera.SecondsSinceLastFrame < 4) return;
            if ((DateTime.Now - _lastStall).TotalSeconds > 120) _nativeStalls = 0; // stalls far apart don't add up
            _lastStall = DateTime.Now;
            _nativeStalls++;
            Log("Low-latency engine stalled (no frames for 4 s) - restart #" + _nativeStalls);
            _restarts++;
            _lastRestartReason = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " encoder stalled";
            if (_nativeStalls >= 2)
            {
                Log("Switching to the standard (MP4) engine until the app is reopened");
                _camera.UseNativeSink = false;
            }
            await ToggleStreamingAsync();
            await ToggleStreamingAsync();
        }

        // ------------------------------------------------------------------ app lifecycle

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                _statsTimer.Stop();
                if (_camera != null) await _camera.ShutdownAsync();
                _camera = null;
                ClearStartFlag(); // a clean suspend is not a crash
                MarkLaunchHealthy();
                _server.Stop();
                _logServer.Stop();
                StopUi();
                _blackedOut = false;
                BlackoutOverlay.Visibility = Visibility.Collapsed;
            }
            finally { deferral.Complete(); }
        }

        private async void OnResuming(object sender, object e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await StartCameraAsync());
        }

        // ------------------------------------------------------------------ login

        private void UpdateLoginUi()
        {
            if (LoginStatusText == null) return;
            bool on = _server.Auth.Enabled;
            LoginStatusText.Text = _server.Auth.IsDefault
                ? "Default login in use (admin / admin) - please change the password"
                : "Login required (user \"" + _server.Auth.UserName + "\")";
            if (on && string.IsNullOrEmpty(UserBox.Text)) UserBox.Text = _server.Auth.UserName;
        }

        private void SaveLoginButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(PassBox.Password) && PassBox.Password.Length < 4) { Log("Password not changed: it needs at least 4 characters"); return; }
                _server.Auth.Save(UserBox.Text, PassBox.Password);
                PassBox.Password = "";
                Log(_server.Auth.IsDefault ? "Login reset to the default (admin / admin)" : "Login set for user \"" + _server.Auth.UserName + "\" (stream and web page)");
            }
            catch (Exception ex) { Log("Saving login failed: " + ex.Message); }
            UpdateLoginUi();
        }

        // ------------------------------------------------------------------ health: /status, power warnings, /config over USB

        private BatteryReport TryBattery()
        {
            try { return Battery.AggregateBattery.GetReport(); } catch { return null; }
        }

        private static int? Percent(BatteryReport r)
        {
            if (r == null || r.FullChargeCapacityInMilliwattHours == null || r.RemainingCapacityInMilliwattHours == null) return null;
            int full = r.FullChargeCapacityInMilliwattHours.Value;
            return full > 0 ? (int?)(r.RemainingCapacityInMilliwattHours.Value * 100 / full) : null;
        }

        /// <summary>A dead battery looks the same as a dead stream from the NVR's side, so warn early.</summary>
        // ------------------------------------------------------------------ focus / exposure / white balance

        private bool _applyingTuning;

        private bool _tuningPending, _tuningForce;

        /// <summary>
        /// Applies the camera settings. Changes that arrive while one is being applied are queued, not dropped,
        /// and run one after another (never two at once). force = resend everything (camera just (re)started).
        /// </summary>
        private async Task ApplyTuningAsync(bool force = false)
        {
            if (_camera?.Capture == null) return;
            if (force) _tuningForce = true;
            if (_applyingTuning) { _tuningPending = true; return; }
            _applyingTuning = true;
            _settings.Values["TuningInProgress"] = true; // cleared when the camera accepted the settings; see the crash guard at startup
            try
            {
                do
                {
                    _tuningPending = false;
                    bool f = _tuningForce; _tuningForce = false;
                    await _tuning.ApplyAsync(_camera.Capture, f);
                } while (_tuningPending);
                TuningStateText.Text = _tuning.State;
                _settings.Values["TuningInProgress"] = false;
            }
            catch (Exception ex) { Log("Camera tuning: " + ex.Message); }
            finally { _applyingTuning = false; }
        }

        private void SaveTuning()
        {
            _settings.Values["CamAuto"] = _tuning.Auto;
            _settings.Values["CamFocusAuto"] = _tuning.FocusAuto;
            _settings.Values["CamExposureAuto"] = _tuning.ExposureAuto;
            _settings.Values["CamWbAuto"] = _tuning.WbAuto;
            _settings.Values["CamFocus"] = _tuning.FocusPercent;
            _settings.Values["CamExposureMs"] = _tuning.ExposureMs;
            _settings.Values["CamWbK"] = (int)_tuning.WhiteBalanceKelvin;
        }

        // Exposure slider is logarithmic (0..100 -> shortest..longest shutter time), since useful values span 1000x.
        private double ExposureMaxForSlider => Math.Max(_tuning.ExposureMinUseful + 0.001, Math.Min(_tuning.ExposureMaxMs, _tuning.FrameLimitMs));
        private double SliderToExposure(double v) =>
            _tuning.ExposureMinUseful * Math.Pow(ExposureMaxForSlider / _tuning.ExposureMinUseful, v / 100.0);
        private double ExposureToSlider(double ms) =>
            100.0 * Math.Log(Math.Max(ms, _tuning.ExposureMinUseful) / _tuning.ExposureMinUseful) / Math.Log(ExposureMaxForSlider / _tuning.ExposureMinUseful);

        /// <summary>Puts the current settings and the camera's ranges on screen.</summary>
        private void LoadTuningUi()
        {
            _loadingTuningUi = true;
            try
            {
                AutoCameraCheck.IsChecked = _tuning.Auto;
                ManualCameraPanel.Visibility = _tuning.Auto ? Visibility.Collapsed : Visibility.Visible;
                FocusAutoCheck.IsChecked = _tuning.FocusAuto;
                ExposureAutoCheck.IsChecked = _tuning.ExposureAuto;
                WbAutoCheck.IsChecked = _tuning.WbAuto;
                FocusAutoCheck.IsEnabled = _tuning.FocusSupported;
                ExposureAutoCheck.IsEnabled = _tuning.ExposureSupported;
                WbAutoCheck.IsEnabled = _tuning.WhiteBalanceSupported;
                FocusSlider.IsEnabled = _tuning.FocusSupported && !_tuning.FocusAuto;
                ExposureSlider.IsEnabled = _tuning.ExposureSupported && !_tuning.ExposureAuto;
                WbSlider.IsEnabled = _tuning.WhiteBalanceSupported && !_tuning.WbAuto;
                FocusSlider.Value = _tuning.FocusPercent;
                if (_tuning.ExposureSupported) ExposureSlider.Value = Math.Max(0, Math.Min(100, ExposureToSlider(_tuning.ExposureMs)));
                if (_tuning.WhiteBalanceSupported)
                {
                    WbSlider.Minimum = _tuning.WbMin;
                    WbSlider.Maximum = Math.Max(_tuning.WbMin + 1, _tuning.WbMax);
                    WbSlider.Value = Math.Max(_tuning.WbMin, Math.Min(_tuning.WbMax, _tuning.WhiteBalanceKelvin));
                }
                UpdateTuningLabels();
            }
            finally { _loadingTuningUi = false; }
        }

        private void UpdateTuningLabels()
        {
            FocusLabel.Text = !_tuning.FocusSupported ? "Focus: not adjustable"
                : _tuning.FocusAuto ? "Focus: automatic" : "Focus: " + Math.Round(_tuning.FocusPercent) + " (0 near - 100 far)";
            ExposureLabel.Text = !_tuning.ExposureSupported ? "Exposure: not adjustable"
                : _tuning.ExposureAuto ? "Exposure: automatic"
                : string.Format("Exposure: {0:0.##} ms (1/{1:0} s)", _tuning.ExposureMs, 1000.0 / Math.Max(0.001, _tuning.ExposureMs));
            WbLabel.Text = !_tuning.WhiteBalanceSupported ? "White balance: not adjustable"
                : _tuning.WbAuto ? "White balance: automatic" : "White balance: " + _tuning.WhiteBalanceKelvin + " K";
        }

        private void AutoCameraCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingTuningUi) return;
            bool auto = AutoCameraCheck.IsChecked == true;
            // Unticking the main box makes all three manual, starting from what the camera is using right now.
            if (!auto) _tuning.SeedFromCamera(_camera?.Capture, _tuning.FocusAuto, _tuning.ExposureAuto, _tuning.WbAuto);
            _tuning.Auto = auto;
            TuningChangedFromUi();
        }

        private void SubAuto_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingTuningUi) return;
            bool on = ((CheckBox)sender).IsChecked == true;
            if (!on) _tuning.SeedFromCamera(_camera?.Capture, sender == FocusAutoCheck, sender == ExposureAutoCheck, sender == WbAutoCheck);
            if (sender == FocusAutoCheck) _tuning.FocusAuto = on;
            else if (sender == ExposureAutoCheck) _tuning.ExposureAuto = on;
            else if (sender == WbAutoCheck) _tuning.WbAuto = on;
            TuningChangedFromUi(); // all three on Auto ticks the main box and hides the panel
        }

        private bool _blackoutForced; // blackout requested from the web page: applies even while camera settings are manual

        private async void FocusOnceButton_Click(object sender, RoutedEventArgs e) => await FocusOnceAsync();

        /// <summary>Autofocus once, then hold that focus (manual at the found position).</summary>
        private async Task FocusOnceAsync()
        {
            if (_camera?.Capture == null) return;
            _blackoutForced = false;
            FocusOnceButton.IsEnabled = false;
            _settings.Values["TuningInProgress"] = true;
            try
            {
                await _tuning.FocusOnceAsync(_camera.Capture);
                _settings.Values["TuningInProgress"] = false;
                SaveTuning();
                LoadTuningUi();
                await UpdateDisplayForTuningAsync();
                await ApplyTuningAsync();
            }
            finally { FocusOnceButton.IsEnabled = true; }
        }

        private void TuningChangedFromUi()
        {
            _blackoutForced = false;
            LoadTuningUi();
            SaveTuning();
            var _ = UpdateDisplayForTuningAsync();
            _tuningTimer.Stop(); _tuningTimer.Start();
        }

        private void TuningSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_loadingTuningUi || FocusLabel == null) return;
            if (sender == FocusSlider) _tuning.FocusPercent = FocusSlider.Value;
            else if (sender == ExposureSlider) _tuning.ExposureMs = SliderToExposure(ExposureSlider.Value);
            else if (sender == WbSlider) _tuning.WhiteBalanceKelvin = (uint)Math.Round(WbSlider.Value / 100.0) * 100;
            UpdateTuningLabels();
            SaveTuning();
            _tuningTimer.Stop(); _tuningTimer.Start(); // apply once the slider stops moving
        }

        // ------------------------------------------------------------------ memory (to spot slow leaks in long runs)

        private ulong _peakMemory;
        private DateTime _lastMemoryLog = DateTime.MinValue;

        private static ulong AppMemory() { try { return Windows.System.MemoryManager.AppMemoryUsage; } catch { return 0; } }
        private static ulong AppMemoryLimit() { try { return Windows.System.MemoryManager.AppMemoryUsageLimit; } catch { return 0; } }

        private static int WifiBars()
        {
            try
            {
                var p = NetworkInformation.GetInternetConnectionProfile();
                if (p == null || !p.IsWlanConnectionProfile) return -1;
                var b = p.GetSignalBars();
                return b.HasValue ? b.Value : -1;
            }
            catch { return -1; }
        }

        /// <summary>Every minute: track peak memory; every 30 minutes (and when it grows past 75% of the limit) log it.</summary>
        private void CheckMemory()
        {
            ulong mem = AppMemory(), limit = AppMemoryLimit();
            if (mem > _peakMemory) _peakMemory = mem;
            bool high = limit > 0 && mem > limit * 3 / 4;
            if ((DateTime.Now - _lastMemoryLog).TotalMinutes >= 30 || (high && (DateTime.Now - _lastMemoryLog).TotalMinutes >= 5))
            {
                _lastMemoryLog = DateTime.Now;
                Log(string.Format("{0}Memory: {1:0.0} MB (peak {2:0.0}, limit {3:0}, managed {4:0.0}), uptime {5:0.0} h",
                    high ? "WARNING " : "", mem / 1048576.0, _peakMemory / 1048576.0, limit / 1048576.0,
                    GC.GetTotalMemory(false) / 1048576.0, (DateTime.Now - _appStarted).TotalHours));
            }
        }

        private void CheckPower()
        {
            var r = TryBattery();
            if (r == null) return;
            int? pct = Percent(r);
            bool discharging = r.Status == Windows.System.Power.BatteryStatus.Discharging;
            // A weak charger (e.g. a PC USB port) can report "Charging" while the battery still runs down.
            // Compare with the level 30 minutes ago.
            if (pct.HasValue)
            {
                _batteryHistory.Enqueue(Tuple.Create(DateTime.Now, pct.Value));
                while (_batteryHistory.Count > 0 && (DateTime.Now - _batteryHistory.Peek().Item1).TotalMinutes > 30) _batteryHistory.Dequeue();
            }
            var oldest = _batteryHistory.Count > 0 ? _batteryHistory.Peek() : null;
            bool fallingWhileCharging = !discharging && oldest != null && pct.HasValue &&
                                        (DateTime.Now - oldest.Item1).TotalMinutes >= 20 && pct.Value <= oldest.Item2 - 2;
            BatteryFalling = discharging || fallingWhileCharging;
            if ((discharging || fallingWhileCharging || pct < 15) && (DateTime.Now - _lastPowerWarning).TotalMinutes >= 10)
            {
                _lastPowerWarning = DateTime.Now;
                Log("POWER: " + (discharging ? "not charging" : fallingWhileCharging ? "charger too weak - battery still falling" : "battery low") +
                    (pct.HasValue ? " - battery " + pct + "%" : "") + (pct < 20 ? " - LOW BATTERY, use a wall charger" : ""));
            }
        }

        // ------------------------------------------------------------------ status block (phone screen and web page)

        private DateTime _streamBeganAt = DateTime.MinValue;
        private double _targetFps;
        private int _lowFpsSeconds;
        private string _heat = "Heat hint: not streaming.";
        private bool _heatWarn;

        /// <summary>
        /// Windows 10 Mobile gives apps no temperature reading. The only honest signal is indirect: a phone that is
        /// too hot throttles, and the frame rate falls below the selected rate for a long time.
        /// </summary>
        private void UpdateHeatHint()
        {
            var cam = _camera;
            if (cam == null || !cam.IsStreaming || _targetFps <= 0) { _lowFpsSeconds = 0; _heatWarn = false; _heat = "Heat hint: not streaming."; return; }
            if ((DateTime.Now - _streamBeganAt).TotalSeconds < 45) { _heat = "Heat hint: measuring..."; return; }
            if (cam.Fps < _targetFps * 0.8) _lowFpsSeconds++; else _lowFpsSeconds = Math.Max(0, _lowFpsSeconds - 1);
            _heatWarn = _lowFpsSeconds >= 30;
            _heat = _heatWarn
                ? "Frame rate has been low for a while: the phone may be hot, the light too dim for this rate, or the Wi-Fi/encoder overloaded."
                : "Frame rate is steady (no sign of overheating).";
        }

        private static string BatterySaverText()
        {
            try { return Windows.System.Power.PowerManager.EnergySaverStatus.ToString(); } catch { return "unknown"; }
        }

        private bool IsCharging(Windows.Devices.Power.BatteryReport r) =>
            r != null && (r.Status == Windows.System.Power.BatteryStatus.Charging || r.Status == Windows.System.Power.BatteryStatus.Idle);

        private void UpdateStatusText()
        {
            try
            {
                var r = TryBattery();
                int? pct = Percent(r);
                string bat = pct.HasValue ? pct + "% " : "";
                bat += r == null ? "unknown" : r.Status.ToString().ToLowerInvariant();
                int bars = WifiBars();
                bool low = pct.HasValue && pct.Value < 20 && !IsCharging(r);
                var sb = new System.Text.StringBuilder();
                sb.Append("Battery: ").Append(bat);
                if (low) sb.Append("  LOW - use a wall charger");
                else if (BatteryFalling) sb.Append("  (charger too weak)");
                sb.Append("\nWi-Fi: ").Append(bars >= 0 ? bars + " of 4 bars" : "n/a");
                sb.Append("   Memory: ").Append(Math.Round(AppMemory() / 1048576.0)).Append(" MB");
                sb.Append("\nUp: ").Append(FormatUptime(DateTime.Now - _appStarted));
                sb.Append("\nTemperature: not readable on this phone. ").Append(_heat);
                sb.Append("\nBattery Saver: ").Append(BatterySaverText());
                StatusText.Text = sb.ToString();
                StatusText.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(
                    (low || _heatWarn) ? Windows.UI.Colors.Salmon : Windows.UI.Colors.LightGray);
            }
            catch { }
        }

        private static string FormatUptime(TimeSpan t) => t.TotalHours >= 2 ? Math.Round(t.TotalHours, 1) + " h" : Math.Round(t.TotalMinutes) + " min";

        private readonly Queue<Tuple<DateTime, int>> _batteryHistory = new Queue<Tuple<DateTime, int>>();
        private bool BatteryFalling;

        /// <summary>Camera settings for the web page. Runs on a network thread: cached values only.</summary>
        private string GetCameraJson()
        {
            var o = new JsonObject();
            o["mode"] = JsonValue.CreateStringValue(_tuning.Mode);
            o["now"] = JsonValue.CreateStringValue(_tuning.State ?? "");
            o["focusAuto"] = JsonValue.CreateBooleanValue(_tuning.FocusAuto);
            o["exposureAuto"] = JsonValue.CreateBooleanValue(_tuning.ExposureAuto);
            o["wbAuto"] = JsonValue.CreateBooleanValue(_tuning.WbAuto);
            o["focus"] = JsonValue.CreateNumberValue(Math.Round(_tuning.FocusPercent));
            o["exposureMs"] = JsonValue.CreateNumberValue(Math.Round(_tuning.ExposureMs, 3));
            double hi = Math.Max(_tuning.ExposureMinUseful + 0.001, Math.Min(_tuning.ExposureMaxMs, _tuning.FrameLimitMs));
            o["expMin"] = JsonValue.CreateNumberValue(_tuning.ExposureMinUseful);
            o["expMax"] = JsonValue.CreateNumberValue(hi);
            // video settings and addresses, for the web page
            var fl = new JsonArray();
            foreach (var k in (_formatList ?? "").Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)) fl.Add(JsonValue.CreateStringValue(k));
            o["formats"] = fl;
            o["format"] = JsonValue.CreateStringValue(_currentFormatKey ?? "");
            var bl = new JsonArray();
            bl.Add(JsonValue.CreateStringValue("auto"));
            foreach (var m in BitrateMenuMbps()) bl.Add(JsonValue.CreateStringValue(m.ToString()));
            o["highBw"] = JsonValue.CreateBooleanValue(_highBw);
            o["blackout"] = JsonValue.CreateBooleanValue(_blackedOut);
            o["safeMode"] = JsonValue.CreateBooleanValue(_camera != null && !_camera.UseNativeSink);
            o["bitrates"] = bl;
            o["bitrate"] = JsonValue.CreateStringValue(_currentBitrateKey ?? "");
            o["rtspPort"] = JsonValue.CreateNumberValue(RtspPort);
            o["usbPort"] = JsonValue.CreateNumberValue(UsbPort);
            o["streamPath"] = JsonValue.CreateStringValue(StreamPath);
            o["streaming"] = JsonValue.CreateBooleanValue(_camera != null && _camera.IsStreaming);
            // status block
            var cm = _camera;
            var br = TryBattery();
            int? bp = Percent(br);
            o["fps"] = JsonValue.CreateNumberValue(Math.Round(cm?.Fps ?? 0, 1));
            o["mbps"] = JsonValue.CreateNumberValue(Math.Round(cm?.Mbps ?? 0, 1));
            o["viewers"] = JsonValue.CreateNumberValue(_server.ClientCount);
            o["battery"] = bp.HasValue ? JsonValue.CreateNumberValue(bp.Value) : JsonValue.CreateNullValue();
            o["batteryStatus"] = JsonValue.CreateStringValue(br != null ? br.Status.ToString() : "unknown");
            o["charging"] = JsonValue.CreateBooleanValue(IsCharging(br));
            o["batteryFalling"] = JsonValue.CreateBooleanValue(BatteryFalling);
            o["batterySaver"] = JsonValue.CreateStringValue(BatterySaverText());
            int wb2 = WifiBars();
            o["wifiBars"] = wb2 >= 0 ? JsonValue.CreateNumberValue(wb2) : JsonValue.CreateNullValue();
            o["memoryMB"] = JsonValue.CreateNumberValue(Math.Round(AppMemory() / 1048576.0));
            o["uptime"] = JsonValue.CreateNumberValue(Math.Round((DateTime.Now - _appStarted).TotalSeconds));
            o["heat"] = JsonValue.CreateStringValue(_heat);
            o["heatWarn"] = JsonValue.CreateBooleanValue(_heatWarn);
            o["wb"] = JsonValue.CreateNumberValue(_tuning.WhiteBalanceKelvin);
            o["wbMin"] = JsonValue.CreateNumberValue(_tuning.WbMin > 0 ? _tuning.WbMin : 2000);
            o["wbMax"] = JsonValue.CreateNumberValue(_tuning.WbMax > _tuning.WbMin ? _tuning.WbMax : 8000);
            return o.Stringify();
        }

        private string GetStatusJson()
        {
            var o = new JsonObject();
            var v = Package.Current.Id.Version;
            o["app"] = JsonValue.CreateStringValue("W10MSecurityCam " + v.Major + "." + v.Minor + "." + v.Build);
            o["uptimeSeconds"] = JsonValue.CreateNumberValue(Math.Round((DateTime.Now - _appStarted).TotalSeconds));
            var cam = _camera;
            o["streaming"] = JsonValue.CreateBooleanValue(cam != null && cam.IsStreaming);
            o["engine"] = JsonValue.CreateStringValue(cam?.PathName ?? "");
            o["format"] = JsonValue.CreateStringValue(_currentFormat ?? "");
            o["fps"] = JsonValue.CreateNumberValue(Math.Round(cam?.Fps ?? 0, 1));
            o["mbps"] = JsonValue.CreateNumberValue(Math.Round(cam?.Mbps ?? 0, 2));
            o["keyframeSeconds"] = JsonValue.CreateNumberValue(Math.Round(cam?.KeyframeIntervalSec ?? 0, 1));
            o["sensorToEncodedMs"] = JsonValue.CreateNumberValue(cam?.LastLatencyMs ?? -1);
            o["viewers"] = JsonValue.CreateNumberValue(_server.ClientCount);
            o["loginRequired"] = JsonValue.CreateBooleanValue(_server.Auth.Enabled);
            o["audio"] = JsonValue.CreateStringValue(cam == null ? (_audioOn ? "on" : "off") : (cam.AudioStreaming ? "streaming" : cam.AudioStatus));
            o["safeMode"] = JsonValue.CreateBooleanValue(_safeMode);
            o["restarts"] = JsonValue.CreateNumberValue(_restarts);
            o["lastRestartReason"] = JsonValue.CreateStringValue(_lastRestartReason);
            var r = TryBattery();
            int? pct = Percent(r);
            o["batteryPercent"] = pct.HasValue ? JsonValue.CreateNumberValue(pct.Value) : JsonValue.CreateNullValue();
            o["batteryStatus"] = JsonValue.CreateStringValue(r != null ? r.Status.ToString() : "unknown");
            o["batteryFalling"] = JsonValue.CreateBooleanValue(BatteryFalling);
            o["bitrateSetting"] = JsonValue.CreateStringValue(_currentBitrate ?? "");
            o["cameraMode"] = JsonValue.CreateStringValue(_tuning.Mode);
            o["camera"] = JsonValue.CreateStringValue(_tuning.State ?? "");
            ulong mem = AppMemory();
            if (mem > _peakMemory) _peakMemory = mem;
            o["memoryMB"] = JsonValue.CreateNumberValue(Math.Round(mem / 1048576.0, 1));
            o["peakMemoryMB"] = JsonValue.CreateNumberValue(Math.Round(_peakMemory / 1048576.0, 1));
            o["memoryLimitMB"] = JsonValue.CreateNumberValue(Math.Round(AppMemoryLimit() / 1048576.0, 1));
            o["managedMemoryMB"] = JsonValue.CreateNumberValue(Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1));
            int bars = WifiBars();
            o["wifiSignalBars"] = bars >= 0 ? JsonValue.CreateNumberValue(bars) : JsonValue.CreateNullValue();
            o["time"] = JsonValue.CreateStringValue(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"));
            return o.Stringify();
        }

        private string GetConfigText()
        {
            return "login=" + (_server.Auth.Enabled ? "on (user " + _server.Auth.UserName + ")" : "off") + "\n" +
                   "basic=" + (_server.Auth.AllowBasic ? "on" : "off") + "\n" +
                   ExtraConfigText() +
                   "\nChange with POST /config, form fields: user, password (empty = reset to admin/admin), basic=on|off" + ExtraConfigHelp() + "\n";
        }

        /// <summary>Settings over the USB cable (tools\Set-Camera-Config.cmd). Runs on a network thread.</summary>
        private async Task<string> ApplyConfigAsync(IDictionary<string, string> form)
        {
            var done = new List<string>();
            if (form.ContainsKey("password"))
            {
                string user = form.TryGetValue("user", out var u) ? u : _server.Auth.UserName;
                _server.Auth.Save(user, form["password"]);
                done.Add(_server.Auth.IsDefault ? "login reset to the default (admin / admin)" : "login set for " + _server.Auth.UserName);
            }
            if (form.TryGetValue("basic", out var basic))
            {
                _server.Auth.AllowBasic = IsOn(basic);
                _settings.Values["AllowBasic"] = _server.Auth.AllowBasic;
                done.Add("basic " + (_server.Auth.AllowBasic ? "on" : "off"));
            }
            await ApplyExtraConfigAsync(form, done);
            string summary = done.Count > 0 ? "Changed: " + string.Join(", ", done) : "Nothing changed";
            Log("Config over USB: " + summary);
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, UpdateLoginUi);
            return summary;
        }

        private static bool IsOn(string s) => s == "1" || s.Equals("on", StringComparison.OrdinalIgnoreCase) || s.Equals("true", StringComparison.OrdinalIgnoreCase);

        private string ExtraConfigText() =>
            // Runs on a network thread: only cached strings here, never the on-screen controls.
            "format=" + (_currentFormat ?? (_settings.Values["Format"] as string ?? "")) + "\n" +
            "bitrate=" + (_currentBitrate ?? "") + "\n" +
            "highbw=" + (_highBw ? "on" : "off") + " (bitrates above 24 Mbps need it; use 5 GHz Wi-Fi or USB-C)\n" +
            "  formats: " + _formatList + "\n" +
            "camera=" + _tuning.Mode + " (now: " + _tuning.State + ")\n" +
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "  focus={0} (0 near - 100 far), exposure={1} (ms, max {2:0.#}), wb={3} (K, {4}-{5})\n",
                _tuning.FocusAuto ? "auto" : Math.Round(_tuning.FocusPercent).ToString(),
                _tuning.ExposureAuto ? "auto" : _tuning.ExposureMs.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                Math.Min(_tuning.ExposureMaxMs, _tuning.FrameLimitMs),
                _tuning.WbAuto ? "auto" : _tuning.WhiteBalanceKelvin.ToString(), _tuning.WbMin, _tuning.WbMax) +
            "audio=" + (_audioOn ? "on" : "off") + (_camera != null ? " (" + (_camera.AudioStreaming ? "streaming" : _camera.AudioStatus) + ")" : "") + "\n" +
            "udp=" + (_server.AllowUdp ? "on" : "off") + "\n" +
            "snapshot=" + (_snapshotsOn ? "on" : "off") +
            (_camera != null ? (_camera.PhotoWhileRecordingSupported ? "" : " (this camera can't take photos while recording)") : "") + "\n";

        private string ExtraConfigHelp() => ", stream=start|stop|restart, engine=normal, powersave=on|off, focus=once, highbw=on|off, camera=auto|manual, focus=auto|manual|0-100, exposure=auto|manual|MS, wb=auto|manual|KELVIN, format=WIDTHxHEIGHT@FPS, bitrate=MBPS|auto, audio=on|off, udp=on|off, snapshot=on|off";

        private static string FormatKey(FormatOption f) => f.Width + "x" + f.Height + "@" + Math.Round(f.Fps);

        /// <summary>
        /// format=3840x2160@30 and/or bitrate=12 (Mbps, or "auto"). Changing them while streaming restarts the stream,
        /// as the on-screen boxes do. Runs on the UI thread. Returns what changed, or an error.
        /// </summary>
        private string ApplyVideoSettings(string format, string bitrate)
        {
            var notes = new List<string>();
            _applyingSettings = true;
            try
            {
                if (format != null)
                {
                    var f = ResolutionBox.Items.OfType<FormatOption>()
                        .FirstOrDefault(o => FormatKey(o).Equals(format.Trim().Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
                    if (f == null) notes.Add("format " + format + " not offered by this camera");
                    else if (ResolutionBox.SelectedItem != f) { ResolutionBox.SelectedItem = f; notes.Add("format " + FormatKey(f)); }
                }
                if (bitrate != null)
                {
                    uint bps;
                    double mbps;
                    if (bitrate.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) bps = 0;
                    else if (double.TryParse(bitrate.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mbps)
                             && mbps >= 0.5 && mbps <= (_highBw ? 80 : NormalMaxMbps)) bps = (uint)Math.Round(mbps * 1e6);
                    else if (mbps > NormalMaxMbps && mbps <= 80 && !_highBw)
                    { notes.Add("bitrate " + bitrate + " not allowed: above " + NormalMaxMbps + " Mbps needs high bandwidth mode (highbw=on)"); bps = uint.MaxValue; }
                    else { notes.Add("bitrate " + bitrate + " not understood (Mbps 0.5-" + (_highBw ? 80 : NormalMaxMbps) + ", or auto)"); bps = uint.MaxValue; }
                    if (bps != uint.MaxValue)
                    {
                        var opt = BitrateBox.Items.OfType<BitrateOption>().FirstOrDefault(o => o.Bps == bps);
                        if (opt == null)
                        {
                            opt = new BitrateOption { Bps = bps };
                            int at = 1;
                            while (at < BitrateBox.Items.Count && ((BitrateOption)BitrateBox.Items[at]).Bps < bps) at++;
                            BitrateBox.Items.Insert(at, opt);
                        }
                        if (BitrateBox.SelectedItem != opt) { BitrateBox.SelectedItem = opt; notes.Add("bitrate " + opt); }
                    }
                }
            }
            finally { _applyingSettings = false; SyncVideoKeys(); }
            return string.Join(", ", notes);
        }

        private bool _applyingSettings;

        // ------------------------------------------------------------------ high bandwidth mode

        private bool _highBw;
        private const uint NormalMaxMbps = 24;
        private static readonly uint[] NormalBitrates = { 1, 2, 3, 4, 6, 8, 12, 16, 20, 24 };
        private static readonly uint[] HighBitrates = { 30, 40, 50, 60 };

        private IEnumerable<uint> BitrateMenuMbps() => _highBw ? NormalBitrates.Concat(HighBitrates) : NormalBitrates;

        /// <summary>Fills the bitrate box for the current mode, keeping the selection when it is still allowed (otherwise Auto).</summary>
        private void RebuildBitrateList()
        {
            uint cur = (BitrateBox.SelectedItem as BitrateOption)?.Bps ?? 4000000;
            if (!_highBw && cur > NormalMaxMbps * 1000000u) cur = 0;
            bool was = _applyingSettings;
            _applyingSettings = true; // not a user change
            try
            {
                BitrateBox.Items.Clear();
                BitrateBox.Items.Add(new BitrateOption { Bps = 0 });
                foreach (var m in BitrateMenuMbps()) BitrateBox.Items.Add(new BitrateOption { Bps = m * 1000000 });
                ApplyVideoSettings(null, cur == 0 ? "auto" : (cur / 1e6).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            finally { _applyingSettings = was; }
        }

        private async void HighBwToggle_Toggled(object sender, RoutedEventArgs e) => await SetHighBwAsync(HighBwToggle.IsOn);

        /// <summary>Turns high bandwidth mode on or off. Turning it off while a higher bitrate is in use drops back to Auto and restarts the stream.</summary>
        private async Task<string> SetHighBwAsync(bool on)
        {
            if (_highBw == on) return "";
            _highBw = on;
            _settings.Values["HighBandwidth"] = on;
            HighBwToggle.IsOn = on;
            uint before = (BitrateBox.SelectedItem as BitrateOption)?.Bps ?? 0;
            RebuildBitrateList();
            uint after = (BitrateBox.SelectedItem as BitrateOption)?.Bps ?? 0;
            Log("High bandwidth mode " + (on ? "on (bitrates above 24 Mbps allowed; needs 5 GHz Wi-Fi or USB-C)" : "off"));
            if (before == after) return "";
            Log("Bitrate set back to Auto (above 24 Mbps needs high bandwidth mode)");
            if (_camera != null && _camera.IsStreaming && !_busy)
            {
                await ToggleStreamingAsync();
                await ToggleStreamingAsync();
            }
            return ", bitrate back to Auto";
        }

        // ------------------------------------------------------------------ microphone audio (optional)

        private bool _audioOn;

        /// <summary>Opening or closing the microphone needs the camera to be set up again.</summary>
        private async Task SetAudioAsync(bool on)
        {
            if (_audioOn == on) return;
            _audioOn = on;
            _settings.Values["Audio"] = on;
            AudioToggle.IsOn = on;
            Log("Microphone audio " + (on ? "on" : "off") + " - restarting the camera");
            await RestartCameraAsync();
        }

        private async Task RestartCameraAsync()
        {
            if (_busy) { Log("Camera busy - audio change applies at the next start"); return; }
            _busy = true;
            _busySince = DateTime.Now;
            try
            {
                _statsTimer.Stop();
                if (_camera != null)
                {
                    try { await _camera.ShutdownAsync(); } catch (Exception ex) { Log("Camera shutdown: " + ex.Message); }
                }
                _camera = null;
                ClearStartFlag();
                StopUi();
                _blackedOut = false;
                BlackoutOverlay.Visibility = Visibility.Collapsed;
            }
            finally { _busy = false; }
            await StartCameraAsync(); // starts streaming again if auto-start is on
        }

        private async void AudioToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_audioOn == AudioToggle.IsOn) return;
            await SetAudioAsync(AudioToggle.IsOn);
        }

        // ------------------------------------------------------------------ snapshots (optional, off by default)

        private volatile bool _snapshotsOn;
        private byte[] _lastSnapshot;
        private DateTime _lastSnapshotAt = DateTime.MinValue;
        private readonly System.Threading.SemaphoreSlim _snapshotLock = new System.Threading.SemaphoreSlim(1);

        /// <summary>/snapshot.jpg: a still taken while recording, at most 1280 px wide; reused for 2 s so polling NVRs can't overload the camera.</summary>
        private async Task<byte[]> GetSnapshotAsync()
        {
            var cam = _camera;
            if (!_snapshotsOn || cam == null || !cam.IsStreaming) return null;
            await _snapshotLock.WaitAsync();
            try
            {
                if (_lastSnapshot != null && (DateTime.Now - _lastSnapshotAt).TotalSeconds < 2) return _lastSnapshot;
                var jpg = await cam.CaptureJpegAsync(1280);
                if (jpg != null) { _lastSnapshot = jpg; _lastSnapshotAt = DateTime.Now; }
                return jpg;
            }
            finally { _snapshotLock.Release(); }
        }

        private void SnapshotToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_snapshotsOn == SnapshotToggle.IsOn) return;
            _snapshotsOn = SnapshotToggle.IsOn;
            _settings.Values["Snapshots"] = _snapshotsOn;
            Log("Snapshots " + (_snapshotsOn ? "on: http://PHONE-IP:" + LogPort + "/snapshot.jpg" : "off"));
        }

        /// <summary>Runs work on the UI thread and completes when it has finished (Dispatcher.RunAsync alone returns at the first await).</summary>
        private Task RunUiAsync(Func<Task> work)
        {
            var tcs = new TaskCompletionSource<bool>();
            var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try { await work(); tcs.SetResult(true); }
                catch (Exception ex) { Log("UI action failed: " + ex.Message); tcs.SetResult(false); }
            });
            return tcs.Task;
        }

        private async Task ApplyExtraConfigAsync(IDictionary<string, string> form, List<string> done)
        {
            bool tuningChanged = false;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (form.TryGetValue("focus", out var fonce) && fonce.Trim().Equals("once", StringComparison.OrdinalIgnoreCase))
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await FocusOnceAsync());
                done.Add("focusing once; the lens then stays where it lands (see /camera/state)");
            }
            if (form.TryGetValue("powersave", out var psv))
            {
                bool on = IsOn(psv);
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    await SetBlackoutAsync(on);
                });
                done.Add("phone screen blackout " + (on ? "on" : "off") + (on && !(_camera != null && _camera.IsStreaming) ? " (starts with the stream)" : ""));
            }
            if (form.TryGetValue("camera", out var cm))
            {
                bool a = cm.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
                if (!a) _tuning.SeedFromCamera(_camera?.Capture, _tuning.FocusAuto, _tuning.ExposureAuto, _tuning.WbAuto);
                _tuning.Auto = a; tuningChanged = true;
            }
            // Each of these takes "auto" or a value; a value makes just that setting manual.
            if (form.TryGetValue("focus", out var fo))
            {
                if (fo.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) { _tuning.FocusAuto = true; tuningChanged = true; }
                else if (fo.Trim().Equals("manual", StringComparison.OrdinalIgnoreCase))
                { if (_tuning.FocusAuto) _tuning.SeedFromCamera(_camera?.Capture, true, false, false); _tuning.FocusAuto = false; tuningChanged = true; }
                else if (double.TryParse(fo, System.Globalization.NumberStyles.Float, inv, out var fv))
                { _tuning.FocusPercent = Math.Max(0, Math.Min(100, fv)); _tuning.FocusAuto = false; tuningChanged = true; }
            }
            if (form.TryGetValue("exposure", out var ex))
            {
                if (ex.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) { _tuning.ExposureAuto = true; tuningChanged = true; }
                else if (ex.Trim().Equals("manual", StringComparison.OrdinalIgnoreCase))
                { if (_tuning.ExposureAuto) _tuning.SeedFromCamera(_camera?.Capture, false, true, false); _tuning.ExposureAuto = false; tuningChanged = true; }
                else if (double.TryParse(ex, System.Globalization.NumberStyles.Float, inv, out var ev))
                { _tuning.ExposureMs = Math.Max(0.01, ev); _tuning.ExposureAuto = false; tuningChanged = true; }
            }
            if (form.TryGetValue("wb", out var wb))
            {
                if (wb.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) { _tuning.WbAuto = true; tuningChanged = true; }
                else if (wb.Trim().Equals("manual", StringComparison.OrdinalIgnoreCase))
                { if (_tuning.WbAuto) _tuning.SeedFromCamera(_camera?.Capture, false, false, true); _tuning.WbAuto = false; tuningChanged = true; }
                else if (uint.TryParse(wb, out var wv)) { _tuning.WhiteBalanceKelvin = wv; _tuning.WbAuto = false; tuningChanged = true; }
            }
            if (tuningChanged)
            {
                SaveTuning();
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => { _blackoutForced = false; LoadTuningUi(); await UpdateDisplayForTuningAsync(); await ApplyTuningAsync(); });
                done.Add(string.Format(inv, "camera {0} (focus {1}, exposure {2}, wb {3})", _tuning.Mode,
                    _tuning.FocusAuto ? "auto" : Math.Round(_tuning.FocusPercent).ToString(inv),
                    _tuning.ExposureAuto ? "auto" : _tuning.ExposureMs.ToString("0.## ms", inv),
                    _tuning.WbAuto ? "auto" : _tuning.WhiteBalanceKelvin + " K"));
            }
            if (form.TryGetValue("highbw", out var hbv))
            {
                bool on = IsOn(hbv);
                string note = "";
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => { note = await SetHighBwAsync(on); });
                done.Add("high bandwidth mode " + (on ? "on" : "off") + note);
            }
            if (form.TryGetValue("engine", out var eng) && eng.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase))
            {
                await RunUiAsync(async () =>
                {
                    if (_camera == null) return;
                    bool was = _camera.IsStreaming;
                    _camera.UseNativeSink = true;
                    _safeMode = false;
                    _lastRestartReason = "";
                    Log("Normal (low-latency) engine restored from the web page");
                    if (was) { await ToggleStreamingAsync(); await ToggleStreamingAsync(); }
                });
                done.Add("normal engine restored");
            }
            form.TryGetValue("format", out var fmt);
            form.TryGetValue("bitrate", out var br);
            if (fmt != null || br != null)
            {
                string result = "";
                await RunUiAsync(async () =>
                {
                    result = ApplyVideoSettings(fmt, br);
                    bool ok = result.Length > 0 && !result.Contains("not ");
                    // Asking for a format means "show me this": start the stream if it's stopped, restart it if it's running.
                    if (_camera != null && (ok || (fmt != null && !_camera.IsStreaming && !result.Contains("not "))))
                    {
                        if (!_camera.UseNativeSink && ResolutionBox.SelectedItem is FormatOption sel && (long)sel.Width * sel.Height > 1920L * 1080)
                            result += (result.Length > 0 ? ", " : "") + "SAFE MODE engine is limited to 1080p - use 'Return to normal mode' for 4K";
                        bool running = _camera.IsStreaming;
                        Log("Video settings changed from the network - " + (running ? "restarting" : "starting") + " the stream");
                        if (running) await ToggleStreamingAsync();
                        await ToggleStreamingAsync();
                        result += (result.Length > 0 ? ", " : "") + (running ? "stream restarting" : "stream starting");
                    }
                });
                if (result.Length > 0) done.Add(result);
            }
            if (form.TryGetValue("stream", out var sact))
            {
                string a = sact.Trim().ToLowerInvariant();
                await RunUiAsync(async () =>
                {
                    bool on = _camera != null && _camera.IsStreaming;
                    if (a == "start" && !on) await ToggleStreamingAsync();
                    else if (a == "stop" && on) await ToggleStreamingAsync();
                    else if (a == "restart") { if (on) await ToggleStreamingAsync(); await ToggleStreamingAsync(); }
                });
                done.Add("stream " + a);
            }
            if (form.TryGetValue("udp", out var udp))
            {
                _server.AllowUdp = IsOn(udp);
                _settings.Values["AllowUdp"] = _server.AllowUdp;
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UdpToggle.IsOn = _server.AllowUdp);
                done.Add("udp " + (_server.AllowUdp ? "on" : "off"));
            }
            if (form.TryGetValue("audio", out var audio))
            {
                bool on = IsOn(audio);
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await SetAudioAsync(on));
                done.Add("audio " + (on ? "on (camera restarting; the phone may ask for microphone access)" : "off"));
            }
            if (form.TryGetValue("snapshot", out var snap))
            {
                _snapshotsOn = IsOn(snap);
                _settings.Values["Snapshots"] = _snapshotsOn;
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => SnapshotToggle.IsOn = _snapshotsOn);
                done.Add("snapshot " + (_snapshotsOn ? "on" : "off"));
            }
        }

        private void UdpToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_server.AllowUdp == UdpToggle.IsOn) return;
            _server.AllowUdp = UdpToggle.IsOn;
            _settings.Values["AllowUdp"] = UdpToggle.IsOn;
            Log("RTP over UDP " + (UdpToggle.IsOn ? "allowed" : "off (TCP only)"));
        }

        // ------------------------------------------------------------------ logging

        private readonly LinkedList<string> _screenLog = new LinkedList<string>();
        private readonly List<string> _fullLog = new List<string>();
        private static readonly string LogFile = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "log.txt");
        private static readonly string PrevLogFile = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "log-previous.txt");
        private string _previousSession = "";
        private int _linesSinceSizeCheck;

        /// <summary>Text served at http://PHONE-IP:5001 and to "GET" requests on the USB port.</summary>
        private string GetFullLog()
        {
            string stats = "";
            var cam = _camera;
            if (cam != null)
                stats = string.Format("Streaming: {0}  fps: {1:0.0}  Mbps: {2:0.00}  keyframe every: {3:0.0}s  viewers: {4}  engine: {5}  login: {6}\n\n",
                    cam.IsStreaming, cam.Fps, cam.Mbps, cam.KeyframeIntervalSec, _server.ClientCount, cam.PathName, _server.Auth.Enabled ? "on" : "off");
            lock (_fullLog) return stats + string.Join("\n", _fullLog) +
                (_previousSession.Length > 0 ? "\n\n--- previous session (last lines) ---\n" + _previousSession : "");
        }

        /// <summary>Keep the last session's log (it survives a crash) and start a fresh one.</summary>
        private void RotateLogFile()
        {
            try
            {
                if (System.IO.File.Exists(LogFile))
                {
                    if (System.IO.File.Exists(PrevLogFile)) System.IO.File.Delete(PrevLogFile);
                    System.IO.File.Move(LogFile, PrevLogFile);
                }
                if (System.IO.File.Exists(PrevLogFile))
                {
                    var lines = System.IO.File.ReadAllLines(PrevLogFile);
                    _previousSession = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 120)));
                }
            }
            catch (Exception ex) { _previousSession = "(could not read previous log: " + ex.Message + ")"; }
        }

        private void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine("[W10MSecurityCam] " + message);
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + message;
            lock (_fullLog)
            {
                _fullLog.Add(line);
                if (_fullLog.Count > 1000) _fullLog.RemoveRange(0, 200);
                try
                {
                    // Keep the on-phone log file small during long sessions: start over above ~2 MB.
                    if (++_linesSinceSizeCheck >= 500)
                    {
                        _linesSinceSizeCheck = 0;
                        if (System.IO.File.Exists(LogFile) && new System.IO.FileInfo(LogFile).Length > 2 * 1024 * 1024)
                            System.IO.File.Delete(LogFile);
                    }
                    System.IO.File.AppendAllText(LogFile, line + "\r\n");
                }
                catch { }
            }
            var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                _screenLog.AddFirst(DateTime.Now.ToString("HH:mm:ss") + " " + message);
                while (_screenLog.Count > 25) _screenLog.RemoveLast();
                LogText.Text = string.Join("\n", _screenLog);
            });
        }
    }
}
