using BedrockLevelDB;
using BedrockWorld.Chunk;

namespace BedrockWorld;

public class BedrockWorld : IDisposable
{
    private readonly string _path;
    private readonly LevelDBDatabase _db;
    private bool _disposed;
    
    public LevelDat LevelDat { get; private set; }
    
    public BedrockWorld(string path, bool readOnly = true)
    {
        _path = path;
        var dbPath = Path.Combine(path, "db");
        _db = new LevelDBDatabase(dbPath);
        LevelDat = LevelDat.Load(Path.Combine(path, "level.dat"));
    }
    
    public byte[]? Get(ChunkKey key)
    {
        return _db.Get(key.Encode());
    }
    
    public List<ChunkPos> ListChunkPositions(Dimension dimension = Dimension.Overworld)
    {
        var positions = new HashSet<ChunkPos>();
        
        foreach (var kvp in _db.Iterate())
        {
            var key = ChunkKey.Decode(kvp.Key);
            if (key != null && key.Pos.Dimension == dimension &&
                (key.Tag == ChunkRecordTag.Data2D || 
                 key.Tag == ChunkRecordTag.Data3D ||
                 key.Tag == ChunkRecordTag.SubChunkPrefix))
            {
                positions.Add(key.Pos);
            }
        }
        
        return positions.ToList();
    }
    
    public SubChunk? GetSubChunk(ChunkPos pos, sbyte y)
    {
        var key = ChunkKey.SubChunk(pos, y);
        var data = Get(key);
        if (data == null)
            return null;
        
        return SubChunk.Parse(y, data);
    }
    
    public byte[]? GetChunkData(ChunkPos pos, ChunkRecordTag tag)
    {
        var key = new ChunkKey(pos, tag);
        return Get(key);
    }
    
    public (short?[,]? HeightMap, int[,]? Biomes) GetChunkTerrain(ChunkPos pos)
    {
        var data2D = GetChunkData(pos, ChunkRecordTag.Data2D);
        var data3D = GetChunkData(pos, ChunkRecordTag.Data3D);
        
        var heightMap = new short?[16, 16];
        var biomes = new int[16, 16];
        
        if (data2D != null && data2D.Length >= 512 + 256)
        {
            for (var z = 0; z < 16; z++)
            {
                for (var x = 0; x < 16; x++)
                {
                    var index = z * 16 + x;
                    heightMap[z, x] = BitConverter.ToInt16(data2D, index * 2);
                    
                    var biomeIndex = 512 + index;
                    if (biomeIndex < data2D.Length)
                    {
                        biomes[z, x] = data2D[biomeIndex];
                    }
                }
            }
        }
        else if (data3D != null)
        {
            ParseData3D(data3D, heightMap, biomes);
        }
        
        return (heightMap, biomes);
    }
    
    private void ParseData3D(byte[] data, short?[,] heightMap, int[,] biomes)
    {
        var offset = 0;
        
        if (data.Length >= 512)
        {
            for (var z = 0; z < 16; z++)
            {
                for (var x = 0; x < 16; x++)
                {
                    var index = z * 16 + x;
                    if (index * 2 + 1 < data.Length)
                    {
                        heightMap[z, x] = BitConverter.ToInt16(data, index * 2);
                    }
                }
            }
            offset = 256;
        }
        
        if (offset + 256 <= data.Length)
        {
            for (var z = 0; z < 16; z++)
            {
                for (var x = 0; x < 16; x++)
                {
                    var index = offset + z * 16 + x;
                    if (index < data.Length)
                    {
                        biomes[z, x] = data[index];
                    }
                }
            }
        }
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _db.Dispose();
        _disposed = true;
    }
}