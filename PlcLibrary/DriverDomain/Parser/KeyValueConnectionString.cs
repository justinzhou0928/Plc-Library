using System;
using System.Collections.Generic;
using System.Text;

namespace PlcLibrary.DriverDto.Parser
{
    public static class KeyValueConnectionString
    {
        private static readonly IReadOnlyDictionary<string, string> Empty =
            new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 解析连接字符串为 key → values（同一 key 多次出现时合并为列表）的查找表。
        /// </summary>
        public static IReadOnlyDictionary<string, string> Parse(string connectionString)
        {
            if (string.IsNullOrEmpty(connectionString))
                return Empty;

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':', 2);
                if (kv.Length != 2) continue;
                dict[kv[0].Trim()] = kv[1].Trim();
            }
            return dict;
        }

        /// <summary>
        /// 便捷读取（区分键别名）。返回第一个匹配别名存在的值，未找到返回 null。
        /// </summary>
        public static string? Get(this IReadOnlyDictionary<string, string> dict, params string[] aliases)
        {
            foreach (var alias in aliases)
                if (dict.TryGetValue(alias, out var v)) return v;
            return null;
        }
    }
}
