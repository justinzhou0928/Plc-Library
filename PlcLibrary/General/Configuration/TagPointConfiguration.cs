using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.General.Configuration
{
    public sealed record TagPointConfiguration
    {
        [Required]
        [MinLength(1)]
        public required string TagId { get; init; }

        public string Name { get; init; } = string.Empty;

        [Required]
        [MinLength(1)]
        public required string Address { get; init; }

        /// <summary>点位数据类型（bool/short/ushort/int/uint/long/ulong/float/double/string 等）；为空时由驱动按默认类型处理。</summary>
        public string DataType { get; init; } = string.Empty;

        /// <summary>
        /// 字符串点位的字节长度（ASCII 下等于字符数）；仅 <see cref="DataType"/> 为 string 时使用。
        /// Modbus 寄存器数 = ceil(Length / 2)。
        /// </summary>
        public int Length { get; init; }

        /// <summary>
        /// 多字节数值或字符串的字节序（AB/BA/ABCD/CDAB/BADC/DCBA/ABCDEFGH...）；
        /// 为空时使用设备级默认值，仍为空则按 AB（大端）处理。
        /// </summary>
        public string ByteOrder { get; init; } = string.Empty;

        public int SamplingInterval { get; init; }

        public int QueueSize { get; init; } = 1;
    }
}
