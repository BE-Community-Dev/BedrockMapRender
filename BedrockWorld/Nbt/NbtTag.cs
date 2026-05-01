namespace BedrockWorld.Nbt;

public abstract class NbtTag
{
    public abstract NbtTagType Type { get; }
    public string Name { get; set; } = string.Empty;
}

public class NbtEnd : NbtTag
{
    public override NbtTagType Type => NbtTagType.End;
}

public class NbtByte : NbtTag
{
    public override NbtTagType Type => NbtTagType.Byte;
    public byte Value { get; set; }
    
    public NbtByte() {}
    public NbtByte(byte value) { Value = value; }
}

public class NbtShort : NbtTag
{
    public override NbtTagType Type => NbtTagType.Short;
    public short Value { get; set; }
    
    public NbtShort() {}
    public NbtShort(short value) { Value = value; }
}

public class NbtInt : NbtTag
{
    public override NbtTagType Type => NbtTagType.Int;
    public int Value { get; set; }
    
    public NbtInt() {}
    public NbtInt(int value) { Value = value; }
}

public class NbtLong : NbtTag
{
    public override NbtTagType Type => NbtTagType.Long;
    public long Value { get; set; }
    
    public NbtLong() {}
    public NbtLong(long value) { Value = value; }
}

public class NbtFloat : NbtTag
{
    public override NbtTagType Type => NbtTagType.Float;
    public float Value { get; set; }
    
    public NbtFloat() {}
    public NbtFloat(float value) { Value = value; }
}

public class NbtDouble : NbtTag
{
    public override NbtTagType Type => NbtTagType.Double;
    public double Value { get; set; }
    
    public NbtDouble() {}
    public NbtDouble(double value) { Value = value; }
}

public class NbtByteArray : NbtTag
{
    public override NbtTagType Type => NbtTagType.ByteArray;
    public byte[] Value { get; set; } = Array.Empty<byte>();
    
    public NbtByteArray() {}
    public NbtByteArray(byte[] value) { Value = value; }
}

public class NbtString : NbtTag
{
    public override NbtTagType Type => NbtTagType.String;
    public string Value { get; set; } = string.Empty;
    
    public NbtString() {}
    public NbtString(string value) { Value = value; }
}

public class NbtList : NbtTag
{
    public override NbtTagType Type => NbtTagType.List;
    public NbtTagType ListType { get; set; }
    public List<NbtTag> Tags { get; set; } = new List<NbtTag>();
    
    public NbtList() {}
    public NbtList(NbtTagType listType) { ListType = listType; }
}

public class NbtCompound : NbtTag
{
    public override NbtTagType Type => NbtTagType.Compound;
    public Dictionary<string, NbtTag> Tags { get; set; } = new Dictionary<string, NbtTag>();
    
    public NbtCompound() {}
    
    public T? Get<T>(string name) where T : NbtTag
    {
        if (Tags.TryGetValue(name, out var tag) && tag is T t)
            return t;
        return null;
    }
    
    public bool TryGet<T>(string name, out T? tag) where T : NbtTag
    {
        tag = Get<T>(name);
        return tag != null;
    }
    
    public NbtTag? this[string name]
    {
        get => Tags.TryGetValue(name, out var tag) ? tag : null;
        set
        {
            if (value != null)
            {
                value.Name = name;
                Tags[name] = value;
            }
            else
            {
                Tags.Remove(name);
            }
        }
    }
}

public class NbtIntArray : NbtTag
{
    public override NbtTagType Type => NbtTagType.IntArray;
    public int[] Value { get; set; } = Array.Empty<int>();
    
    public NbtIntArray() {}
    public NbtIntArray(int[] value) { Value = value; }
}

public class NbtLongArray : NbtTag
{
    public override NbtTagType Type => NbtTagType.LongArray;
    public long[] Value { get; set; } = Array.Empty<long>();
    
    public NbtLongArray() {}
    public NbtLongArray(long[] value) { Value = value; }
}
