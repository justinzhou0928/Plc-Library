using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Pipeline.Engine
{
    internal sealed class PipelineHost(DriverResultPipeline pipeline) : BackgroundService
    {
        private readonly DriverResultPipeline _pipeline = pipeline;

        protected override async Task ExecuteAsync(CancellationToken ct)
            => await _pipeline.ConsumeAsync(ct).ConfigureAwait(false);

        public override async Task StopAsync(CancellationToken ct)
        {
            _pipeline.StopConsuming();
            await base.StopAsync(ct).ConfigureAwait(false);
        }

        public override void Dispose()
        {
            _pipeline.DisposeResources();
            base.Dispose();
        }
    }
}
