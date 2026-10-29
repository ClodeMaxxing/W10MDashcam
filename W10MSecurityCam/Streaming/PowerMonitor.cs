using System;
using System.Collections.Generic;
using System.Globalization;
using Windows.Devices.Power;

namespace W10MSecurityCam.Streaming
{
    /// <summary>
    /// Estimates how much power the phone uses. Windows gives apps no power meter, but the battery report has the remaining
    /// capacity (mWh) and sometimes a live charge rate (mW). The change in capacity over a few minutes is the net flow into
    /// (+) or out of (-) the battery. While the phone is not charging (on battery, or charging paused because it is hot)
    /// that flow is the whole phone's draw. Also reads processor use where Windows allows it.
    /// </summary>
    internal sealed class PowerMonitor
    {
        private struct Point { public DateTime T; public int Mwh; }
        private readonly Queue<Point> _hist = new Queue<Point>();
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
        private string _histState = "";                       // the average only ever covers one state (charging / on battery / paused)

        public double? NetWatts;          // + into the battery, - out of it (averaged over up to 5 minutes in the current state)
        public double? DrawWatts;         // whole-phone draw, only known while the battery is not being charged
        public int? RateMw;               // live rate if the driver reports one (+ charging)
        public string Status = "unknown", Supply = "unknown";
        public int? RemainingMwh, FullMwh, DesignMwh;
        public double? ProcCpuPct, SysCpuPct;

        // processor use
        private TimeSpan _procBusy; private DateTime _procAt = DateTime.MinValue;
        private TimeSpan _sysTotal, _sysIdle; private bool _sysDead, _sysHave;

        public void Sample()
        {
            try
            {
                var r = Battery.AggregateBattery.GetReport();
                Status = r.Status.ToString();
                RemainingMwh = r.RemainingCapacityInMilliwattHours;
                FullMwh = r.FullChargeCapacityInMilliwattHours;
                DesignMwh = r.DesignCapacityInMilliwattHours;
                RateMw = r.ChargeRateInMilliwatts;
                try { Supply = Windows.System.Power.PowerManager.PowerSupplyStatus.ToString(); } catch { }
                // Plugging in, unplugging or a heat pause starts a fresh average: a window spanning two states showed
                // "11 W out of the battery" while it was charging again.
                string state = Status + "/" + Supply;
                if (state != _histState) { _hist.Clear(); _histState = state; }
                if (RemainingMwh.HasValue)
                {
                    _hist.Enqueue(new Point { T = DateTime.Now, Mwh = RemainingMwh.Value });
                    while (_hist.Count > 0 && DateTime.Now - _hist.Peek().T > Window) _hist.Dequeue();
                    var first = _hist.Peek();
                    double hours = (DateTime.Now - first.T).TotalHours;
                    NetWatts = hours * 3600 >= 120 ? (double?)((RemainingMwh.Value - first.Mwh) / 1000.0 / hours) : null;
                }
                // The draw comes from the capacity change only. The driver's "charge rate" on this phone is stale or wrong
                // (it read -1.1 W while the capacity showed about 9 W leaving the battery), so it is shown but never used.
                bool charging = r.Status == Windows.System.Power.BatteryStatus.Charging;
                DrawWatts = !charging && NetWatts.HasValue ? (double?)Math.Max(0, -NetWatts.Value) : null;
            }
            catch { }
            SampleCpu();
        }

        private void SampleCpu()
        {
            try
            {
                var p = Windows.System.Diagnostics.ProcessDiagnosticInfo.GetForCurrentProcess().CpuUsage.GetReport();
                var busy = p.UserTime + p.KernelTime;
                if (_procAt != DateTime.MinValue)
                {
                    double wall = (DateTime.Now - _procAt).TotalSeconds * Math.Max(1, Environment.ProcessorCount);
                    if (wall > 0) ProcCpuPct = Math.Max(0, Math.Min(100, (busy - _procBusy).TotalSeconds / wall * 100));
                }
                _procBusy = busy; _procAt = DateTime.Now;
            }
            catch { ProcCpuPct = null; }
            if (_sysDead) return;
            try
            {
                var s = Windows.System.Diagnostics.SystemDiagnosticInfo.GetForCurrentSystem().CpuUsage.GetReport();
                var total = s.UserTime + s.KernelTime;
                if (_sysHave)
                {
                    double dTotal = (total - _sysTotal).TotalSeconds, dIdle = (s.IdleTime - _sysIdle).TotalSeconds;
                    // Like GetSystemTimes, kernel time normally includes idle time; if idle is larger than the total it is separate.
                    double all = dIdle <= dTotal ? dTotal : dTotal + dIdle;
                    double busy = dIdle <= dTotal ? dTotal - dIdle : dTotal;
                    if (all > 0) SysCpuPct = Math.Max(0, Math.Min(100, busy / all * 100));
                }
                _sysTotal = total; _sysIdle = s.IdleTime; _sysHave = true;
            }
            catch { _sysDead = true; SysCpuPct = null; }
        }

