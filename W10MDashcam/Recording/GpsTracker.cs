using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;

namespace W10MDashcam.Recording
{
    /// <summary>One GPS reading.</summary>
    internal struct GpsFix
    {
        public DateTime Utc;       // when the app received it
        public double Lat, Lon;
        public double? SpeedMs;    // metres per second (null: unknown)
        public double? Heading;    // degrees from north (null: unknown or standing still)
        public double? Altitude;
        public double Accuracy;    // metres
    }

    /// <summary>
    /// GPS speed and position for the recordings' subtitle track and the screen. Readings come about once a second;
    /// the last minute is kept so a subtitle can use the reading for its own moment. Thread-safe.
    /// </summary>
    internal sealed class GpsTracker
    {
        public Action<string> Log = _ => { };
        public string Status { get; private set; } = "off";

        private Geolocator _geo;
        private readonly object _lock = new object();
        private readonly List<GpsFix> _fixes = new List<GpsFix>();

        public bool Running => _geo != null;

        /// <summary>Asks for location access (the first time Windows asks the user) and starts. Call on the UI thread.</summary>
        private bool _starting;

        public async Task StartAsync()
        {
            if (_geo != null || _starting) return;
            _starting = true;
            try
            {
                var access = await Geolocator.RequestAccessAsync();
                if (access != GeolocationAccessStatus.Allowed)
                {
                    Status = "location access denied";
                    Log("GPS: location access denied - allow it in Settings > Privacy > Location (and Location on)");
                    return;
                }
                _geo = new Geolocator { DesiredAccuracy = PositionAccuracy.High, ReportInterval = 1000, MovementThreshold = 0 };
                _geo.PositionChanged += OnPosition;
                _geo.StatusChanged += OnStatus;
                Status = "waiting for a fix";
                Log("GPS: on");
            }
            catch (Exception ex) { Status = "not available"; Log("GPS not available: " + ex.Message); _geo = null; }
            finally { _starting = false; }
        }

        public void Stop()
        {
            var g = _geo;
            _geo = null;
            if (g != null) { g.PositionChanged -= OnPosition; g.StatusChanged -= OnStatus; }
            Status = "off";
            lock (_lock) _fixes.Clear();
        }

        private void OnStatus(Geolocator sender, StatusChangedEventArgs args)
        {
            switch (args.Status)
            {
                case PositionStatus.Disabled: Status = "location is off in Settings"; break;
                case PositionStatus.NoData: Status = "no signal"; break;
                case PositionStatus.Initializing: Status = "waiting for a fix"; break;
                case PositionStatus.NotAvailable: Status = "not available"; break;
            }
        }

        private void OnPosition(Geolocator sender, PositionChangedEventArgs args)
        {
            try
            {
                var c = args.Position.Coordinate;
                var p = c.Point.Position;
                var fix = new GpsFix
                {
                    Utc = DateTime.UtcNow,
                    Lat = p.Latitude,
                    Lon = p.Longitude,
                    Altitude = p.Altitude,
                    Accuracy = c.Accuracy,
                    SpeedMs = c.Speed.HasValue && !double.IsNaN(c.Speed.Value) && c.Speed.Value >= 0 ? c.Speed : null,
                    Heading = c.Heading.HasValue && !double.IsNaN(c.Heading.Value) ? c.Heading : null
                };
                lock (_lock)
                {
                    _fixes.Add(fix);
                    var cut = fix.Utc.AddSeconds(-60);
                    int old = 0;
                    while (old < _fixes.Count && _fixes[old].Utc < cut) old++;
                    if (old > 0) _fixes.RemoveRange(0, old);
                }
                if (Status != "fix") Log("GPS: fix (" + Math.Round(c.Accuracy) + " m)");
                Status = "fix";
            }
            catch { }
        }

        /// <summary>The latest reading at or before this moment, if it is recent (under 5 s old then).</summary>
        public GpsFix? At(DateTime utc)
        {
            lock (_lock)
            {
                for (int i = _fixes.Count - 1; i >= 0; i--)
                {
                    var f = _fixes[i];
                    if (f.Utc <= utc.AddMilliseconds(500)) return (utc - f.Utc).TotalSeconds <= 5 ? f : (GpsFix?)null;
                }
            }
            return null;
        }

        public GpsFix? Latest => At(DateTime.UtcNow);
    }
}
