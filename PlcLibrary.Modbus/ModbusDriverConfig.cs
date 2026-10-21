using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.Modbus
{
    public sealed record ModbusDriverConfig
    {
        public string Host { get; init; } = "127.0.0.1";
        public int Port { get; init; } = 502;
        public int BaudRate { get; init; } = 9600;
        public string Parity { get; init; } = "None";
        public int DataBits { get; init; } = 8;
        public string StopBits { get; init; } = "One";
        public int Timeout { get; init; } = 3000;
        public byte SlaveId { get; init; } = 1;

        /// <summary>设备级默认字节序（AB/BA/ABCD/CDAB/...）；点位未指定时使用，空表示 AB（大端）。</summary>
        public string ByteOrder { get; init; } = string.Empty;

        public static ModbusDriverConfig Parse(string cs)
            => ConnectionStringBinder.Bind<ModbusDriverConfig>(cs);
    }
}
