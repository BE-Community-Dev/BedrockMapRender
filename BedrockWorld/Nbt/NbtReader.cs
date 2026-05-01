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
            _ => throw new NotSupportedException($"Unknown NBT tag type: {type}")
        };
    }
    
    private NbtByte ReadByte()
    {
        return new NbtByte(_reader.ReadByte());
    }
    
    private NbtShort ReadShort()
    {
        return new NbtShort(ReadLittleEndianInt16());
    }
    
    private NbtInt ReadInt()
    {
        return new NbtInt(ReadLittleEndianInt32());
    }
    
    private NbtLong ReadLong()
    {
        return new NbtLong(ReadLittleEndianInt64());
    }
    
    private NbtFloat ReadFloat()
    {
        var bytes = _reader.ReadBytes(4);
        Array.Reverse(bytes);
        return new NbtFloat(BitConverter.ToSingle(bytes, 0));
    }
    
    private NbtDouble ReadDouble()
    {
        var bytes = _reader.ReadBytes(8);
        Array.Reverse(bytes);
        return new NbtDouble(BitConverter.ToDouble(bytes, 0));
    }
    
    private NbtByteArray ReadByteArray()
    {
        var length = ReadLittleEndianInt32();
        return new NbtByteArray(_reader.ReadBytes(length));
    }
    
    private string ReadString()
    {
        var length = ReadLittleEndianUInt16();
        var bytes = _reader.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes);
    }
    
    private NbtString ReadStringTag()
    {
        return new NbtString(ReadString());
    }
    
    private NbtList ReadList()
    {
        var listType = (NbtTagType)_reader.ReadByte();
        var length = ReadLittleEndianInt32();
        var list = new NbtList(listType);
        
        for (var i = 0; i < length; i++)
        {
            list.Tags.Add(ReadTag(listType));
        }
        
        return list;
    }
    
    private NbtCompound ReadCompound()
    {
        var compound = new NbtCompound();
        
        while (true)
        {
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
        var length = ReadLittleEndianInt32();
        var array = new int[length];
        for (var i = 0; i < length; i++)
        {
            array[i] = ReadLittleEndianInt32();
        }
        return new NbtIntArray(array);
    }
    
    private NbtLongArray ReadLongArray()
    {
        var length = ReadLittleEndianInt32();
        var array = new long[length];
        for (var i = 0; i < length; i++)
        {
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
