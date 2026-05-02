using BedrockWorld;
using BedrockWorld.Chunk;
using BedrockRender.Palette;
using BedrockRender.Gpu;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Buffers;
using System.Threading;

namespace BedrockRender;

public class RenderProgress
{
    public int TotalChunks { get; set; }
    public int RenderedChunks { get; set; }
    public int CurrentGroup { get; set; }
    public int TotalGroups { get; set; }
    public ChunkPos? CurrentChunk { get; set; }
    public bool IsComplete { get; set; }
    public float ProgressPercent => TotalChunks > 0 ? (float)RenderedChunks / TotalChunks * 100 : 0;
}

public class ChunkRenderResult
{
    public ChunkPos Position { get; set; }
    public int MinChunkX { get; set; }
    public int MinChunkZ { get; set; }
    public int Width { get; set; }
    public uint[] PixelData { get; set; } = Array.Empty<uint>();
    /// <summary>标记 PixelData 是否来自 ArrayPool，供消费方归还</summary>
    public bool PixelDataFromPool { get; set; }
}

public class StreamingMapRenderer : IDisposable
{
    private readonly StreamingWorld _world;
    private readonly RenderPalette _palette;
    private readonly GpuRenderEngine? _gpuEngine;
    private RenderEngine _currentEngine;
    private bool _disposed;

    private const int ProgressUpdateInterval = 10;
    private const long ProgressUpdateIntervalTicks = TimeSpan.TicksPerMillisecond * 100;
    // 限制并发数为 CPU 核心数，避免过多线程切换和内存压力
    private static readonly int MaxParallelChunks = Math.Max(1, Environment.ProcessorCount);
    private static readonly int ChunkPixelCount = 16 * 16; // 256
    private static readonly int ShadedChunkWidth = 18;

    public static RenderEngine DefaultEngine { get; private set; } = RenderEngine.Auto;

    public static bool IsGpuAvailable => GpuCapabilities.IsGpuAvailable();

    public RenderEngine CurrentEngine => _currentEngine;

    public event Action<RenderProgress>? ProgressChanged;
    public event Action<ChunkRenderResult>? ChunkRendered;

    private CancellationTokenSource? _cts;

    public StreamingMapRenderer(StreamingWorld world, RenderPalette palette, RenderEngine preferredEngine = RenderEngine.Auto)
    {
        _world = world;
        _palette = palette;

        if (preferredEngine == RenderEngine.Auto)
        {
            DefaultEngine = GpuCapabilities.DetectBestEngine() == RenderEngineType.Gpu ? RenderEngine.Gpu : RenderEngine.Cpu;
        }
        else
        {
            DefaultEngine = preferredEngine;
        }

        _currentEngine = DefaultEngine;

        if (_currentEngine == RenderEngine.Gpu || preferredEngine == RenderEngine.Gpu)
        {
            _gpuEngine = new GpuRenderEngine();
            if (!_gpuEngine.IsGpuEnabled)
            {
                _gpuEngine.Dispose();
                _gpuEngine = null;
                _currentEngine = RenderEngine.Cpu;
            }
        }
    }

    public void SetEngine(RenderEngine engine)
    {
        if (engine == _currentEngine)
            return;

        if (engine == RenderEngine.Gpu)
        {
            if (_gpuEngine == null || !_gpuEngine.IsGpuEnabled)
            {
                throw new InvalidOperationException("GPU engine is not available");
            }
            _currentEngine = RenderEngine.Gpu;
        }
        else
        {
            _currentEngine = RenderEngine.Cpu;
        }
    }

    public void CancelCurrentRender()
    {
        _cts?.Cancel();
    }

