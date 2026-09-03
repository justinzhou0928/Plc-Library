using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Monitor.Engine
{
    internal sealed class PlcMonitorHost(PlcMonitor monitor) : BackgroundService
    {
        private readonly PlcMonitor _monitor = monitor;

        protected override Task ExecuteAsync(CancellationToken ct)
            => _monitor.RunEvictionAsync(ct);

        public override void Dispose()
        {
            _monitor.DisposeResources();
            base.Dispose();
        }
    }
}
