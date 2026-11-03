using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.Omron
{
    /// <summary>Omron FINS 连接配置</summary>
    public sealed record OmronDriverConfig
    {
        /// <summary>PLC 地址</summary>
        public string Host { get; init; } = "127.0.0.1";

        /// <summary>端口，默认 9600</summary>
        public int Port { get; init; } = 9600;

        /// <summary>读写超时（毫秒）</summary>
        public int Timeout { get; init; } = 3000;

        /// <summary>本地节点号</summary>
        public byte LocalNode { get; init; } = 1;

        /// <summary>目标节点号</summary>
        public byte DestinyNode { get; init; } = 2;

        /// <summary>是否使用 UDP</summary>
        public bool IsUdp { get; init; } = false;

        /// <summary>从连接串解析配置</summary>
        /// <param name="connectionString">连接字符串</param>
        public static OmronDriverConfig Parse(string connectionString)
            => ConnectionStringBinder.Bind<OmronDriverConfig>(connectionString);
    }
}
