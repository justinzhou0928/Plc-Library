using System;
using System.IO.Ports;

namespace PlcLibrary.Modbus
{
    /// <summary>Modbus 连接串中的串口字符串参数 → System.IO.Ports 枚举映射（大小写不敏感）。</summary>
    internal static class SerialPortOptionsMapper
    {
        public static Parity ParseParity(string? parity) => parity?.Trim().ToUpperInvariant() switch
        {
            "ODD" => Parity.Odd,
            "EVEN" => Parity.Even,
            "MARK" => Parity.Mark,
            "SPACE" => Parity.Space,
            _ => Parity.None
        };

        public static StopBits ParseStopBits(string? stopBits) => stopBits?.Trim().ToUpperInvariant() switch
        {
            "ONE" => StopBits.One,
            "ONEPOINTFIVE" => StopBits.OnePointFive,
            "TWO" => StopBits.Two,
            _ => StopBits.One
        };
    }
}
