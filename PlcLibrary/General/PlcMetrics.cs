using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PlcLibrary.General
{
    internal static class PlcMetrics
    {
        internal static readonly Meter Meter = new("PlcLibrary", "1.1.0");

        internal static readonly Counter<long> ReadsTotal = Meter.CreateCounter<long>(
            "plc.reads.total", "points", "Total tag points read");

        internal static readonly Histogram<double> ReadDuration = Meter.CreateHistogram<double>(
            "plc.read.duration", "s", "Read operation duration");

        internal static readonly Counter<long> ReadErrors = Meter.CreateCounter<long>(
            "plc.read.errors", "errors", "Total read operation errors");

        internal static readonly Histogram<double> AcquireDuration = Meter.CreateHistogram<double>(
            "plc.acquire.duration", "s", "Driver acquire duration");

        internal static readonly Counter<long> WriteOperations = Meter.CreateCounter<long>(
            "plc.write.total", "ops", "Total write operations");

        internal static readonly Counter<long> PipelineDispatched = Meter.CreateCounter<long>(
            "plc.pipeline.dispatched", "points", "Points dispatched via pipeline");

        internal static readonly Counter<long> PipelineDropped = Meter.CreateCounter<long>(
            "plc.pipeline.dropped", "points", "Points dropped due to subscriber backpressure");
    }
}
