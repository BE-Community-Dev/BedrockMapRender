namespace BedrockWorld.Chunk;

public struct ChunkPos : IEquatable<ChunkPos>
{
    public int X { get; set; }
    public int Z { get; set; }
    public Dimension Dimension { get; set; }
    
    public ChunkPos(int x, int z, Dimension dimension = Dimension.Overworld)
    {
        X = x;
        Z = z;
        Dimension = dimension;
    }
    
    public bool Equals(ChunkPos other)
    {
        return X == other.X && Z == other.Z && Dimension == other.Dimension;
    }
    
    public override bool Equals(object? obj)
    {
        return obj is ChunkPos other && Equals(other);
    }
    
    public override int GetHashCode()
    {
        return HashCode.Combine(X, Z, Dimension);
    }
    
    public static bool operator ==(ChunkPos left, ChunkPos right)
    {
        return left.Equals(right);
    }
    
    public static bool operator !=(ChunkPos left, ChunkPos right)
    {
        return !left.Equals(right);
    }
    
    public override string ToString()
    {
        return $"{X}, {Z} ({Dimension})";
    }
}
