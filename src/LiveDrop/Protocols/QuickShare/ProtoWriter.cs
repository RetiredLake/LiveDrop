using System;
using System.IO;
using System.Text;

namespace LiveDrop.Protocols.QuickShare
{
    internal sealed class ProtoWriter
    {
        private readonly MemoryStream _stream = new MemoryStream();

        internal byte[] ToArray() { return _stream.ToArray(); }

        internal void WriteInt32(int field, int value) { WriteTag(field, 0); WriteVarint((ulong)(long)value); }
        internal void WriteInt64(int field, long value) { WriteTag(field, 0); WriteVarint(unchecked((ulong)value)); }
        internal void WriteBool(int field, bool value) { WriteTag(field, 0); WriteVarint(value ? 1UL : 0UL); }
        internal void WriteEnum(int field, int value) { WriteInt32(field, value); }
        internal void WriteString(int field, string value) { WriteBytes(field, Encoding.UTF8.GetBytes(value ?? string.Empty)); }
        internal void WriteBytes(int field, byte[] value)
        {
            WriteTag(field, 2);
            WriteVarint((ulong)(value == null ? 0 : value.Length));
            if (value != null) _stream.Write(value, 0, value.Length);
        }
        internal void WriteMessage(int field, byte[] value) { WriteBytes(field, value); }

        private void WriteTag(int field, int wireType) { WriteVarint((ulong)((field << 3) | wireType)); }

        private void WriteVarint(ulong value)
        {
            while (value >= 0x80)
            {
                _stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            _stream.WriteByte((byte)value);
        }
    }

    internal sealed class ProtoReader
    {
        private readonly byte[] _data;
        private int _offset;

        internal ProtoReader(byte[] data) { _data = data ?? new byte[0]; }
        internal bool End { get { return _offset >= _data.Length; } }

        internal int ReadTag()
        {
            if (End) return 0;
            return checked((int)ReadVarint());
        }

        internal ulong ReadVarint()
        {
            ulong result = 0;
            var shift = 0;
            while (_offset < _data.Length && shift < 64)
            {
                var b = _data[_offset++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
            }
            throw new InvalidDataException("Invalid protobuf varint.");
        }

        internal byte[] ReadBytes()
        {
            var length = checked((int)ReadVarint());
            if (length < 0 || length > _data.Length - _offset) throw new InvalidDataException("Invalid protobuf length.");
            var value = new byte[length];
            Buffer.BlockCopy(_data, _offset, value, 0, length);
            _offset += length;
            return value;
        }

        internal string ReadString() { return Encoding.UTF8.GetString(ReadBytes()); }

        internal void Skip(int wireType)
        {
            switch (wireType)
            {
                case 0: ReadVarint(); return;
                case 1: Advance(8); return;
                case 2: Advance(checked((int)ReadVarint())); return;
                case 5: Advance(4); return;
                default: throw new InvalidDataException("Unsupported protobuf wire type " + wireType + ".");
            }
        }

        private void Advance(int count)
        {
            if (count < 0 || count > _data.Length - _offset) throw new InvalidDataException("Invalid protobuf field length.");
            _offset += count;
        }
    }
}

