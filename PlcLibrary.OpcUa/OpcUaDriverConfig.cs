using System;
using Opc.Ua;
using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.OpcUa
{
    /// <summary>OPC UA 连接配置</summary>
    public sealed record OpcUaDriverConfig
    {
        private static readonly string[] AllowedSecurityModes = ["None", "Sign", "SignAndEncrypt"];

        /// <summary>OPC UA 服务端端点。</summary>
        public string Endpoint { get; init; } = "opc.tcp://localhost:4840";

        /// <summary>用户名；为空时使用匿名身份。</summary>
        public string? UserName { get; init; }

        /// <summary>密码。</summary>
        public string? Password { get; init; }

        /// <summary>安全模式：None / Sign / SignAndEncrypt；非法值在 <see cref="Parse"/> 时抛异常。</summary>
        public string Security { get; init; } = "None";

        /// <summary>操作超时（毫秒）。</summary>
        public int Timeout { get; init; } = 5000;

        /// <summary>订阅发布间隔（毫秒）。</summary>
        public int PublishingInterval { get; init; } = 1000;

        /// <summary>会话超时（毫秒）。</summary>
        public int SessionTimeout { get; init; } = 60_000;

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

        /// <summary>从连接字符串绑定配置；非法安全模式直接抛异常，避免静默降级为 None。</summary>
        public static OpcUaDriverConfig Parse(string connectionString)
        {
            var config = ConnectionStringBinder.Bind<OpcUaDriverConfig>(connectionString);
            if (!TryParseSecurityMode(config.Security, out _))
            {
                throw new InvalidOperationException(
                    $"Invalid OPC UA security '{config.Security}'. Allowed values: None, Sign, SignAndEncrypt.");
            }

            return config;
        }

        /// <summary>严格解析安全模式；只接受 None/Sign/SignAndEncrypt（大小写不敏感）。</summary>
        internal static bool TryParseSecurityMode(string? value, out MessageSecurityMode mode)
        {
            mode = MessageSecurityMode.None;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var name = value.Trim();
            foreach (var allowed in AllowedSecurityModes)
            {
                if (!string.Equals(name, allowed, StringComparison.OrdinalIgnoreCase))
                    continue;

                mode = Enum.Parse<MessageSecurityMode>(allowed);
                return true;
            }

            return false;
        }
    }
}