        public string PowerText()
        {
            string net;
            if (!NetWatts.HasValue) net = "measuring (needs a few minutes)";
            else if (NetWatts.Value >= 0.05) net = "+" + NetWatts.Value.ToString("0.0", CultureInfo.InvariantCulture) + " W into the battery";
            else if (NetWatts.Value <= -0.05) net = (-NetWatts.Value).ToString("0.0", CultureInfo.InvariantCulture) + " W out of the battery";
            else net = "battery level steady";
            string s = "Power: " + net;
            if (DrawWatts.HasValue && DrawWatts.Value > 0) s += "; phone draws about " + DrawWatts.Value.ToString("0.0", CultureInfo.InvariantCulture) + " W";
            else if (Status == "Charging") s += " (the phone's own use is not measurable while charging)";
            // the driver's own rate is left out: it is stale on this phone (see Sample); probe=power still shows it
            s += "; charger " + Supply.ToLowerInvariant();
            return s;
        }

        public string CpuText()
        {
            if (!ProcCpuPct.HasValue && !SysCpuPct.HasValue) return "";
            string s = "CPU: ";
            if (SysCpuPct.HasValue) s += "phone " + Math.Round(SysCpuPct.Value) + " %, ";
            s += "app " + (ProcCpuPct.HasValue ? Math.Round(ProcCpuPct.Value).ToString() : "?") + " %";
            return s;
        }

        /// <summary>Everything the battery report and the processor counters return on this phone (for probe=power).</summary>
        public static string Dump()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var r = Battery.AggregateBattery.GetReport();
                sb.Append("Battery report: status ").Append(r.Status).Append(", remaining ").Append(r.RemainingCapacityInMilliwattHours)
                  .Append(" mWh, full ").Append(r.FullChargeCapacityInMilliwattHours).Append(" mWh, design ").Append(r.DesignCapacityInMilliwattHours)
                  .Append(" mWh, charge rate ").Append(r.ChargeRateInMilliwatts.HasValue ? r.ChargeRateInMilliwatts + " mW" : "not reported");
            }
            catch (Exception ex) { sb.Append("Battery report failed: ").Append(ex.Message); }
            try { sb.Append("; supply ").Append(Windows.System.Power.PowerManager.PowerSupplyStatus).Append(", Windows says ").Append(Windows.System.Power.PowerManager.RemainingChargePercent).Append(" %"); } catch { }
            try
            {
                var p = Windows.System.Diagnostics.ProcessDiagnosticInfo.GetForCurrentProcess().CpuUsage.GetReport();
                sb.Append("; app CPU time user ").Append(p.UserTime.TotalSeconds.ToString("0.0")).Append(" s, kernel ").Append(p.KernelTime.TotalSeconds.ToString("0.0")).Append(" s, cores ").Append(Environment.ProcessorCount);
            }
            catch (Exception ex) { sb.Append("; app CPU unavailable (").Append(ex.Message.Trim()).Append(")"); }
            try
            {
                var s = Windows.System.Diagnostics.SystemDiagnosticInfo.GetForCurrentSystem().CpuUsage.GetReport();
                sb.Append("; system CPU time user ").Append(s.UserTime.TotalSeconds.ToString("0")).Append(" s, kernel ").Append(s.KernelTime.TotalSeconds.ToString("0")).Append(" s, idle ").Append(s.IdleTime.TotalSeconds.ToString("0")).Append(" s");
            }
            catch (Exception ex) { sb.Append("; system CPU unavailable (").Append(ex.Message.Trim()).Append(")"); }
            return sb.ToString();
        }
    }
}
