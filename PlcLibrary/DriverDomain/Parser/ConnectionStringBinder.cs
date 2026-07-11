using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Linq;

namespace PlcLibrary.DriverDomain.Parser
{
    public static class ConnectionStringBinder
    {
        public static T Bind<T>(string connectionString) where T : new()
        {
            var dict = KeyValueConnectionString.Parse(connectionString);
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(dict.Select(kv =>
                    new KeyValuePair<string, string?>(kv.Key, kv.Value)))
                .Build();
            return config.Get<T>()!;
        }
    }
}
