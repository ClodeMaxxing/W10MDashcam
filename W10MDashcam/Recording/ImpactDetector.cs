using System;
using Windows.Devices.Sensors;

namespace W10MDashcam.Recording
{
    /// <summary>
    /// Crash detection with the phone's accelerometer (the dashcam "G-sensor").
    ///
    /// Gravity is followed with a slow average and taken away, leaving the sudden change in motion. An impact is
    /// that change staying above the threshold for two readings in a row (~40 ms), or one reading above 1.5 x the
    /// threshold. A road bump is usually a single short spike; a collision lasts longer. After an impact the
    /// detector waits 30 s before it can trigger again. Readings arrive on a sensor thread; Impact is raised there.
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
        private int _over;
        private DateTime _peakAt = DateTime.MinValue;
        private DateTime _quietUntil = DateTime.MinValue;
        private const double GravityFollow = 0.04;      // per reading (~1 s to settle at 50 readings/s)
        private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

        public static float ThresholdFor(string sensitivity)
        {
            switch ((sensitivity ?? "").ToLowerInvariant())
            {
                case "off": return 0f;
                case "low": return 3.0f;     // only big impacts
                case "high": return 1.2f;    // also hard bumps (may trigger on bad roads)
                default: return 2.0f;        // medium
            }
        }

        public bool Start()
        {
            try
            {
                if (_acc != null) return true;
                _acc = Accelerometer.GetDefault();
                if (_acc == null) { Log("Crash detection: this phone has no accelerometer"); return false; }
                _acc.ReportInterval = Math.Max(_acc.MinimumReportInterval, 20u);
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
                double dx = x - _gx, dy = y - _gy, dz = z - _gz;
                _gx += GravityFollow * dx; _gy += GravityFollow * dy; _gz += GravityFollow * dz;
                double m = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                Now = m;
                var now = DateTime.UtcNow;
                if (m >= Peak || (now - _peakAt).TotalSeconds > 10) { Peak = m; _peakAt = now; }

                float t = Threshold;
                if (t <= 0) { _over = 0; return; }
                _over = m >= t ? _over + 1 : 0;
                if ((_over >= 2 || m >= t * 1.5) && now >= _quietUntil)
                {
                    _quietUntil = now + Cooldown;
                    _over = 0;
                    LastImpact = DateTime.Now;
                    LastImpactG = m;
                    Impact?.Invoke(m);
                }
            }
            catch { }
        }
    }
}
