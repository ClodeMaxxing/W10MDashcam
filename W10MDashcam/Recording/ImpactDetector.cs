using System;
using Windows.Devices.Sensors;

namespace W10MDashcam.Recording
{
    /// <summary>
    /// Crash detection with the phone's accelerometer (the dashcam "G-sensor").
    ///
    /// Gravity is followed with a slow average (about 1 s) and taken away, leaving the sudden change in motion.
    /// One reading above the threshold is an impact: knocks and collisions are short spikes that often show up in
    /// a single reading only (0.4.2 asked for two in a row and missed real whacks). The sensor is read as often as
    /// it allows (10 ms here). Jolts above 60 % of the threshold are logged, so the level can be tuned from real
    /// drives. After an impact the detector waits 30 s. Readings arrive on a sensor thread; Impact is raised there.
    /// </summary>
    internal sealed class ImpactDetector
    {
        public Action<string> Log = _ => { };
        /// <summary>Raised with the change in g at the moment of impact.</summary>
        public event Action<double> Impact;

        /// <summary>Trigger level in g (0 = off).</summary>
        public volatile float Threshold = 2.0f;
        public bool Available { get; private set; }
        /// <summary>The latest change in motion (g).</summary>
        public double Now { get; private set; }
        /// <summary>The largest change in the last 10 s (g), for setting the sensitivity.</summary>
        public double Peak { get; private set; }
        public DateTime LastImpact { get; private set; } = DateTime.MinValue;
        public double LastImpactG { get; private set; }

        private Accelerometer _acc;
        private bool _init;
        private double _gx, _gy, _gz;
        private DateTime _lastReading = DateTime.MinValue;
        private DateTime _highSince = DateTime.MinValue;
        private DateTime _lastJoltLog = DateTime.MinValue;
        /// <summary>A jolt worth logging (below the threshold): its size in g. Raised on the sensor thread, at most once every 5 s.</summary>
        public event Action<double> NearMiss;
        private DateTime _peakAt = DateTime.MinValue;
        private DateTime _quietUntil = DateTime.MinValue;
        private const double GravitySeconds = 1.0;      // how fast the gravity estimate follows a change of mounting angle
        private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

        public static float ThresholdFor(string sensitivity)
        {
            switch ((sensitivity ?? "").ToLowerInvariant())
            {
                case "off": return 0f;
                case "low": return 2.5f;     // only big impacts
                case "high": return 1.0f;    // also hard knocks and bumps (may trigger on bad roads)
                default: return 1.6f;        // medium
            }
        }

        public bool Start()
        {
            try
            {
                if (_acc != null) return true;
                _acc = Accelerometer.GetDefault();
                if (_acc == null) { Log("Crash detection: this phone has no accelerometer"); return false; }
                _acc.ReportInterval = Math.Max(_acc.MinimumReportInterval, 10u);
                _acc.ReadingChanged += OnReading;
                Available = true;
                Log("Crash detection: G-sensor reading every " + _acc.ReportInterval + " ms");
                return true;
            }
            catch (Exception ex) { Log("Crash detection unavailable: " + ex.Message); _acc = null; return false; }
        }

        private void OnReading(Accelerometer sender, AccelerometerReadingChangedEventArgs args)
        {
            try
            {
                var r = args.Reading;
                double x = r.AccelerationX, y = r.AccelerationY, z = r.AccelerationZ;
                if (!_init) { _gx = x; _gy = y; _gz = z; _init = true; return; }
                var now = DateTime.UtcNow;
                double dt = _lastReading == DateTime.MinValue ? 0.01 : Math.Min(0.5, Math.Max(0.001, (now - _lastReading).TotalSeconds));
                _lastReading = now;
                double dx = x - _gx, dy = y - _gy, dz = z - _gz;
                double m = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                double follow = Math.Min(1.0, dt / GravitySeconds);
                // A jolt must not drag the gravity estimate along; a change that lasts (phone re-mounted at a new
                // angle) is followed after 1.5 s, quickly.
                if (m < 0.5) _highSince = DateTime.MinValue;
                else if (_highSince == DateTime.MinValue) _highSince = now;
                bool settled = _highSince != DateTime.MinValue && (now - _highSince).TotalSeconds > 1.5;
                if (settled) follow = Math.Min(1.0, dt / 0.2);
                if (m < 0.5 || settled) { _gx += follow * dx; _gy += follow * dy; _gz += follow * dz; }
                Now = m;
                if (m >= Peak || (now - _peakAt).TotalSeconds > 10) { Peak = m; _peakAt = now; }

                float t = Threshold;
                if (t <= 0) return;
                if (m >= t && now >= _quietUntil)
                {
                    _quietUntil = now + Cooldown;
                    LastImpact = DateTime.Now;
                    LastImpactG = m;
                    Impact?.Invoke(m);
                }
                else if (m >= t * 0.6 && (now - _lastJoltLog).TotalSeconds >= 5)
                {
                    _lastJoltLog = now;
                    NearMiss?.Invoke(m);
                }
            }
            catch { }
        }
    }
}
