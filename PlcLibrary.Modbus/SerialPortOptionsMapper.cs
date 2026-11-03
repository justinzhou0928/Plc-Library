using System;
using System.IO.Ports;

namespace PlcLibrary.Modbus
{
    /// <summary>Modbus 连接串中的串口字符串参数 → System.IO.Ports 枚举映射（大小写不敏感）。</summary>
    internal static class SerialPortOptionsMapper
    {
        /// <summary>解析校验位；空值使用默认 None，非空未知值返回 false，不再静默回退。</summary>
        public static bool TryParseParity(string? parity, out Parity result)
        {
            result = Parity.None;
            if (string.IsNullOrWhiteSpace(parity))
                return true;

            switch (parity.Trim().ToUpperInvariant())
            {
                case "NONE":
                    return true;
                case "ODD":
                    result = Parity.Odd;
                    return true;
                case "EVEN":
                    result = Parity.Even;
                    return true;
                case "MARK":
                    result = Parity.Mark;
                    return true;
                case "SPACE":
                    result = Parity.Space;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>解析停止位；空值使用默认 One，非空未知值返回 false，不再静默回退。</summary>
        public static bool TryParseStopBits(string? stopBits, out StopBits result)
        {
            result = StopBits.One;
            if (string.IsNullOrWhiteSpace(stopBits))
                return true;

            switch (stopBits.Trim().ToUpperInvariant())
            {
                case "ONE":
                    return true;
                case "ONEPOINTFIVE":
                    result = StopBits.OnePointFive;
                    return true;
                case "TWO":
                    result = StopBits.Two;
                    return true;
                default:
                    return false;
            }
        }

        public static Parity ParseParity(string? parity)
            => TryParseParity(parity, out var result)
                ? result
                : throw new InvalidOperationException(
                    $"Invalid Modbus Parity '{parity}'. Allowed values: None, Odd, Even, Mark, Space.");

        public static StopBits ParseStopBits(string? stopBits)
            => TryParseStopBits(stopBits, out var result)
                ? result
                : throw new InvalidOperationException(
                    $"Invalid Modbus StopBits '{stopBits}'. Allowed values: One, OnePointFive, Two.");
    }
}
