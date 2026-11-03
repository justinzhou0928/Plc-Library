using Microsoft.Extensions.Configuration;
using PlcLibrary.DriverDomain.Parser;
using S7.Net;

namespace PlcLibrary.S7
{
    /// <summary>S7 连接配置</summary>
    public sealed record S7DriverConfig
    {
        /// <summary>PLC 地址</summary>
        public string Host { get; init; } = "127.0.0.1";

        /// <summary>端口，默认 102</summary>
        public int Port { get; init; } = 102;

        /// <summary>读写超时（毫秒）</summary>
        public int Timeout { get; init; } = 3000;

        /// <summary>机架号</summary>
        public short Rack { get; init; }

        /// <summary>槽号</summary>
        public short Slot { get; init; }

        /// <summary>CPU 类型；连接串可用 <c>cpu</c> 别名配置，非法值在 <see cref="Parse"/> 时抛异常</summary>
        [ConfigurationKeyName("cpu")]
        public CpuType CpuType { get; init; } = CpuType.S71200;

        /// <summary>从连接串解析配置</summary>
        /// <param name="connectionString">连接字符串</param>
        public static S7DriverConfig Parse(string connectionString)
            => ConnectionStringBinder.Bind<S7DriverConfig>(connectionString, "cpu");
    }
}
