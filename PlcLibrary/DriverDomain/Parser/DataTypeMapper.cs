using System;
using System.Collections.Generic;

namespace PlcLibrary.DriverDomain.Parser
{
    public static class DataTypeMapper
    {
        private static readonly Dictionary<string, Type> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["bool"]   = typeof(bool),
            ["byte"]   = typeof(byte),
            ["sbyte"]  = typeof(sbyte),
            ["short"]  = typeof(short),
            ["ushort"] = typeof(ushort),
            ["int"]    = typeof(int),
            ["uint"]   = typeof(uint),
            ["long"]   = typeof(long),
            ["ulong"]  = typeof(ulong),
            ["float"]  = typeof(float),
            ["double"] = typeof(double),
            ["string"] = typeof(string),
        };

        public static Type Resolve(string? dataType)
        {
            if (string.IsNullOrEmpty(dataType))
                return typeof(int);

            if (Aliases.TryGetValue(dataType, out var t))
                return t;

            return Type.GetType(dataType, throwOnError: false) ?? typeof(int);
        }
    }
}
