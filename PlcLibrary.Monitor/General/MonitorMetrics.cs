using System.Diagnostics.Metrics;

namespace PlcLibrary.Monitor.General
{
    internal static class MonitorMetrics
    {
        // 与基础库共用 Meter 名 "PlcLibrary"，宿主接入 OpenTelemetry 时无需额外 AddMeter。
        internal static readonly Meter Meter = new("PlcLibrary", "1.1.0");

        internal static readonly Counter<long> Updates = Meter.CreateCounter<long>(
            "plc.monitor.updates", "points", "Total incoming points received by monitor");

        internal static readonly Counter<long> Changes = Meter.CreateCounter<long>(
            "plc.monitor.changes", "points", "Total points whose value/status changed and were published");
    }
}
