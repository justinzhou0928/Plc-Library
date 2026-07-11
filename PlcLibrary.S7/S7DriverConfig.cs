using PlcLibrary.DriverDomain.Parser;
using S7.Net;
using System;

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
        {
            var config = ConnectionStringBinder.Bind<S7DriverConfig>(connectionString);
            var dict = KeyValueConnectionString.Parse(connectionString);
            if (dict.TryGetValue("cpu", out var cpu) && Enum.TryParse<CpuType>(cpu, true, out var cpuType))
                config = config with { CpuType = cpuType };
            return config;
        }
    }
}
