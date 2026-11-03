using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.Bacnet
{
    /// <summary>BACnet/IP 连接配置</summary>
    public sealed record BacnetDriverConfig
    {
        /// <summary>设备地址</summary>
        public string Host { get; init; } = "127.0.0.1";

        /// <summary>UDP 端口，默认 47808</summary>
        public int Port { get; init; } = 47808;

        /// <summary>操作超时（毫秒）</summary>
        public int Timeout { get; init; } = 5000;

        /// <summary>目标设备实例号；0 表示直接使用 host/port</summary>
        public uint DeviceInstance { get; init; } = 0;

        /// <summary>本地绑定 IP；为空时由系统选择</summary>
        public string LocalEndpointIp { get; init; } = "";

        /// <summary>从连接串解析配置</summary>
        /// <param name="connectionString">连接字符串</param>
        public static BacnetDriverConfig Parse(string connectionString)
            => ConnectionStringBinder.Bind<BacnetDriverConfig>(connectionString);
    }
}
