using System.Text;

namespace BedrockWorld.Nbt;

public class NbtReader
{
    private readonly BinaryReader _reader;
    
    public NbtReader(Stream stream)
    {
        _reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
    }
    
    public NbtReader(byte[] data) : this(new MemoryStream(data))
    {
    }
    
    public NbtCompound ReadRoot()
    {
        if (_reader.BaseStream.Position >= _reader.BaseStream.Length)
            return new NbtCompound();
        var type = (NbtTagType)_reader.ReadByte();
        if (type == NbtTagType.End)
            return new NbtCompound();

        var name = ReadString();
        var compound = ReadCompound();
        compound.Name = name;
        return compound;
    }
    
    public NbtTag ReadTag(NbtTagType type)
    {
        return type switch
        {
            NbtTagType.End => new NbtEnd(),
            NbtTagType.Byte => ReadByte(),
            NbtTagType.Short => ReadShort(),
            NbtTagType.Int => ReadInt(),
            NbtTagType.Long => ReadLong(),
            NbtTagType.Float => ReadFloat(),
            NbtTagType.Double => ReadDouble(),
            NbtTagType.ByteArray => ReadByteArray(),
            NbtTagType.String => ReadStringTag(),
            NbtTagType.List => ReadList(),
            NbtTagType.Compound => ReadCompound(),
            NbtTagType.IntArray => ReadIntArray(),
            NbtTagType.LongArray => ReadLongArray(),
            _ => SkipUnknownTag(type)
        };
    }

    private NbtTag SkipUnknownTag(NbtTagType type)
    {
        try
        {
            if (_reader.BaseStream.Position >= _reader.BaseStream.Length)
                return new NbtEnd();

            var tagName = ReadString();
            var compound = new NbtCompound { Name = tagName };

            while (true)
            {
                if (_reader.BaseStream.Position >= _reader.BaseStream.Length)
                    break;
                var nextType = (NbtTagType)_reader.ReadByte();
                if (nextType == NbtTagType.End)
                    break;
                if (nextType >= NbtTagType.IntArray)
                {
                    SkipUnknownTag(nextType);
                }
                else
                {
                    var nextName = ReadString();
                    SkipValueForType(nextType);
                }
            }
            return compound;
        }
        catch
        {
            return new NbtEnd();
        }
    }

    private void SkipValueForType(NbtTagType type)
    {
        try
        {
            switch (type)
            {
                case NbtTagType.Byte:
                    _reader.ReadByte();
                    break;
                case NbtTagType.Short:
                    _reader.ReadBytes(2);
                    break;
                case NbtTagType.Int:
                case NbtTagType.Float:
                    _reader.ReadBytes(4);
                    break;
                case NbtTagType.Long:
                case NbtTagType.Double:
                    _reader.ReadBytes(8);
                    break;
                case NbtTagType.ByteArray:
                    var baLen = ReadLittleEndianInt32();
                    _reader.ReadBytes(baLen);
                    break;
                case NbtTagType.String:
                    var strLen = ReadLittleEndianUInt16();
                    _reader.ReadBytes(strLen);
                    break;
                case NbtTagType.List:
                    _reader.ReadBytes(5);
                    break;
                case NbtTagType.Compound:
                    while (true)
                    {
                        if (_reader.BaseStream.Position >= _reader.BaseStream.Length)
                            break;
                        var innerType = (NbtTagType)_reader.ReadByte();
                        if (innerType == NbtTagType.End)
                            break;
                        SkipValueForType(innerType);
                    }
                    break;
                case NbtTagType.IntArray:
                    var iaLen = ReadLittleEndianInt32();
                    _reader.ReadBytes(iaLen * 4);
                    break;
                case NbtTagType.LongArray:
                    var laLen = ReadLittleEndianInt32();
                    _reader.ReadBytes(laLen * 8);
                    break;
            }
        }
        catch { }
    }
    
    private NbtByte ReadByte()
    {
        if (_reader.BaseStream.Position >= _reader.BaseStream.Length)
            return new NbtByte(0);
        return new NbtByte(_reader.ReadByte());
    }

    private NbtShort ReadShort()
    {
        if (_reader.BaseStream.Position + 2 > _reader.BaseStream.Length)
            return new NbtShort(0);
        return new NbtShort(ReadLittleEndianInt16());
    }

    private NbtInt ReadInt()
    {
        if (_reader.BaseStream.Position + 4 > _reader.BaseStream.Length)
            return new NbtInt(0);
        return new NbtInt(ReadLittleEndianInt32());
    }

    private NbtLong ReadLong()
    {
        if (_reader.BaseStream.Position + 8 > _reader.BaseStream.Length)
            return new NbtLong(0);
        return new NbtLong(ReadLittleEndianInt64());
    }

