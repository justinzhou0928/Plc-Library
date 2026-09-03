using System;
using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.Monitor.Models
{
    public sealed class MonitorOptions
    {
        public const string SectionName = "Monitor";

        /// <summary>缓存条目空闲超时：点位超过该时长未收到新数据则移除缓存（设备热更新下线后不残留）。
        /// <c>TimeSpan.Zero</c> 表示禁用自动清理。</summary>
        [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
        public TimeSpan EntryIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);
    }
}
