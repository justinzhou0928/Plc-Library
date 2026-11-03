using System;
using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.Modbus
{
    /// <summary>Modbus 连接配置</summary>
    public sealed record ModbusDriverConfig
    {
        /// <summary>设备地址</summary>
        public string Host { get; init; } = "127.0.0.1";

        /// <summary>TCP/UDP 端口，默认 502</summary>
        public int Port { get; init; } = 502;

        /// <summary>串口波特率</summary>
        public int BaudRate { get; init; } = 9600;

        /// <summary>串口校验位（None/Odd/Even/Mark/Space）；非法值在 <see cref="Parse"/> 时抛异常</summary>
        public string Parity { get; init; } = "None";

        /// <summary>串口数据位</summary>
        public int DataBits { get; init; } = 8;

        /// <summary>串口停止位（One/OnePointFive/Two）；非法值在 <see cref="Parse"/> 时抛异常</summary>
        public string StopBits { get; init; } = "One";

        /// <summary>读写超时（毫秒）</summary>
        public int Timeout { get; init; } = 3000;

        /// <summary>从站地址</summary>
        public byte SlaveId { get; init; } = 1;

        /// <summary>设备级默认字节序（AB/BA/ABCD/CDAB/...）；点位未指定时使用，空表示 AB（大端）</summary>
        public string ByteOrder { get; init; } = string.Empty;

        /// <summary>从连接串解析配置；非法串口枚举值直接抛异常，不静默回退默认值</summary>
        /// <param name="cs">连接字符串</param>
        public static ModbusDriverConfig Parse(string cs)
        {
            var config = ConnectionStringBinder.Bind<ModbusDriverConfig>(cs);
            if (!SerialPortOptionsMapper.TryParseParity(config.Parity, out _))
            {
                throw new InvalidOperationException(
                    $"Invalid Modbus Parity '{config.Parity}'. Allowed values: None, Odd, Even, Mark, Space.");
            }

            if (!SerialPortOptionsMapper.TryParseStopBits(config.StopBits, out _))
            {
                throw new InvalidOperationException(
                    $"Invalid Modbus StopBits '{config.StopBits}'. Allowed values: One, OnePointFive, Two.");
            }

            return config;
        }
    }
}
