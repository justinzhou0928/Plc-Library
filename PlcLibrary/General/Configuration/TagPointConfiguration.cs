using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.General.Configuration
{
    /// <summary>点位配置</summary>
    public sealed record TagPointConfiguration
    {
        /// <summary>点位业务标识，同一设备内唯一</summary>
        [Required]
        [MinLength(1)]
        public required string TagId { get; init; }

        /// <summary>点位显示名</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>协议地址，如 DB1.DBW0、40001、ns=2;s=Tag</summary>
        [Required]
        [MinLength(1)]
        public required string Address { get; init; }

        /// <summary>点位数据类型（bool/short/ushort/int/uint/long/ulong/float/double/string 等）；为空时由驱动按默认类型处理</summary>
        public string DataType { get; init; } = string.Empty;

        /// <summary>字符串点位的字节长度（ASCII 下等于字符数）；仅 <see cref="DataType"/> 为 string 时使用
        /// Modbus 寄存器数 = ceil(Length / 2)</summary>
        public int Length { get; init; }

        /// <summary>多字节数值或字符串的字节序（AB/BA/ABCD/CDAB/BADC/DCBA/ABCDEFGH...）
        /// 为空时使用设备级默认值，仍为空则按 AB（大端）处理</summary>
        public string ByteOrder { get; init; } = string.Empty;

        /// <summary>推送订阅的采样间隔（毫秒）；0 表示使用默认</summary>
        public int SamplingInterval { get; init; }

        /// <summary>推送订阅的队列长度，默认 1</summary>
        public int QueueSize { get; init; } = 1;
    }
}
