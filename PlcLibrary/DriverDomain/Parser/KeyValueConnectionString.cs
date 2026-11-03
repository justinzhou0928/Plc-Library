using System;
using System.Collections.Generic;
using System.Text;

namespace PlcLibrary.DriverDomain.Parser
{
    public static class KeyValueConnectionString
    {
        /// <summary>
        /// 解析 <c>key:value;key:value</c>。值可用单/双引号包裹以保留 <c>;</c>、
        /// 首尾空格和冒号；引号内的同种引号用双写转义（如 <c>"a""b"</c>）。
        /// 未加引号的值保持旧的 Trim 行为。
        /// </summary>
        public static IReadOnlyDictionary<string, string> Parse(string connectionString)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(connectionString)) return dict;

            var text = connectionString;
            var i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ';'))
                    i++;
                if (i >= text.Length) break;

                var keyStart = i;
                while (i < text.Length && text[i] != ':' && text[i] != ';')
                    i++;

                if (i >= text.Length || text[i] == ';')
                {
                    // 没有冒号的畸形片段：跳到下一个分号，保持旧的“跳过”语义。
                    while (i < text.Length && text[i] != ';') i++;
                    continue;
                }

                var key = text[keyStart..i].Trim();
                i++; // ':'

                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;

                string value;
                if (i < text.Length && (text[i] == '"' || text[i] == '\''))
                {
                    var quote = text[i++];
                    var sb = new StringBuilder();
                    while (i < text.Length)
                    {
                        if (text[i] != quote)
                        {
                            sb.Append(text[i++]);
                            continue;
                        }

                        if (i + 1 < text.Length && text[i + 1] == quote)
                        {
                            sb.Append(quote);
                            i += 2;
                            continue;
                        }

                        i++; // closing quote
                        break;
                    }

                    // 跳过引号后的空白/多余字符，直到下一个分隔符。
                    while (i < text.Length && text[i] != ';') i++;
                    value = sb.ToString();
                }
                else
                {
                    var valueStart = i;
                    while (i < text.Length && text[i] != ';') i++;
                    value = text[valueStart..i].Trim();
                }

                if (key.Length > 0)
                    dict[key] = value;

                if (i < text.Length && text[i] == ';')
                    i++;
            }

            return dict;
        }
    }
}