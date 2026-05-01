using System.Text;

namespace BedrockWorld.Nbt;

public class NbtWriter : IDisposable
{
    private readonly BinaryWriter _writer;
    private bool _disposed;
    
    public NbtWriter(Stream stream)
    {
        _writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
    }
    
    public void WriteRoot(NbtCompound compound)
    {
        WriteTag(NbtTagType.Compound, compound.Name);
        WriteCompound(compound);
    }
    
    public void WriteTag(NbtTagType type, string name)
    {
        _writer.Write((byte)type);
        if (type != NbtTagType.End)
        {
            WriteString(name);
        }
    }
    
    public void Write(NbtTag tag)
    {
        switch (tag.Type)
        {
            case NbtTagType.Byte:
                WriteByte((NbtByte)tag);
                break;
            case NbtTagType.Short:
                WriteShort((NbtShort)tag);
                break;
            case NbtTagType.Int:
                WriteInt((NbtInt)tag);
                break;
            case NbtTagType.Long:
                WriteLong((NbtLong)tag);
                break;
            case NbtTagType.Float:
                WriteFloat((NbtFloat)tag);
                break;
            case NbtTagType.Double:
                WriteDouble((NbtDouble)tag);
                break;
            case NbtTagType.ByteArray:
                WriteByteArray((NbtByteArray)tag);
                break;
            case NbtTagType.String:
                WriteStringTag((NbtString)tag);
                break;
            case NbtTagType.List:
                WriteList((NbtList)tag);
                break;
            case NbtTagType.Compound:
                WriteCompound((NbtCompound)tag);
                break;
            case NbtTagType.IntArray:
                WriteIntArray((NbtIntArray)tag);
                break;
            case NbtTagType.LongArray:
                WriteLongArray((NbtLongArray)tag);
                break;
        }
    }
    
    private void WriteByte(NbtByte tag)
    {
        _writer.Write(tag.Value);
    }
    
    private void WriteShort(NbtShort tag)
    {
        WriteLittleEndian(tag.Value);
    }
    
    private void WriteInt(NbtInt tag)
    {
        WriteLittleEndian(tag.Value);
    }
    
    private void WriteLong(NbtLong tag)
    {
        WriteLittleEndian(tag.Value);
    }
    
    private void WriteFloat(NbtFloat tag)
    {
        var bytes = BitConverter.GetBytes(tag.Value);
        Array.Reverse(bytes);
        _writer.Write(bytes);
    }
    
    private void WriteDouble(NbtDouble tag)
    {
        var bytes = BitConverter.GetBytes(tag.Value);
        Array.Reverse(bytes);
        _writer.Write(bytes);
    }
    
    private void WriteByteArray(NbtByteArray tag)
    {
        WriteLittleEndian(tag.Value.Length);
        _writer.Write(tag.Value);
    }
    
    private void WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteLittleEndian((ushort)bytes.Length);
        _writer.Write(bytes);
    }
    
    private void WriteStringTag(NbtString tag)
    {
        WriteString(tag.Value);
    }
    
    private void WriteList(NbtList tag)
    {
        _writer.Write((byte)tag.ListType);
        WriteLittleEndian(tag.Tags.Count);
        
        foreach (var item in tag.Tags)
        {
            Write(item);
        }
    }
    
    private void WriteCompound(NbtCompound tag)
    {
        foreach (var kvp in tag.Tags)
        {
            WriteTag(kvp.Value.Type, kvp.Key);
            Write(kvp.Value);
        }
        
        _writer.Write((byte)NbtTagType.End);
    }
    
    private void WriteIntArray(NbtIntArray tag)
    {
        WriteLittleEndian(tag.Value.Length);
        foreach (var val in tag.Value)
        {
            WriteLittleEndian(val);
        }
    }
    
    private void WriteLongArray(NbtLongArray tag)
    {
        WriteLittleEndian(tag.Value.Length);
        foreach (var val in tag.Value)
        {
            WriteLittleEndian(val);
        }
    }
    
    private void WriteLittleEndian(ushort value)
    {
        _writer.Write((byte)(value & 0xff));
        _writer.Write((byte)((value >> 8) & 0xff));
    }
    
    private void WriteLittleEndian(short value)
    {
        var u = (ushort)value;
        WriteLittleEndian(u);
    }
    
    private void WriteLittleEndian(int value)
    {
        var u = (uint)value;
        _writer.Write((byte)(u & 0xff));
        _writer.Write((byte)((u >> 8) & 0xff));
        _writer.Write((byte)((u >> 16) & 0xff));
        _writer.Write((byte)((u >> 24) & 0xff));
    }
    
    private void WriteLittleEndian(long value)
    {
        var u = (ulong)value;
        var lo = (uint)(u & 0xffffffff);
        var hi = (uint)(u >> 32);
        WriteLittleEndian((int)lo);
        WriteLittleEndian((int)hi);
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _writer.Dispose();
        _disposed = true;
    }
}
