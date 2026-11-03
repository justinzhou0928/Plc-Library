using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.General.Configuration
{
    /// <summary>设备配置</summary>
    public sealed record DeviceConfiguration
    {
        /// <summary>是否启用该设备</summary>
        public bool Enabled { get; init; } = true;

        /// <summary>设备唯一标识，参与监控缓存键</summary>
        [Required]
        [MinLength(1)]
        public required string Id { get; init; }

        /// <summary>显示名</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>协议名，必须与驱动上的 [ProtocolDriverName] 精确匹配</summary>
        [Required]
        [MinLength(1)]
        public required string Protocol { get; init; }

        /// <summary>连接字符串，格式 key:value;key:value</summary>
        [Required]
        [MinLength(1)]
        public required string ConnectionString { get; init; }

        /// <summary>连接/重连超时</summary>
        public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>采集周期</summary>
        [TimeSpanMinimum("00:00:00.001")]
        public TimeSpan CollectionInterval { get; init; } = TimeSpan.FromSeconds(1);

        /// <summary>设备点位列表</summary>
        [MinLength(1)]
        public TagPointConfiguration[] TagPoints { get; init; } = [];

        /// <summary>校验设备与点位配置</summary>
        /// <param name="errors">校验错误列表；通过时为空</param>
        /// <returns>配置是否有效</returns>
        public bool Validate(out IReadOnlyList<ValidationResult> errors)
        {
            List<ValidationResult> list = [];
            Validator.TryValidateObject(this, new ValidationContext(this), list, true);

            var seenTagIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tag in TagPoints)
            {
                List<ValidationResult> tagResults = [];
                if (!Validator.TryValidateObject(tag, new ValidationContext(tag), tagResults, true))
                    list.AddRange(tagResults);

                if (!string.IsNullOrEmpty(tag.TagId) && !seenTagIds.Add(tag.TagId))
                    list.Add(new ValidationResult($"TagId '{tag.TagId}' 重复", new[] { nameof(TagPoints) }));
            }

            errors = list;
            return list.Count == 0;
        }
    }
}
