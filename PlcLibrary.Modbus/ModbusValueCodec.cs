using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.Modbus
{
    internal enum ModbusValueKind
    {
        UInt16,
        Int16,
        UInt32,
        Int32,
        UInt64,
        Int64,
        Single,
        Double,
        Boolean,
        String,
    }

    /// <summary>
    /// 字节序映射。AB 为恒等（大端），BA 为字内字节交换，CDAB 为字交换，DCBA 为全反；
    /// 64 位使用 8 字符排列（如 GHEFCDAB 表示 16 位字逆序）。
    /// </summary>
    internal readonly struct ByteOrderMap
    {
        private readonly uint _map;

        public int Width { get; }

        private ByteOrderMap(int width, uint map)
        {
            Width = width;
            _map = map;
        }

        public byte this[int index] => (byte)((_map >> (index * 4)) & 0xF);

        public static ByteOrderMap Identity(int width)
        {
            var map = 0u;
            for (var i = 0; i < width; i++)
                map |= (uint)i << (i * 4);
            return new ByteOrderMap(width, map);
        }

        public static bool TryParse(string? text, int width, out ByteOrderMap map, out string? error)
        {
            map = default;
            error = null;
            var value = text?.Trim();

            if (string.IsNullOrEmpty(value) || string.Equals(value, "AB", StringComparison.OrdinalIgnoreCase))
            {
                if (width is < 2 or > 8)
                {
                    error = $"ByteOrder is not supported for {width}-byte values";
                    return false;
                }

                map = Identity(width);
                return true;
            }

            if (width is < 2 or > 8)
            {
                error = $"ByteOrder is not supported for {width}-byte values";
                return false;
            }

            if (value.Length != width)
            {
                error = $"ByteOrder '{value}' has {value.Length} bytes but the data type is {width} bytes";
                return false;
            }

            var used = 0;
            var packed = 0u;
            for (var i = 0; i < width; i++)
            {
                var c = char.ToUpperInvariant(value[i]);
                if (c < 'A' || c >= 'A' + width)
                {
                    error = $"ByteOrder '{value}' contains invalid byte '{value[i]}'";
                    return false;
                }

                var index = c - 'A';
                if ((used & (1 << index)) != 0)
                {
                    error = $"ByteOrder '{value}' repeats byte '{value[i]}'";
                    return false;
                }

                used |= 1 << index;
                packed |= (uint)index << (i * 4);
            }

            map = new ByteOrderMap(width, packed);
            return true;
        }
    }

    /// <summary>Modbus 多寄存器值编解码：ushort[] + ByteOrder + DataType → CLR 值，读写共用。</summary>
    internal static class ModbusValueCodec
    {
        internal static bool TryGetKind(string? dataType, out ModbusValueKind kind, out string? error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(dataType))
            {
                kind = ModbusValueKind.UInt16;
                return true;
            }

            if (!DataTypeMapper.TryResolve(dataType, out var type))
            {
                kind = default;
                error = $"Unsupported DataType '{dataType}'. Supported: bool, short, ushort, int, uint, long, ulong, float, double, string";
                return false;
            }

            switch (Type.GetTypeCode(type))
            {
                case TypeCode.UInt16:
                    kind = ModbusValueKind.UInt16;
                    return true;
                case TypeCode.Int16:
                    kind = ModbusValueKind.Int16;
                    return true;
                case TypeCode.UInt32:
                    kind = ModbusValueKind.UInt32;
                    return true;
                case TypeCode.Int32:
                    kind = ModbusValueKind.Int32;
                    return true;
                case TypeCode.UInt64:
                    kind = ModbusValueKind.UInt64;
                    return true;
                case TypeCode.Int64:
                    kind = ModbusValueKind.Int64;
                    return true;
                case TypeCode.Single:
                    kind = ModbusValueKind.Single;
                    return true;
                case TypeCode.Double:
                    kind = ModbusValueKind.Double;
                    return true;
                case TypeCode.Boolean:
                    kind = ModbusValueKind.Boolean;
                    return true;
                case TypeCode.String:
                    kind = ModbusValueKind.String;
                    return true;
                default:
                    kind = default;
                    error = $"DataType '{dataType}' is not supported on Modbus 16-bit registers";
                    return false;
            }
        }

        internal static int GetByteWidth(ModbusValueKind kind)
            => kind switch
            {
                ModbusValueKind.UInt16 or ModbusValueKind.Int16 or ModbusValueKind.Boolean => 2,
                ModbusValueKind.UInt32 or ModbusValueKind.Int32 or ModbusValueKind.Single => 4,
                ModbusValueKind.UInt64 or ModbusValueKind.Int64 or ModbusValueKind.Double => 8,
                _ => 2,
            };

        internal static bool TryGetWordCount(ModbusValueKind kind, int stringLength, out int wordCount, out string? error)
        {
            error = null;
            switch (kind)
            {
                case ModbusValueKind.UInt16:
                case ModbusValueKind.Int16:
                case ModbusValueKind.Boolean:
                    wordCount = 1;
                    return true;
                case ModbusValueKind.UInt32:
                case ModbusValueKind.Int32:
                case ModbusValueKind.Single:
                    wordCount = 2;
                    return true;
                case ModbusValueKind.UInt64:
                case ModbusValueKind.Int64:
                case ModbusValueKind.Double:
                    wordCount = 4;
                    return true;
                case ModbusValueKind.String:
                    if (stringLength <= 0)
                    {
                        wordCount = 0;
                        error = "String Length must be greater than zero";
                        return false;
                    }

                    wordCount = stringLength / 2 + stringLength % 2;
                    return true;
                default:
                    wordCount = 0;
                    error = "Unsupported data type";
                    return false;
            }
        }

        internal static bool TryParseByteOrder(string? text, int width, out ByteOrderMap map, out string? error)
            => ByteOrderMap.TryParse(text, width, out map, out error);

        internal static bool IsValidByteOrderText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), "AB", StringComparison.OrdinalIgnoreCase))
                return true;

            var value = text.Trim();
            if (value.Length is not (2 or 4 or 8))
                return false;

            return ByteOrderMap.TryParse(value, value.Length, out _, out _);
        }

        internal static object Decode(ReadOnlySpan<ushort> registers, ModbusValueKind kind, int stringLength,
            in ByteOrderMap byteOrder)
        {
            if (kind == ModbusValueKind.String)
                return DecodeString(registers, stringLength, byteOrder);

            if (kind == ModbusValueKind.Boolean)
                return registers[0] != 0;

            var width = GetByteWidth(kind);
            Span<byte> canonical = stackalloc byte[8];
            for (var i = 0; i < registers.Length && i < 4; i++)
            {
                canonical[2 * i] = (byte)(registers[i] >> 8);
                canonical[2 * i + 1] = (byte)registers[i];
            }

            Span<byte> value = stackalloc byte[8];
            for (var i = 0; i < width; i++)
                value[i] = canonical[byteOrder[i]];

            switch (kind)
            {
                case ModbusValueKind.UInt16:
                    return BinaryPrimitives.ReadUInt16BigEndian(value);
                case ModbusValueKind.Int16:
                    return BinaryPrimitives.ReadInt16BigEndian(value);
                case ModbusValueKind.UInt32:
                    return BinaryPrimitives.ReadUInt32BigEndian(value);
                case ModbusValueKind.Int32:
                    return BinaryPrimitives.ReadInt32BigEndian(value);
                case ModbusValueKind.UInt64:
                    return BinaryPrimitives.ReadUInt64BigEndian(value);
                case ModbusValueKind.Int64:
                    return BinaryPrimitives.ReadInt64BigEndian(value);
                case ModbusValueKind.Single:
                    return BinaryPrimitives.ReadSingleBigEndian(value);
                case ModbusValueKind.Double:
                    return BinaryPrimitives.ReadDoubleBigEndian(value);
                default:
                    throw new NotSupportedException($"Unsupported Modbus value kind '{kind}'");
            }
        }

        internal static bool TryEncode(object? value, ModbusValueKind kind, int stringLength,
            in ByteOrderMap byteOrder, Span<ushort> destination, out string? error)
        {
            error = null;

            if (value is null)
            {
                error = "Value is null";
                return false;
            }

            if (kind == ModbusValueKind.String)
                return TryEncodeString(value, stringLength, byteOrder, destination, out error);

            if (kind == ModbusValueKind.Boolean)
            {
                if (destination.Length < 1)
                {
                    error = "Destination buffer is too small";
                    return false;
                }

                if (!TryToBoolean(value, out var boolean, out error))
                    return false;

                destination[0] = boolean ? (ushort)1 : (ushort)0;
                return true;
            }

            var width = GetByteWidth(kind);
            if (destination.Length < width / 2)
            {
                error = "Destination buffer is too small";
                return false;
            }

            Span<byte> valueBytes = stackalloc byte[8];
            switch (kind)
            {
                case ModbusValueKind.UInt16:
                case ModbusValueKind.Int16:
                {
                    if (!TryToUInt64(value, out var raw, out error)) return false;
                    BinaryPrimitives.WriteUInt16BigEndian(valueBytes, unchecked((ushort)raw));
                    break;
                }
                case ModbusValueKind.UInt32:
                case ModbusValueKind.Int32:
                {
                    if (!TryToUInt64(value, out var raw, out error)) return false;
                    BinaryPrimitives.WriteUInt32BigEndian(valueBytes, unchecked((uint)raw));
                    break;
                }
                case ModbusValueKind.UInt64:
                case ModbusValueKind.Int64:
                {
                    if (!TryToUInt64(value, out var raw, out error)) return false;
                    BinaryPrimitives.WriteUInt64BigEndian(valueBytes, raw);
                    break;
                }
                case ModbusValueKind.Single:
                {
                    if (!TryToSingle(value, out var single, out error)) return false;
                    BinaryPrimitives.WriteSingleBigEndian(valueBytes, single);
                    break;
                }
                case ModbusValueKind.Double:
                {
                    if (!TryToDouble(value, out var dbl, out error)) return false;
                    BinaryPrimitives.WriteDoubleBigEndian(valueBytes, dbl);
                    break;
                }
                default:
                    error = $"Unsupported Modbus value kind '{kind}'";
                    return false;
            }

            Span<byte> canonical = stackalloc byte[8];
            for (var i = 0; i < width; i++)
                canonical[byteOrder[i]] = valueBytes[i];

            for (var i = 0; i < width / 2; i++)
                destination[i] = (ushort)((canonical[2 * i] << 8) | canonical[2 * i + 1]);

            return true;
        }

        private static string DecodeString(ReadOnlySpan<ushort> registers, int stringLength,
            in ByteOrderMap byteOrder)
        {
            var bytes = new byte[registers.Length * 2];
            var swap = byteOrder[0] != 0;
            for (var i = 0; i < registers.Length; i++)
            {
                var hi = (byte)(registers[i] >> 8);
                var lo = (byte)registers[i];
                bytes[2 * i] = swap ? lo : hi;
                bytes[2 * i + 1] = swap ? hi : lo;
            }

            var length = Math.Min(stringLength, bytes.Length);
            var text = Encoding.ASCII.GetString(bytes, 0, length);
            var nul = text.IndexOf('\0');
            return nul >= 0 ? text[..nul] : text;
        }

        private static bool TryEncodeString(object? value, int stringLength, in ByteOrderMap byteOrder,
            Span<ushort> destination, out string? error)
        {
            error = null;

            if (value is not string text)
            {
                error = "String point requires a string value";
                return false;
            }

            if (text.Length > stringLength)
            {
                error = $"String value length {text.Length} exceeds configured Length {stringLength}";
                return false;
            }

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] > 0x7F)
                {
                    error = "Non-ASCII string values are not supported";
                    return false;
                }
            }

            var bytes = Encoding.ASCII.GetBytes(text);
            var swap = byteOrder[0] != 0;
            for (var i = 0; i < destination.Length; i++)
            {
                var hiIndex = 2 * i;
                var loIndex = hiIndex + 1;
                var hi = hiIndex < bytes.Length ? bytes[hiIndex] : (byte)0;
                var lo = loIndex < bytes.Length ? bytes[loIndex] : (byte)0;
                destination[i] = swap ? (ushort)((lo << 8) | hi) : (ushort)((hi << 8) | lo);
            }

            return true;
        }

        private static bool TryToBoolean(object value, out bool result, out string? error)
        {
            try
            {
                result = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                result = false;
                error = $"Value '{value}' cannot be converted to bool";
                return false;
            }
        }

        private static bool TryToUInt64(object value, out ulong result, out string? error)
        {
            try
            {
                result = value switch
                {
                    ulong u => u,
                    uint u32 => u32,
                    ushort u16 => u16,
                    byte b => b,
                    _ => unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture)),
                };
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                result = 0;
                error = $"Value '{value}' cannot be converted to an integer";
                return false;
            }
        }

        private static bool TryToSingle(object value, out float result, out string? error)
        {
            try
            {
                result = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                result = 0;
                error = $"Value '{value}' cannot be converted to float";
                return false;
            }
        }

        private static bool TryToDouble(object value, out double result, out string? error)
        {
            try
            {
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                result = 0;
                error = $"Value '{value}' cannot be converted to double";
                return false;
            }
        }
    }
}
