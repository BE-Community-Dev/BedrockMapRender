using BedrockWorld.Nbt;

namespace BedrockWorld;

public class LevelDat
{
    public NbtCompound Data { get; set; }
    
    public string LevelName => GetString("LevelName") ?? "World";
    public int SpawnX => GetInt("SpawnX");
    public int SpawnY => GetInt("SpawnY");
    public int SpawnZ => GetInt("SpawnZ");
    public long Time => GetLong("Time");
    
    private LevelDat(NbtCompound data)
    {
        Data = data;
    }
    
    public static LevelDat Load(string path)
    {
        using var stream = File.OpenRead(path);
        var reader = new NbtReader(stream);
        var compound = reader.ReadRoot();
        return new LevelDat(compound);
    }
    
    private string? GetString(string name)
    {
        var tag = Data.Get<NbtString>(name);
        return tag?.Value;
    }
    
    private int GetInt(string name)
    {
        var tag = Data.Get<NbtInt>(name);
        return tag?.Value ?? 0;
    }
    
    private long GetLong(string name)
    {
        var tag = Data.Get<NbtLong>(name);
        return tag?.Value ?? 0;
    }
}
