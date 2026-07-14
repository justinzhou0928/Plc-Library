using Microsoft.Extensions.Logging;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using System;
using System.Threading;

namespace PlcLibrary.Extensions
{
    public static class PoolResiliencePipelineExtensions
    {
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
