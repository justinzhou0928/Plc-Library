using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.Mitsubishi
{
    /// <summary>Mitsubishi MC 连接配置</summary>
    public sealed record MitsubishiDriverConfig
    {
        /// <summary>PLC 地址</summary>
        public string Host { get; init; } = "127.0.0.1";

        /// <summary>端口，默认 6000</summary>
        public int Port { get; init; } = 6000;

        /// <summary>读写超时（毫秒）</summary>
        public int Timeout { get; init; } = 3000;

        /// <summary>协议类型（MC/A1E/A3C/FX）</summary>
        public string ProtocolType { get; init; } = "MC";

        /// <summary>从连接串解析配置</summary>
        /// <param name="connectionString">连接字符串</param>
        public static MitsubishiDriverConfig Parse(string connectionString)
            => ConnectionStringBinder.Bind<MitsubishiDriverConfig>(connectionString);
    }
}
