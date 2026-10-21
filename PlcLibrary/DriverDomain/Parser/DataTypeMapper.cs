using System;
using System.Collections.Generic;

namespace PlcLibrary.DriverDomain.Parser
{
    public static class DataTypeMapper
    {
        private static readonly Dictionary<string, Type> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["bool"] = typeof(bool),
            ["byte"] = typeof(byte),
            ["sbyte"] = typeof(sbyte),
            ["short"] = typeof(short),
            ["ushort"] = typeof(ushort),
            ["int"] = typeof(int),
            ["uint"] = typeof(uint),
            ["long"] = typeof(long),
            ["ulong"] = typeof(ulong),
            ["float"] = typeof(float),
            ["double"] = typeof(double),
            ["string"] = typeof(string),
            ["int16"] = typeof(short),
            ["uint16"] = typeof(ushort),
            ["int32"] = typeof(int),
            ["uint32"] = typeof(uint),
            ["int64"] = typeof(long),
            ["uint64"] = typeof(ulong),
            ["real"] = typeof(float),
            ["lreal"] = typeof(double),
            ["single"] = typeof(float),
            ["dint"] = typeof(int),
            ["udint"] = typeof(uint),
            ["lint"] = typeof(long),
            ["ulint"] = typeof(ulong),
            ["word"] = typeof(ushort),
            ["dword"] = typeof(uint),
            ["lword"] = typeof(ulong),
            ["sint"] = typeof(sbyte),
            ["usint"] = typeof(byte),
        };

        /// <summary>解析数据类型名称或别名；未指定时返回 <see cref="int"/>（保持既有默认）。</summary>
        public static Type Resolve(string? dataType)
            => TryResolve(dataType, out var type) ? type : typeof(int);

        /// <summary>
        /// 严格解析数据类型名称或别名。空值返回 true 且 <paramref name="type"/> 为 <see cref="int"/>（兼容既有语义）；
        /// 未知名称返回 false，调用方应将其视为配置错误，避免静默回退到 int。
        /// </summary>
        public static bool TryResolve(string? dataType, out Type type)
        {
            if (string.IsNullOrWhiteSpace(dataType))
            {
                type = typeof(int);
                return true;
            }

            var name = dataType.Trim();
            if (Aliases.TryGetValue(name, out var alias))
            {
                type = alias;
                return true;
            }

            Type? resolved;
            try
            {
                resolved = Type.GetType(name, throwOnError: false);
            }
            catch (Exception ex) when (ex is ArgumentException or TypeLoadException or BadImageFormatException
                or NotSupportedException or System.IO.IOException)
            {
                resolved = null;
            }

            if (resolved is not null)
            {
                type = resolved;
                return true;
            }

            type = typeof(int);
            return false;
        }
    }
}
