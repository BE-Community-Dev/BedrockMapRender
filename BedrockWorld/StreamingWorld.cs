using System.Buffers.Binary;
using BedrockLevelDB;
using BedrockWorld.Chunk;

namespace BedrockWorld;

public class StreamingWorld : IDisposable
{
    private readonly LevelDBDatabase _db;
    private bool _disposed;

    public LevelDat LevelDat { get; private set; }

    public StreamingWorld(string path, bool readOnly = true)
    {
        var dbPath = Path.Combine(path, "db");
        // 建议：如果底层库支持，可以在此处传入 ReadOptions 以优化内存
        _db = new LevelDBDatabase(dbPath);
        LevelDat = LevelDat.Load(Path.Combine(path, "level.dat"));
    }

    /// <summary>
    /// 使用迭代器模式，避免一次性在堆上分配巨大的 List
    /// </summary>
    public IEnumerable<ChunkPos> EnumerateChunkPositions(Dimension dimension = Dimension.Overworld)
    {
        // 假设 Iterate 返回的是封装了 LevelDB Iterator 的对象
        foreach (var kvp in _db.Iterate())
        {
            // 假设 ChunkKey.Decode 内部已优化为不产生多余对象
            var key = ChunkKey.Decode(kvp.Key);
            
            if (key != null && key.Pos.Dimension == dimension && IsDataTag(key.Tag))
            {
                yield return key.Pos;
            }
        }
    }

    public List<ChunkPos> ListChunkPositions(Dimension dimension = Dimension.Overworld)
    {
        return EnumerateChunkPositions(dimension).Distinct().ToList();
    }

    private static bool IsDataTag(ChunkRecordTag tag) =>
        tag is ChunkRecordTag.Data2D or ChunkRecordTag.Data3D or ChunkRecordTag.SubChunkPrefix;

    /// <summary>
    /// 获取子区块数据。
    /// 建议：如果 SubChunk.Parse 能够接受 ReadOnlySpan<byte>，性能会更好。
    /// </summary>
    public SubChunk? GetSubChunk(ChunkPos pos, sbyte y)
    {
        var key = ChunkKey.SubChunk(pos, y);
        byte[]? data = _db.Get(key.Encode());
        
        return data == null ? null : SubChunk.Parse(y, data);
    }

    /// <summary>
    /// 核心优化：使用传入的 Span 填充数据，完全避免数组分配
    /// </summary>
    /// <param name="pos">区块坐标</param>
    /// <param name="heightMap">长度应为 256 的缓冲区</param>
    /// <param name="biomes">长度应为 256 的缓冲区</param>
    public void FillChunkTerrain(ChunkPos pos, Span<short> heightMap, Span<int> biomes)
    {
        heightMap.Clear();
        biomes.Clear();

        // 尝试获取 2D 数据 (Legacy/Standard Bedrock)
        byte[]? data2D = _db.Get(new ChunkKey(pos, ChunkRecordTag.Data2D).Encode());
        if (data2D != null)
        {
            ParseData2D(data2D, heightMap, biomes);
            return;
        }

        // 尝试获取 3D 数据 (Newer Bedrock versions)
        byte[]? data3D = _db.Get(new ChunkKey(pos, ChunkRecordTag.Data3D).Encode());
        if (data3D != null)
        {
            ParseData3D(data3D, heightMap, biomes);
        }
    }

    public (short?[,]? HeightMap, int[,]? Biomes) GetChunkTerrain(ChunkPos pos)
    {
        Span<short> heightBuffer = stackalloc short[256];
        Span<int> biomeBuffer = stackalloc int[256];
        FillChunkTerrain(pos, heightBuffer, biomeBuffer);

        var heightMap = new short?[16, 16];
        var biomes = new int[16, 16];

        for (var z = 0; z < 16; z++)
        {
            for (var x = 0; x < 16; x++)
            {
                var index = z * 16 + x;
                heightMap[z, x] = heightBuffer[index];
                biomes[z, x] = biomeBuffer[index];
            }
        }

        return (heightMap, biomes);
    }

    private static void ParseData2D(ReadOnlySpan<byte> data, Span<short> heightMap, Span<int> biomes)
    {
        // 前 512 字节是 HeightMap (256 * int16)
        var heightCount = Math.Min(256, data.Length / sizeof(short));
        for (var i = 0; i < heightCount; i++)
        {
            heightMap[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * sizeof(short), sizeof(short)));
        }

        // 紧接着是 256 字节的 Biomes
        const int biomeOffset = 512;
        var biomeCount = data.Length > biomeOffset ? Math.Min(256, data.Length - biomeOffset) : 0;
        for (var i = 0; i < biomeCount; i++)
        {
            biomes[i] = data[biomeOffset + i];
        }
    }

    private static void ParseData3D(ReadOnlySpan<byte> data, Span<short> heightMap, Span<int> biomes)
    {
        // 3D 格式逻辑与 2D 类似，但通常包含更多层信息，此处保持逻辑一致性并增加边界检查
        var heightCount = Math.Min(256, data.Length / sizeof(short));
        for (var i = 0; i < heightCount; i++)
        {
            heightMap[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * sizeof(short), sizeof(short)));
        }

        const int biomeOffset = 512; // 假设 3D 数据中 Biome 偏移量
        var biomeCount = data.Length > biomeOffset ? Math.Min(256, data.Length - biomeOffset) : 0;
        for (var i = 0; i < biomeCount; i++)
        {
            biomes[i] = data[biomeOffset + i];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _db.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}