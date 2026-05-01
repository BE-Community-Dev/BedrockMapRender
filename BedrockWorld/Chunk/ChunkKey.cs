namespace BedrockWorld.Chunk;

public class ChunkKey
{
    public ChunkPos Pos { get; set; }
    public ChunkRecordTag Tag { get; set; }
    public sbyte? SubChunkY { get; set; }
    
    public ChunkKey(ChunkPos pos, ChunkRecordTag tag, sbyte? subChunkY = null)
    {
        Pos = pos;
        Tag = tag;
        SubChunkY = subChunkY;
    }
    
    public byte[] Encode()
    {
        var list = new List<byte>();
        
        list.AddRange(BitConverter.GetBytes(Pos.X));
        list.AddRange(BitConverter.GetBytes(Pos.Z));
        
        if (Pos.Dimension != Dimension.Overworld)
        {
            list.AddRange(BitConverter.GetBytes((int)Pos.Dimension));
        }
        
        list.Add((byte)Tag);
        
        if (SubChunkY.HasValue)
        {
            list.Add((byte)SubChunkY.Value);
        }
        
        return list.ToArray();
    }
    
    public static ChunkKey? Decode(byte[] data)
    {
        if (data.Length < 9)
            return null;
        
        var x = BitConverter.ToInt32(data, 0);
        var z = BitConverter.ToInt32(data, 4);
        
        if (x < -2_000_000 || x > 2_000_000 || z < -2_000_000 || z > 2_000_000)
            return null;
        
        var offset = 8;
        var dimension = Dimension.Overworld;
        
        if (data.Length >= 13)
        {
            dimension = (Dimension)BitConverter.ToInt32(data, 8);
            offset = 12;
        }
        
        var tag = (ChunkRecordTag)data[offset];
        offset++;
        
        sbyte? subChunkY = null;
        if (data.Length > offset)
        {
            subChunkY = (sbyte)data[offset];
        }
        
        return new ChunkKey(new ChunkPos(x, z, dimension), tag, subChunkY);
    }
    
    public static ChunkKey SubChunk(ChunkPos pos, sbyte y)
    {
        return new ChunkKey(pos, ChunkRecordTag.SubChunkPrefix, y);
    }
    
    public static byte[] Prefix(ChunkPos pos)
    {
        var list = new List<byte>();
        list.AddRange(BitConverter.GetBytes(pos.X));
        list.AddRange(BitConverter.GetBytes(pos.Z));
        if (pos.Dimension != Dimension.Overworld)
        {
            list.AddRange(BitConverter.GetBytes((int)pos.Dimension));
        }
        return list.ToArray();
    }
}