    private NbtFloat ReadFloat()
    {
        if (_reader.BaseStream.Position + 4 > _reader.BaseStream.Length)
            return new NbtFloat(0);
        var bytes = _reader.ReadBytes(4);
        Array.Reverse(bytes);
        return new NbtFloat(BitConverter.ToSingle(bytes, 0));
    }

    private NbtDouble ReadDouble()
    {
        if (_reader.BaseStream.Position + 8 > _reader.BaseStream.Length)
            return new NbtDouble(0);
        var bytes = _reader.ReadBytes(8);
        Array.Reverse(bytes);
        return new NbtDouble(BitConverter.ToDouble(bytes, 0));
    }

    private NbtByteArray ReadByteArray()
    {
        if (_reader.BaseStream.Position + 4 > _reader.BaseStream.Length)
            return new NbtByteArray(Array.Empty<byte>());
        var length = ReadLittleEndianInt32();
        if (_reader.BaseStream.Position + length > _reader.BaseStream.Length)
            return new NbtByteArray(Array.Empty<byte>());
        return new NbtByteArray(_reader.ReadBytes(length));
    }

    private string ReadString()
    {
        if (_reader.BaseStream.Position + 2 > _reader.BaseStream.Length)
            return string.Empty;
        var length = ReadLittleEndianUInt16();
        if (_reader.BaseStream.Position + length > _reader.BaseStream.Length)
            return string.Empty;
        var bytes = _reader.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes);
    }
    
    private NbtString ReadStringTag()
    {
        return new NbtString(ReadString());
    }
    
    private NbtList ReadList()
    {
        if (_reader.BaseStream.Position >= _reader.BaseStream.Length)
            return new NbtList(NbtTagType.End);
        var listType = (NbtTagType)_reader.ReadByte();
        if (_reader.BaseStream.Position + 4 > _reader.BaseStream.Length)
            return new NbtList(listType);
        var length = ReadLittleEndianInt32();
        var list = new NbtList(listType);

        for (var i = 0; i < length; i++)
        {
            if (_reader.BaseStream.Position >= _reader.BaseStream.Length)
                break;
            list.Tags.Add(ReadTag(listType));
        }

        return list;
    }

    private NbtCompound ReadCompound()
    {
        var compound = new NbtCompound();

        while (true)
        {
            if (_reader.BaseStream.Position >= _reader.BaseStream.Length)
                break;
            var type = (NbtTagType)_reader.ReadByte();
            if (type == NbtTagType.End)
                break;

            var name = ReadString();
            var tag = ReadTag(type);
            tag.Name = name;
            compound.Tags[name] = tag;
        }

        return compound;
    }

    private NbtIntArray ReadIntArray()
    {
        if (_reader.BaseStream.Position + 4 > _reader.BaseStream.Length)
            return new NbtIntArray(Array.Empty<int>());
        var length = ReadLittleEndianInt32();
        if (length < 0 || _reader.BaseStream.Position + length * 4 > _reader.BaseStream.Length)
            return new NbtIntArray(Array.Empty<int>());
        var array = new int[length];
        for (var i = 0; i < length; i++)
        {
            if (_reader.BaseStream.Position + 4 > _reader.BaseStream.Length)
                break;
            array[i] = ReadLittleEndianInt32();
        }
        return new NbtIntArray(array);
    }

    private NbtLongArray ReadLongArray()
    {
        if (_reader.BaseStream.Position + 4 > _reader.BaseStream.Length)
            return new NbtLongArray(Array.Empty<long>());
        var length = ReadLittleEndianInt32();
        if (length < 0 || _reader.BaseStream.Position + length * 8 > _reader.BaseStream.Length)
            return new NbtLongArray(Array.Empty<long>());
        var array = new long[length];
        for (var i = 0; i < length; i++)
        {
            if (_reader.BaseStream.Position + 8 > _reader.BaseStream.Length)
                break;
            array[i] = ReadLittleEndianInt64();
        }
        return new NbtLongArray(array);
    }
    
    private ushort ReadLittleEndianUInt16()
    {
        var bytes = _reader.ReadBytes(2);
        return (ushort)(bytes[0] | (bytes[1] << 8));
    }
    
    private short ReadLittleEndianInt16()
    {
        var bytes = _reader.ReadBytes(2);
        return (short)(bytes[0] | (bytes[1] << 8));
    }
    
    private int ReadLittleEndianInt32()
    {
        var bytes = _reader.ReadBytes(4);
        return bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
    }
    
    private long ReadLittleEndianInt64()
    {
        var bytes = _reader.ReadBytes(8);
        uint lo = (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
        uint hi = (uint)(bytes[4] | (bytes[5] << 8) | (bytes[6] << 16) | (bytes[7] << 24));
        return (long)hi << 32 | lo;
    }
}
