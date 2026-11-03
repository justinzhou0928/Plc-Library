using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.AllenBradley
{
    /// <summary>Allen-Bradley CIP/EtherNet-IP 连接配置</summary>
    public sealed record AllenBradleyDriverConfig
    {
        /// <summary>PLC 地址</summary>
        public string Host { get; init; } = "127.0.0.1";

        /// <summary>端口，默认 44818</summary>
        public int Port { get; init; } = 44818;

        /// <summary>读写超时（毫秒）</summary>
        public int Timeout { get; init; } = 5000;

        /// <summary>CIP 路径</summary>
        public string Path { get; init; } = "";

        /// <summary>是否使用 Connected 消息</summary>
        public bool UseConnected { get; init; } = false;

        /// <summary>从连接串解析配置</summary>
        /// <param name="connectionString">连接字符串</param>
        public static AllenBradleyDriverConfig Parse(string connectionString)
            => ConnectionStringBinder.Bind<AllenBradleyDriverConfig>(connectionString);
    }
}