    public async Task RenderChunksProgressiveAsync(
        Dimension dimension,
        int minChunkX,
        int minChunkZ,
        int maxChunkX,
        int maxChunkZ,
        List<ChunkPos>? chunksToRender = null,
        short minHeight = -64,
        short maxHeight = 320,
        int layerY = 64,
        RenderMode mode = RenderMode.SurfaceBlocks,
        CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        List<ChunkPos> chunks;
        var targetWidth = maxChunkX - minChunkX + 1;
        var targetHeight = maxChunkZ - minChunkZ + 1;

        if (chunksToRender == null)
        {
            chunks = new List<ChunkPos>();
            var allChunks = _world.ListChunkPositions(dimension);
            foreach (var c in allChunks)
            {
                if (c.X >= minChunkX && c.X <= maxChunkX && c.Z >= minChunkZ && c.Z <= maxChunkZ)
                {
                    chunks.Add(c);
                }
            }
        }
        else
        {
            chunks = chunksToRender;
        }

        var totalChunks = chunks.Count;
        var renderedCount = 0;
        var lastProgressUpdate = 0;
        var lastProgressTicks = DateTime.UtcNow.Ticks;

        var nextIndex = -1;
        var workerCount = Math.Min(MaxParallelChunks, totalChunks);
        var tasks = new Task[workerCount];

        for (var worker = 0; worker < workerCount; worker++)
        {
            tasks[worker] = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    var chunkIndex = Interlocked.Increment(ref nextIndex);
                    if (chunkIndex >= totalChunks)
                        break;

                    var chunkPos = chunks[chunkIndex];
                    var result = RenderSingleChunk(chunkPos, minChunkX, minChunkZ, targetWidth * 16, mode, layerY, minHeight, maxHeight);

                    ChunkRendered?.Invoke(result);

                    var newCount = Interlocked.Increment(ref renderedCount);
                    var nowTicks = DateTime.UtcNow.Ticks;
                    var previousTicks = Volatile.Read(ref lastProgressTicks);
                    var shouldReport = newCount == totalChunks ||
                        (newCount - Volatile.Read(ref lastProgressUpdate) >= ProgressUpdateInterval &&
                         nowTicks - previousTicks >= ProgressUpdateIntervalTicks &&
                         Interlocked.CompareExchange(ref lastProgressTicks, nowTicks, previousTicks) == previousTicks);

                    if (shouldReport)
                    {
                        Volatile.Write(ref lastProgressUpdate, newCount);
                        ProgressChanged?.Invoke(new RenderProgress
                        {
                            TotalChunks = totalChunks,
                            RenderedChunks = newCount,
                            IsComplete = newCount == totalChunks
                        });
                    }
                }
            }, token);
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private ChunkRenderResult RenderSingleChunk(
        ChunkPos chunkPos,
        int minChunkX,
        int minChunkZ,
        int totalWidth,
        RenderMode mode,
        int layerY,
        short minHeight,
        short maxHeight)
    {
        // 使用 ArrayPool 避免每块 new uint[256] 带来的 GC 压力
        var pixelData = ArrayPool<uint>.Shared.Rent(ChunkPixelCount);

        var result = new ChunkRenderResult
        {
            Position = chunkPos,
            MinChunkX = minChunkX,
            MinChunkZ = minChunkZ,
            Width = totalWidth,
            PixelData = pixelData,
            PixelDataFromPool = true
        };

        var terrainData = _world.GetChunkTerrain(chunkPos);
        var heightMap = terrainData.Item1;
        var biomeData = terrainData.Item2;
        int[]? shadedHeightMap = mode == RenderMode.SurfaceBlocks ? BuildShadedHeightMap(chunkPos, heightMap) : null;

        // 缓存 sbyte 键以避免 tuple 装筱
        var localCache = new Dictionary<sbyte, SubChunk?>(24);

        for (var z = 0; z < 16; z++)
        {
            for (var x = 0; x < 16; x++)
            {
                switch (mode)
                {
                    case RenderMode.SurfaceBlocks:
                        {
                            var y = heightMap?[z, x] ?? 0;
                            var color = GetBlockColorWithWater(chunkPos, x, z, y, biomeData?[z, x] ?? 0, localCache);
                            var colorUint = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
                            pixelData[z * 16 + x] = ApplyDropShadow(colorUint, y, shadedHeightMap!, x + 1, z + 1);
                            break;
                        }
                    case RenderMode.HeightMap:
                        {
                            var y = heightMap?[z, x] ?? 0;
                            var color = _palette.HeightColor(y, minHeight, maxHeight);
                            pixelData[z * 16 + x] = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
                            break;
                        }
                    case RenderMode.Biome:
                        {
                            var color = _palette.BiomeColor(biomeData?[z, x] ?? 0);
                            pixelData[z * 16 + x] = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
                            break;
                        }
                    case RenderMode.LayerBlocks:
                    case RenderMode.CaveSlice:
                        {
                            var color = GetBlockAtLayer(chunkPos, x, z, layerY, biomeData?[z, x] ?? 0, localCache);
                            pixelData[z * 16 + x] = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
                            break;
                        }
                }
            }
        }

        return result;
    }

    private int[] BuildShadedHeightMap(ChunkPos chunkPos, short?[,]? centerHeightMap)
    {
        var shadedHeightMap = new int[ShadedChunkWidth * ShadedChunkWidth];
        var terrainCache = new Dictionary<ChunkPos, short?[,]?>
        {
            [chunkPos] = centerHeightMap
        };

        for (var z = -1; z <= 16; z++)
        {
            for (var x = -1; x <= 16; x++)
            {
                shadedHeightMap[(z + 1) * ShadedChunkWidth + (x + 1)] = GetTerrainHeightAt(chunkPos, x, z, terrainCache);
            }
        }

        return shadedHeightMap;
    }

    private int GetTerrainHeightAt(ChunkPos chunkPos, int localX, int localZ, Dictionary<ChunkPos, short?[,]?> terrainCache)
    {
        var chunkX = chunkPos.X;
        var chunkZ = chunkPos.Z;
        while (localX < 0)
        {
            chunkX--;
            localX += 16;
        }
        while (localX >= 16)
        {
            chunkX++;
            localX -= 16;
        }
        while (localZ < 0)
        {
            chunkZ--;
            localZ += 16;
        }
        while (localZ >= 16)
        {
            chunkZ++;
            localZ -= 16;
        }

        var pos = new ChunkPos(chunkX, chunkZ, chunkPos.Dimension);
        if (!terrainCache.TryGetValue(pos, out var heightMap))
        {
            heightMap = _world.GetChunkTerrain(pos).HeightMap;
            terrainCache[pos] = heightMap;
        }

        return heightMap?[localZ, localX] ?? -64;
    }

    private static uint ApplyDropShadow(uint colorUint, int height, int[] shadedHeightMap, int x, int z)
    {
        var a = (byte)((colorUint >> 24) & 0xff);
        if (a == 0)
            return 0;

        var r = (byte)((colorUint >> 16) & 0xff);
        var g = (byte)((colorUint >> 8) & 0xff);
        var b = (byte)(colorUint & 0xff);

        var shadow = MapRenderer.CalculateDropShadow(shadedHeightMap, ShadedChunkWidth, ShadedChunkWidth, x, z);
        var heightFactor = Math.Clamp((height + 64) / 400f + 0.8f, 0.8f, 1.05f);
        var finalIntensity = shadow * heightFactor;

        var finalR = (byte)Math.Clamp(r * finalIntensity, 0, 255);
        var finalG = (byte)Math.Clamp(g * finalIntensity, 0, 255);
        var finalB = (byte)Math.Clamp(b * finalIntensity, 0, 255);

        return (uint)((255 << 24) | (finalR << 16) | (finalG << 8) | finalB);
    }

    private RgbaColor GetBlockColorWithWater(ChunkPos pos, int localX, int localZ, short height, int biomeId, Dictionary<sbyte, SubChunk?> subChunksCache)
    {
        if (height < -64) return _palette.VoidColor;

        bool foundWater = false;
        int waterSurfaceY = 0;
        RgbaColor waterColor = new RgbaColor(44, 88, 178, 255);

        for (var y = (int)height; y >= -64; y--)
        {
            var blockName = GetBlockNameAt(pos, localX, y, localZ, subChunksCache);

            if (_palette.IsAirBlock(blockName)) continue;

            if (blockName == "minecraft:water" || blockName == "minecraft:flowing_water")
            {
                if (!foundWater)
                {
                    foundWater = true;
                    waterSurfaceY = y;
                    waterColor = _palette.SurfaceBlockColor(blockName, biomeId, true);
                }
                continue;
            }

            var solidColor = _palette.SurfaceBlockColor(blockName, biomeId, true);

            if (foundWater)
            {
                int depth = waterSurfaceY - y;
                float blend = Math.Clamp(depth / 15f, 0.2f, 0.8f);

                return new RgbaColor(
                    (byte)(solidColor.R * (1 - blend) + waterColor.R * blend),
                    (byte)(solidColor.G * (1 - blend) + waterColor.G * blend),
                    (byte)(solidColor.B * (1 - blend) + waterColor.B * blend),
                    255);
            }

            return solidColor;
        }

        return _palette.VoidColor;
    }

    private string GetBlockNameAt(ChunkPos pos, int x, int y, int z, Dictionary<sbyte, SubChunk?> cache)
    {
        sbyte subY = (sbyte)(y >> 4);
        int localY = y & 0xF;
        if (!cache.TryGetValue(subY, out var subChunk))
        {
            subChunk = _world.GetSubChunk(pos, subY);
            cache[subY] = subChunk;
        }

        return subChunk?.BlockStateAt(x, localY, z)?.Name ?? "minecraft:air";
    }

    private RgbaColor GetBlockAtLayer(ChunkPos pos, int localX, int localZ, int y, int biomeId, Dictionary<sbyte, SubChunk?> subChunksCache)
    {
        if (y < -64 || y >= 320)
            return _palette.VoidColor;

        var subChunkY = (sbyte)(y / 16);
        var localY = y % 16;
        if (localY < 0)
        {
            localY += 16;
            subChunkY--;
        }

        SubChunk? subChunk;
        if (!subChunksCache.TryGetValue(subChunkY, out subChunk))
        {
            subChunk = _world.GetSubChunk(pos, subChunkY);
            subChunksCache[subChunkY] = subChunk;
        }

        if (subChunk == null)
            return _palette.VoidColor;

        var blockState = subChunk.BlockStateAt(localX, localY, localZ);
        if (blockState == null)
            return _palette.VoidColor;

        var blockName = blockState.Name;

        if (_palette.IsAirBlock(blockName))
            return _palette.VoidColor;

        return _palette.SurfaceBlockColor(blockName, biomeId, true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _cts?.Cancel();
        _cts?.Dispose();
        _gpuEngine?.Dispose();
        _disposed = true;
    }
}
