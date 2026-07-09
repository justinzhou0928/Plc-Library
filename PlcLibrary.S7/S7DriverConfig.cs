using Microsoft.Extensions.Configuration;
using PlcLibrary.DriverDomain.Parser;
using S7.Net;
using System.Collections.Generic;
using System.Linq;

namespace PlcLibrary.S7
{
    public sealed record S7DriverConfig
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 102;
        public int Timeout { get; set; } = 3000;
        public short Rack { get; set; }
        public short Slot { get; set; }
        public CpuType CpuType { get; set; } = CpuType.S71200;

        public static S7DriverConfig Parse(string connectionString)
        {
            var dict = KeyValueConnectionString.Parse(connectionString);
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(dict.Select(kv =>
                    new KeyValuePair<string, string?>(kv.Key, kv.Value)))
                .Build();
            var result = new S7DriverConfig();
            config.Bind(result);
            return result;
        }
    }
}
