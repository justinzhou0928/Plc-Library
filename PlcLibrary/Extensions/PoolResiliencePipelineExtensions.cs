using PlcLibrary.DriverPool.Models;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using System;
using System.Threading;

namespace PlcLibrary.Extensions
{
    public static class PoolResiliencePipelineExtensions
    {
        public static ResiliencePipelineBuilder AddPoolStrategies(
            this ResiliencePipelineBuilder builder,
            PoolOptions options) => builder
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = options.MaxRetryAttempts,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = options.RetryDelay,
                    ShouldHandle = new PredicateBuilder()
                        .Handle<Exception>(ex => ex is not OperationCanceledException),
                })
                .AddTimeout(options.OperationTimeout)
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = options.CircuitBreakerMinimumThroughput,
                    BreakDuration = options.CircuitBreakerDuration,
                    ShouldHandle = new PredicateBuilder()
                        .Handle<Exception>(ex => ex is not OperationCanceledException),
                });
    }
}
