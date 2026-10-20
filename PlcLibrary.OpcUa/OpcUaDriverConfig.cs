using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.OpcUa
{
    public sealed record OpcUaDriverConfig
    {
        /// <summary>OPC UA 服务端端点。</summary>
        public string Endpoint { get; init; } = "opc.tcp://localhost:4840";

        /// <summary>用户名；为空时使用匿名身份。</summary>
        public string? UserName { get; init; }

        /// <summary>密码。</summary>
        public string? Password { get; init; }

        /// <summary>安全模式：None / Sign / SignAndEncrypt。</summary>
        public string Security { get; init; } = "None";

        /// <summary>操作超时（毫秒）。</summary>
        public int Timeout { get; init; } = 5000;

        /// <summary>订阅发布间隔（毫秒）。</summary>
        public int PublishingInterval { get; init; } = 1000;

        /// <summary>会话超时（毫秒）。</summary>
        public int SessionTimeout { get; init; } = 60000;

        /// <summary>是否自动接受不受信任的服务端证书。</summary>
        public bool AutoAcceptCertificate { get; init; } = false;

        /// <summary>应用证书存储目录。</summary>
        public string PkiOwnPath { get; init; } = "PlcLibrary.OpcUa/pki/own";

        /// <summary>受信任的对端证书存储目录。</summary>
        public string PkiTrustedPath { get; init; } = "PlcLibrary.OpcUa/pki/trusted";

        /// <summary>被拒绝证书存储目录。</summary>
        public string PkiRejectedPath { get; init; } = "PlcLibrary.OpcUa/pki/rejected";

        /// <summary>受信任的颁发者证书存储目录（OPC UA 堆栈配置校验的必填项）。</summary>
        public string PkiIssuerPath { get; init; } = "PlcLibrary.OpcUa/pki/issuer";

        /// <summary>从连接字符串绑定配置。</summary>
        public static OpcUaDriverConfig Parse(string connectionString)
            => ConnectionStringBinder.Bind<OpcUaDriverConfig>(connectionString);
    }
}
