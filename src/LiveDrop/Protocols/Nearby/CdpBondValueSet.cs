using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LiveDrop.Protocols.Nearby
{
    internal sealed class CdpValueSet
    {
        internal readonly Dictionary<string, CdpValue> Values = new Dictionary<string, CdpValue>(StringComparer.Ordinal);

        internal CdpValueSet AddUInt32(string key, uint value) { Values[key] = new CdpValue(5, value); return this; }
        internal CdpValueSet AddUInt64(string key, ulong value) { Values[key] = new CdpValue(7, value); return this; }
        internal CdpValueSet AddGuid(string key, Guid value) { Values[key] = new CdpValue(15, value); return this; }
        internal CdpValueSet AddString(string key, string value) { Values[key] = new CdpValue(39, value ?? string.Empty); return this; }
        internal CdpValueSet AddUInt32Array(string key, IList<uint> value) { Values[key] = new CdpValue(24, value); return this; }
        internal CdpValueSet AddUInt64Array(string key, IList<ulong> value) { Values[key] = new CdpValue(26, value); return this; }
        internal CdpValueSet AddStringArray(string key, IList<string> value) { Values[key] = new CdpValue(40, value); return this; }
        internal CdpValueSet AddByteArray(string key, IList<byte> value) { Values[key] = new CdpValue(20, value); return this; }

        internal uint GetUInt32(string key) { return (uint)Values[key].Value; }
        internal ulong GetUInt64(string key) { return (ulong)Values[key].Value; }
        internal Guid GetGuid(string key) { return (Guid)Values[key].Value; }
        internal IList<byte> GetBytes(string key) { return (IList<byte>)Values[key].Value; }
        internal IList<uint> GetUInt32Array(string key) { return (IList<uint>)Values[key].Value; }
        internal IList<ulong> GetUInt64Array(string key) { return (IList<ulong>)Values[key].Value; }
        internal IList<string> GetStringArray(string key) { return (IList<string>)Values[key].Value; }

        internal byte[] Serialize()
        {
            var stream = new MemoryStream();
            WriteFieldHeader(stream, 13, 1);
            stream.WriteByte(18); stream.WriteByte(10); WriteVarUInt(stream, (uint)Values.Count);
            foreach (var entry in Values)
            {
                WriteWString(stream, entry.Key);
                WriteFieldHeader(stream, 16, 0); WriteVarUInt(stream, (uint)(entry.Value.PropertyType * 2));
                WritePropertyValue(stream, entry.Value);
                stream.WriteByte(0);
            }
            stream.WriteByte(0);
            return stream.ToArray();
        }

        internal static CdpValueSet Parse(byte[] data)
        {
            var stream = new MemoryStream(data ?? new byte[0]);
            if (ReadFieldHeader(stream) != 0x2D) throw new InvalidDataException("NearShare ValueSet is not a map.");
            if (stream.ReadByte() != 18 || stream.ReadByte() != 10) throw new InvalidDataException("NearShare ValueSet map types are invalid.");
            var count = ReadVarUInt(stream); if (count > 1024) throw new InvalidDataException("NearShare ValueSet is too large.");
            var result = new CdpValueSet();
            for (var i = 0; i < (int)count; i++)
            {
                var key = ReadWString(stream);
                var typeHeader = ReadFieldHeader(stream); if ((typeHeader & 31) != 16 || ((typeHeader >> 5) & 7) != 0) throw new InvalidDataException("NearShare property type is invalid.");
                var propertyType = (int)(ReadVarUInt(stream) >> 1);
                var valueHeader = ReadFieldHeader(stream); var fieldId = valueHeader >> 5;
                if (fieldId == 6) fieldId = stream.ReadByte(); else if (fieldId == 7) fieldId = ReadUInt16Little(stream);
                var value = ReadPropertyValue(stream, propertyType, valueHeader & 31);
                result.Values[key] = new CdpValue(propertyType, value);
                if (stream.ReadByte() != 0) throw new InvalidDataException("NearShare property struct is invalid.");
            }
            if (stream.ReadByte() != 0) throw new InvalidDataException("NearShare ValueSet is not terminated.");
            return result;
        }

        private static void WritePropertyValue(Stream stream, CdpValue value)
        {
            var bondType = BondType(value.PropertyType);
            WriteFieldHeader(stream, bondType, 99 + value.PropertyType);
            switch (value.PropertyType)
            {
                case 5: WriteVarUInt(stream, (uint)value.Value); break;
                case 7: WriteVarUInt(stream, (ulong)value.Value); break;
                case 15:
                    WriteByte(stream, 10); WriteVarUInt(stream, 1); var guid = (Guid)value.Value; var bytes = guid.ToByteArray();
                    WriteFieldHeader(stream, 5, 0); WriteVarUInt(stream, BitConverter.ToUInt32(bytes, 0));
                    WriteFieldHeader(stream, 4, 1); WriteVarUInt(stream, BitConverter.ToUInt16(bytes, 4));
                    WriteFieldHeader(stream, 4, 2); WriteVarUInt(stream, BitConverter.ToUInt16(bytes, 6));
                    WriteFieldHeader(stream, 6, 3); WriteVarUInt(stream, BitConverter.ToUInt64(bytes, 8)); stream.WriteByte(0); break;
                case 39: WriteString(stream, (string)value.Value); break;
                case 20: WriteList(stream, 3, (IList<byte>)value.Value, (s, x) => s.WriteByte(x)); break;
                case 24: WriteList(stream, 5, (IList<uint>)value.Value, (s, x) => WriteVarUInt(s, x)); break;
                case 26: WriteList(stream, 6, (IList<ulong>)value.Value, (s, x) => WriteVarUInt(s, x)); break;
                case 40: WriteList(stream, 9, (IList<string>)value.Value, WriteString); break;
                default: throw new InvalidDataException("NearShare ValueSet property type is not implemented: " + value.PropertyType);
            }
        }

        private static object ReadPropertyValue(Stream stream, int propertyType, int bondType)
        {
            switch (propertyType)
            {
                case 5: return (uint)ReadVarUInt(stream);
                case 7: return ReadVarUInt(stream);
                case 15:
                    if (stream.ReadByte() != 10 || ReadVarUInt(stream) != 1) throw new InvalidDataException("NearShare Guid value is invalid.");
                    var data = new byte[16]; if (ReadFieldHeader(stream) != 5) throw new InvalidDataException(); WriteUInt32Little(data, 0, ReadVarUInt(stream)); if (ReadFieldHeader(stream) != 36) throw new InvalidDataException(); WriteUInt16Little(data, 4, (ushort)ReadVarUInt(stream)); if (ReadFieldHeader(stream) != 68) throw new InvalidDataException(); WriteUInt16Little(data, 6, (ushort)ReadVarUInt(stream)); if (ReadFieldHeader(stream) != 102) throw new InvalidDataException(); WriteUInt64Little(data, 8, ReadVarUInt(stream)); if (stream.ReadByte() != 0) throw new InvalidDataException(); return new Guid(data);
                case 39: return ReadString(stream);
                case 20: return ReadList(stream, 3, x => (byte)(ulong)x);
                case 24: return ReadList(stream, 5, x => (uint)(ulong)x);
                case 26: return ReadList(stream, 6, x => (ulong)x);
                case 40: return ReadStringList(stream);
                default: throw new InvalidDataException("NearShare ValueSet property type is not implemented: " + propertyType);
            }
        }

        private static int BondType(int propertyType)
        {
            switch (propertyType)
            {
                case 5: return 5; case 7: return 6; case 15: return 11; case 39: return 9;
                case 20: case 24: case 26: case 40: return 11; default: throw new InvalidDataException();
            }
        }

        private static void WriteList<T>(Stream stream, int elementType, IList<T> values, Action<Stream, T> writer)
        {
            WriteByte(stream, checked((byte)elementType)); WriteVarUInt(stream, (uint)values.Count); foreach (var value in values) writer(stream, value);
        }

        private static List<T> ReadList<T>(Stream stream, int elementType, Func<object, T> reader)
        {
            if (stream.ReadByte() != elementType) throw new InvalidDataException("NearShare list element type is invalid.");
            var count = ReadVarUInt(stream); if (count > 1024 * 1024) throw new InvalidDataException("NearShare list is too large.");
            var result = new List<T>((int)count); for (var i = 0; i < (int)count; i++) result.Add(reader(ReadVarUInt(stream))); return result;
        }

        private static IList<string> ReadStringList(Stream stream)
        {
            if (stream.ReadByte() != 9) throw new InvalidDataException("NearShare list element type is invalid.");
            var count = ReadVarUInt(stream); if (count > 1024 * 1024) throw new InvalidDataException("NearShare list is too large.");
            var result = new List<string>((int)count);
            for (var i = 0; i < (int)count; i++) result.Add(ReadString(stream));
            return result;
        }

        private static void WriteFieldHeader(Stream stream, int type, int id)
        {
            if (id <= 5) stream.WriteByte((byte)(type | (id << 5)));
            else if (id <= 255) { stream.WriteByte((byte)(type | 0xC0)); stream.WriteByte((byte)id); }
            else { stream.WriteByte((byte)(type | 0xE0)); WriteUInt16Little(stream, (ushort)id); }
        }

        private static int ReadFieldHeader(Stream stream)
        {
            var value = stream.ReadByte(); if (value < 0) throw new EndOfStreamException(); return value;
        }

        private static void WriteWString(Stream stream, string value) { var bytes = Encoding.Unicode.GetBytes(value ?? string.Empty); WriteVarUInt(stream, (uint)(bytes.Length / 2)); stream.Write(bytes, 0, bytes.Length); }
        private static string ReadWString(Stream stream) { var length = ReadVarUInt(stream); if (length > 65535) throw new InvalidDataException(); var bytes = new byte[checked((int)length * 2)]; ReadExactly(stream, bytes); return Encoding.Unicode.GetString(bytes); }
        private static void WriteString(Stream stream, string value) { var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty); WriteVarUInt(stream, (uint)bytes.Length); stream.Write(bytes, 0, bytes.Length); }
        private static string ReadString(Stream stream) { var length = ReadVarUInt(stream); if (length > 1024 * 1024) throw new InvalidDataException(); var bytes = new byte[checked((int)length)]; ReadExactly(stream, bytes); return Encoding.UTF8.GetString(bytes); }
        private static void WriteVarUInt(Stream stream, ulong value) { while (value >= 0x80) { stream.WriteByte((byte)(value | 0x80)); value >>= 7; } stream.WriteByte((byte)value); }
        private static ulong ReadVarUInt(Stream stream) { ulong result = 0; var shift = 0; while (shift < 64) { var item = stream.ReadByte(); if (item < 0) throw new EndOfStreamException(); result |= ((ulong)item & 0x7F) << shift; if ((item & 0x80) == 0) return result; shift += 7; } throw new InvalidDataException(); }
        private static void ReadExactly(Stream stream, byte[] buffer) { var offset = 0; while (offset < buffer.Length) { var read = stream.Read(buffer, offset, buffer.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; } }
        private static void WriteByte(Stream stream, byte value) { stream.WriteByte(value); }
        private static void WriteUInt16Little(Stream stream, ushort value) { stream.WriteByte((byte)value); stream.WriteByte((byte)(value >> 8)); }
        private static ushort ReadUInt16Little(Stream stream) { var a = stream.ReadByte(); var b = stream.ReadByte(); if (b < 0) throw new EndOfStreamException(); return (ushort)(a | (b << 8)); }
        private static void WriteUInt32Little(byte[] buffer, int offset, ulong value) { for (var i = 0; i < 4; i++) buffer[offset + i] = (byte)(value >> (i * 8)); }
        private static void WriteUInt16Little(byte[] buffer, int offset, ushort value) { buffer[offset] = (byte)value; buffer[offset + 1] = (byte)(value >> 8); }
        private static void WriteUInt64Little(byte[] buffer, int offset, ulong value) { for (var i = 0; i < 8; i++) buffer[offset + i] = (byte)(value >> (i * 8)); }
    }

    internal sealed class CdpValue
    {
        internal int PropertyType { get; private set; }
        internal object Value { get; private set; }
        internal CdpValue(int propertyType, object value) { PropertyType = propertyType; Value = value; }
    }
}
