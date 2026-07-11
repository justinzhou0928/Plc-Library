using PlcLibrary.DriverDomain.Parser;
using S7.Net;

namespace PlcLibrary.S7
{
    public sealed record S7DriverConfig
    {
        public string Host { get; init; } = "127.0.0.1";
        public int Port { get; init; } = 102;
        public int Timeout { get; init; } = 3000;
        public short Rack { get; init; }
        public short Slot { get; init; }
        public CpuType CpuType { get; init; } = CpuType.S71200;

        public static S7DriverConfig Parse(string connectionString)
            => ConnectionStringBinder.Bind<S7DriverConfig>(connectionString);
    }
}
