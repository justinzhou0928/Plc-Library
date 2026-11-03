using Microsoft.Extensions.Options;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.Monitor.Interfaces;
using PlcLibrary.Monitor.Models;
using PlcLibrary.Pipeline.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PlcLibrary.Monitor.Engine
{
    internal sealed class PlcMonitor(IOptions<MonitorOptions> options) : IPlcMonitor, IDataHandler
    {
        private const int EvictionPeriodSeconds = 60;
        private const int SubscriptionCapacity = 1;

        private static readonly TimeSpan EvictionPeriod = TimeSpan.FromSeconds(EvictionPeriodSeconds);

        private readonly TimeSpan _entryIdleTimeout = options.Value.EntryIdleTimeout;
        private readonly ConcurrentDictionary<PlcPointId, Entry> _entries = new();
        private readonly ConcurrentDictionary<Guid, Subscription> _subscribers = new();
        private int _disposed;

        public ValueTask HandleAsync(DriverResult result, CancellationToken ct)
        {
            MonitorMetrics.Updates.Add(1, new TagList { { "device.id", result.DeviceId } });

            var key = new PlcPointId(result.DeviceId, result.TagId);
            if (!TryStore(key, result, DateTime.UtcNow.Ticks, out var changed) || !changed)
                return ValueTask.CompletedTask;

            MonitorMetrics.Changes.Add(1, new TagList { { "device.id", result.DeviceId } });
            FanOut(key, result);
            return ValueTask.CompletedTask;
        }

        public DriverResult? Get(string deviceId, string tagId)
            => _entries.TryGetValue(new PlcPointId(deviceId, tagId), out var entry) ? entry.Result : null;

        public IReadOnlyList<DriverResult> GetDevice(string deviceId)
        {
            var list = new List<DriverResult>();
            foreach (var (key, entry) in _entries)
                if (key.DeviceId == deviceId)
                    list.Add(entry.Result);
            return list;
        }

        public IAsyncEnumerable<DriverResult> SubscribeAsync(string deviceId, string tagId, CancellationToken ct = default)
        {
            // 立即注册订阅（而非延迟到首次 MoveNextAsync），避免「调用→枚举」间隙丢失变更
            var sub = CreateSubscription(deviceId, tagId);
            _subscribers[sub.Id] = sub;
            return ReadSubscriptionAsync(sub, tagId, ct);
        }

        public IAsyncEnumerable<DriverResult> SubscribeDeviceAsync(string deviceId, CancellationToken ct = default)
        {
            var sub = CreateSubscription(deviceId, null);
            _subscribers[sub.Id] = sub;
            return ReadSubscriptionAsync(sub, null, ct);
        }

        private async IAsyncEnumerable<DriverResult> ReadSubscriptionAsync(
            Subscription sub, string? tagId, [EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                // 先注册、再产出当前快照，避免「读取快照→注册」之间的丢失更新
                DriverResult? last = null;
                if (tagId is not null
                    && _entries.TryGetValue(new PlcPointId(sub.DeviceId, tagId), out var current))
                {
                    yield return current.Result;
                    last = current.Result;
                }

                await foreach (var item in sub.Channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    // 边界去重：快照与首个通道元素可能重叠（注册后立即发生变更）
                    if (last is { } l && !Changed(l, item))
                        continue;
                    yield return item;
                    last = item;
                }
            }
            finally
            {
                if (_subscribers.TryRemove(sub.Id, out _))
                    sub.Channel.Writer.TryComplete();
            }
        }

        internal async Task RunEvictionAsync(CancellationToken ct)
        {
            if (_entryIdleTimeout <= TimeSpan.Zero) return;

            using var timer = new PeriodicTimer(EvictionPeriod);
            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    EvictStaleEntries();
            }
            catch (OperationCanceledException) { }
        }

        internal void DisposeResources()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var (_, sub) in _subscribers)
                sub.Channel.Writer.TryComplete();
            _subscribers.Clear();
            _entries.Clear();
        }

        internal void EvictStaleEntries()
        {
            if (Volatile.Read(ref _disposed) != 0 || _entryIdleTimeout <= TimeSpan.Zero) return;

            var cutoff = DateTime.UtcNow.Ticks - _entryIdleTimeout.Ticks;
            foreach (var (key, entry) in _entries)
            {
                if (entry.LastSeenTicks >= cutoff) continue;
                // 值匹配移除：并发刚更新的条目不会被误删
                _entries.TryRemove(new KeyValuePair<PlcPointId, Entry>(key, entry));
            }
        }

        /// <summary>
        /// 原子写入缓存条目：值/质量变化时替换并返回 changed=true，仅未变化时刷新最后活跃时间。
        /// 用 CAS 循环替代全局锁，避免高点数下所有设备串行化；同一变化只有一个线程能 CAS 成功，
        /// 因此通知仍然只发一次。
        /// </summary>
        private bool TryStore(PlcPointId key, DriverResult result, long now, out bool changed)
        {
            changed = false;
            while (true)
            {
                if (_entries.TryGetValue(key, out var existing))
                {
                    var valueChanged = Changed(existing.Result, result);
                    var updated = valueChanged ? new Entry(result, now) : existing with { LastSeenTicks = now };
                    if (!_entries.TryUpdate(key, updated, existing)) continue;
                    changed = valueChanged;
                    return true;
                }

                if (_entries.TryAdd(key, new Entry(result, now)))
                {
                    changed = true;
                    return true;
                }
            }
        }

        private void FanOut(PlcPointId key, DriverResult result)
        {
            if (_subscribers.IsEmpty) return;

            foreach (var (_, sub) in _subscribers)
            {
                if (!Matches(sub, key)) continue;
                sub.Channel.Writer.TryWrite(result);
            }
        }

        private static bool Changed(DriverResult a, DriverResult b)
            => a.Status != b.Status || !object.Equals(a.Value, b.Value);

        private static bool Matches(Subscription sub, PlcPointId key)
            => string.Equals(sub.DeviceId, key.DeviceId, StringComparison.Ordinal)
               && (sub.TagId is null || string.Equals(sub.TagId, key.TagId, StringComparison.Ordinal));

        private static Subscription CreateSubscription(string deviceId, string? tagId)
            => new()
            {
                Id = Guid.NewGuid(),
                DeviceId = deviceId,
                TagId = tagId,
                Channel = Channel.CreateBounded<DriverResult>(new BoundedChannelOptions(SubscriptionCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                }),
            };

        private readonly record struct Entry(DriverResult Result, long LastSeenTicks);
    }

    internal sealed record Subscription
    {
        public required Guid Id { get; init; }

        public required string DeviceId { get; init; }

        public string? TagId { get; init; }

        public required Channel<DriverResult> Channel { get; init; }
    }
}
