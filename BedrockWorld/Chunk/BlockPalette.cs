using BedrockWorld.Nbt;

namespace BedrockWorld.Chunk;

public class BlockPalette
{
    public List<BlockState> States { get; set; } = new List<BlockState>();
    public ushort[]? Indices { get; set; }
    public ushort[] Counts { get; set; } = Array.Empty<ushort>();
    
    public BlockState? BlockStateAt(int localX, int localY, int localZ)
    {
        var index = BlockStorageIndex(localX, localY, localZ);
        if (Indices == null || index >= Indices.Length)
            return null;
        
        var paletteIndex = Indices[index];
        if (paletteIndex >= States.Count)
            return null;
        
        return States[paletteIndex];
    }
    
    public static int BlockStorageIndex(int localX, int localY, int localZ)
    {
        return localY + localZ * 16 + localX * 256;
    }
}