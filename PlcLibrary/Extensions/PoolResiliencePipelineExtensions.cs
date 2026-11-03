using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace PlcLibrary.Extensions
{
    /// <summary>连接级 Polly 弹性管线扩展</summary>
    public static class PoolResiliencePipelineExtensions
    {
        /// <summary>添加连接级重试/超时/断路器策略</summary>
        /// <param name="builder">Polly 管线构建器</param>
        /// <param name="options">池配置</param>
        /// <param name="logger">日志</param>
        /// <param name="deviceId">设备标识（用于指标）</param>
        public static ResiliencePipelineBuilder AddPoolStrategies(
            this ResiliencePipelineBuilder builder,
            PoolOptions options,
            ILogger? logger = null,
            string? deviceId = null) => builder
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = options.MaxRetryAttempts,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = options.RetryDelay,
                    ShouldHandle = new PredicateBuilder()
                        .Handle<Exception>(ex => ex is not OperationCanceledException and not TimeoutRejectedException),
                })
                .AddTimeout(options.OperationTimeout)
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = options.CircuitBreakerFailureRatio,
                    MinimumThroughput = options.CircuitBreakerMinimumThroughput,
                    BreakDuration = options.CircuitBreakerDuration,
                    ShouldHandle = new PredicateBuilder()
                        .Handle<Exception>(ex => ex is not OperationCanceledException and not TimeoutRejectedException),
                    OnOpened = args =>
                    {
                        if (logger is not null && deviceId is not null)
                            PoolLog.LogCircuitBreakerOpened(logger, deviceId, options.CircuitBreakerDuration);
                        return default;
                    },
                    OnHalfOpened = args =>
                    {
                        if (logger is not null && deviceId is not null)
                            PoolLog.LogCircuitBreakerHalfOpened(logger, deviceId);
                        return default;
                    },
                    OnClosed = args =>
                    {
                        if (logger is not null && deviceId is not null)
                            PoolLog.LogCircuitBreakerClosed(logger, deviceId);
                        return default;
                    },
                });
    }
}
