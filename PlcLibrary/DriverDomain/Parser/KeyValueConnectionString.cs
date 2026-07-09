using System;
using System.Collections.Generic;

namespace PlcLibrary.DriverDomain.Parser
{
    public static class KeyValueConnectionString
    {
        public static IReadOnlyDictionary<string, string> Parse(string connectionString)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(connectionString)) return dict;

            foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':', 2);
                if (kv.Length != 2) continue;
                dict[kv[0].Trim()] = kv[1].Trim();
            }
            return dict;
        }
    }
}
