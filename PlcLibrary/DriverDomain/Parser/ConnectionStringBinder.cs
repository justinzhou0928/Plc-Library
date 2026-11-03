using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace PlcLibrary.DriverDomain.Parser
{
    public static class ConnectionStringBinder
    {
        /// <summary>
        /// 绑定连接串。默认校验未知键：键名必须是 <typeparamref name="T"/> 的公开属性名
        /// （大小写不敏感）或 <paramref name="additionalKeys"/> 中的别名；拼写错误不再静默使用默认值。
        /// </summary>
        public static T Bind<T>(string connectionString, params string[] additionalKeys)
        {
            var dict = KeyValueConnectionString.Parse(connectionString);
            ValidateKnownKeys<T>(dict, additionalKeys);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(dict.Select(kv =>
                    new KeyValuePair<string, string?>(kv.Key, kv.Value)))
                .Build();
            return config.Get<T>()
                ?? throw new InvalidOperationException($"Failed to bind connection string to {typeof(T).Name}");
        }

        private static void ValidateKnownKeys<T>(IReadOnlyDictionary<string, string> values, string[] additionalKeys)
        {
            var allowed = new HashSet<string>(
                typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase);
            foreach (var key in additionalKeys)
                allowed.Add(key);

            var unknown = values.Keys.Where(k => !allowed.Contains(k)).ToArray();
            if (unknown.Length == 0)
                return;

            throw new InvalidOperationException(
                $"Unknown connection string key(s) for {typeof(T).Name}: {string.Join(", ", unknown)}. " +
                $"Allowed keys: {string.Join(", ", allowed.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))}.");
        }
    }
}
