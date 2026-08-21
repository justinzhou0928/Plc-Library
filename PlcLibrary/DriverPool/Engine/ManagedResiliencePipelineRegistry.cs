using Polly;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace PlcLibrary.DriverPool.Engine
{
    /// <summary>
    /// 可移除的弹性管线注册表：在 Polly 注册表能力基础上增加生命周期管理。
    /// 设备热更新移除时，随池/采集器一并删除对应 Pipeline（Polly 原生注册表只有 GetOrAddPipeline，
    /// 无法移除，长期运行会累积每设备的弹性管线）。
    /// </summary>
    internal sealed class ManagedResiliencePipelineRegistry
    {
        private readonly ConcurrentDictionary<string, Lazy<ResiliencePipeline>> _pipelines = new();

        public ResiliencePipeline GetOrAddPipeline(string key, Func<ResiliencePipelineBuilder, ResiliencePipelineBuilder> factory)
            => _pipelines.GetOrAdd(key, _ => new Lazy<ResiliencePipeline>(
                () => factory(new ResiliencePipelineBuilder()).Build(),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        /// <summary>移除指定管线（设备移除/池回收时调用）。返回是否存在。</summary>
        public bool TryRemove(string key) => _pipelines.TryRemove(key, out _);

        /// <summary>清空全部管线（宿主停止时调用）。</summary>
        public void Clear() => _pipelines.Clear();
    }
}
