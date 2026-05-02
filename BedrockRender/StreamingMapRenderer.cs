using BedrockWorld;
using BedrockWorld.Chunk;
using BedrockRender.Palette;
using BedrockRender.Gpu;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;
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
    public uint[]? HeightMapData { get; set; }
    public uint[]? BiomeData { get; set; }
}

public class StreamingMapRenderer : IDisposable
{
    private readonly StreamingWorld _world;
    private readonly RenderPalette _palette;
    private readonly GpuRenderEngine? _gpuEngine;
    private RenderEngine _currentEngine;
    private bool _disposed;

    private const int ChunkGroupSize = 16;
    private const int MaxChunkCacheSize = 128;
    private const int ProgressUpdateInterval = 10;

    public static RenderEngine DefaultEngine { get; private set; } = RenderEngine.Auto;

    public static bool IsGpuAvailable => GpuCapabilities.IsGpuAvailable();

    public RenderEngine CurrentEngine => _currentEngine;

    public event Action<RenderProgress>? ProgressChanged;
    public event Action<ChunkRenderResult>? ChunkRendered;

    private CancellationTokenSource? _cts;
    private int[]? _globalHeightMap;
    private uint[]? _globalBlockColors;
    private int _globalWidth;
    private int _globalHeight;

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

    public async Task<Image<Rgba32>?> RenderAllChunksAsync(
        Dimension dimension,
        int minChunkX,
        int minChunkZ,
        int maxChunkX,
        int maxChunkZ,
        short minHeight = -64,
        short maxHeight = 320,
        int layerY = 64,
        RenderMode mode = RenderMode.SurfaceBlocks,
        CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        var width = (maxChunkX - minChunkX + 1) * 16;
        var height = (maxChunkZ - minChunkZ + 1) * 16;
        var image = new Image<Rgba32>(width, height);

        var totalWidth = maxChunkX - minChunkX + 1;
        var totalHeight = maxChunkZ - minChunkZ + 1;
        var totalChunks = totalWidth * totalHeight;
        var totalGroups = (totalWidth + ChunkGroupSize - 1) / ChunkGroupSize * (totalHeight + ChunkGroupSize - 1) / ChunkGroupSize;

        var renderedChunks = 0;
        var currentGroup = 0;

        var progress = new RenderProgress
        {
            TotalChunks = totalChunks,
            TotalGroups = totalGroups,
            RenderedChunks = 0
        };

        var groupTasks = new List<Task>();

        for (var groupX = 0; groupX < totalWidth; groupX += ChunkGroupSize)
        {
            for (var groupZ = 0; groupZ < totalHeight; groupZ += ChunkGroupSize)
            {
                currentGroup++;
                var groupMinX = minChunkX + groupX;
                var groupMinZ = minChunkZ + groupZ;
                var groupMaxX = Math.Min(groupMinX + ChunkGroupSize - 1, maxChunkX);
                var groupMaxZ = Math.Min(groupMinZ + ChunkGroupSize - 1, maxChunkZ);

                var task = Task.Run(() =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    for (var cz = groupMinZ; cz <= groupMaxZ; cz++)
                    {
                        for (var cx = groupMinX; cx <= groupMaxX; cx++)
                        {
                            if (token.IsCancellationRequested)
                                return;

                            var pos = new ChunkPos(cx, cz, dimension);
                            var result = mode switch
                            {
                                RenderMode.HeightMap => RenderHeightMapChunk(cx, cz, dimension, minChunkX, minChunkZ, width, minHeight, maxHeight),
                                RenderMode.SurfaceBlocks => RenderSurfaceBlocksChunk(cx, cz, dimension, minChunkX, minChunkZ, width),
                                RenderMode.Biome => RenderBiomeChunk(cx, cz, dimension, minChunkX, minChunkZ, width),
                                RenderMode.LayerBlocks => RenderLayerBlocksChunk(cx, cz, dimension, minChunkX, minChunkZ, width, layerY),
                                RenderMode.CaveSlice => RenderLayerBlocksChunk(cx, cz, dimension, minChunkX, minChunkZ, width, layerY),
                                _ => RenderSurfaceBlocksChunk(cx, cz, dimension, minChunkX, minChunkZ, width)
                            };

                            result.Position = pos;
                            result.MinChunkX = minChunkX;
                            result.MinChunkZ = minChunkZ;
                            result.Width = width;

                            ChunkRendered?.Invoke(result);

                            var count = Interlocked.Increment(ref renderedChunks);
                            progress.RenderedChunks = count;
                            progress.CurrentGroup = currentGroup;
                            progress.CurrentChunk = pos;
                            ProgressChanged?.Invoke(progress);
                        }
                    }
                }, token);

                groupTasks.Add(task);
            }
        }

        try
        {
            await Task.WhenAll(groupTasks);
        }
        catch (OperationCanceledException)
        {
            image.Dispose();
            return null;
        }

        progress.IsComplete = true;
        progress.RenderedChunks = totalChunks;
        ProgressChanged?.Invoke(progress);

        return image;
    }

    public async Task RenderChunksProgressiveAsync(
        Dimension dimension,
        int minChunkX,
        int minChunkZ,
        int maxChunkX,
        int maxChunkZ,
        short minHeight = -64,
        short maxHeight = 320,
        int layerY = 64,
        RenderMode mode = RenderMode.SurfaceBlocks,
        CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        _globalWidth = (maxChunkX - minChunkX + 1) * 16;
        _globalHeight = (maxChunkZ - minChunkZ + 1) * 16;
        _globalHeightMap = new int[_globalWidth * _globalHeight];
        _globalBlockColors = new uint[_globalWidth * _globalHeight];

        var totalWidth = maxChunkX - minChunkX + 1;
        var totalHeight = maxChunkZ - minChunkZ + 1;
        var totalChunks = totalWidth * totalHeight;

        var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 4);
        var renderedChunks = 0;
        var currentGroup = 0;
        var totalGroups = (totalWidth + ChunkGroupSize - 1) / ChunkGroupSize * (totalHeight + ChunkGroupSize - 1) / ChunkGroupSize;
        var lastProgressUpdate = 0;

        var progress = new RenderProgress
        {
            TotalChunks = totalChunks,
            TotalGroups = totalGroups
        };

        var tasks = new List<Task>();

        for (var groupX = 0; groupX < totalWidth; groupX += ChunkGroupSize)
        {
            for (var groupZ = 0; groupZ < totalHeight; groupZ += ChunkGroupSize)
            {
                currentGroup++;
                var groupMinX = minChunkX + groupX;
                var groupMinZ = minChunkZ + groupZ;
                var groupMaxX = Math.Min(groupMinX + ChunkGroupSize - 1, maxChunkX);
                var groupMaxZ = Math.Min(groupMinZ + ChunkGroupSize - 1, maxChunkZ);

                var task = Task.Run(async () =>
                {
                    await semaphore.WaitAsync(token);
                    try
                    {
                        if (token.IsCancellationRequested)
                            return;

                        for (var cz = groupMinZ; cz <= groupMaxZ; cz++)
                        {
                            for (var cx = groupMinX; cx <= groupMaxX; cx++)
                            {
                                if (token.IsCancellationRequested)
                                    return;

                                var pos = new ChunkPos(cx, cz, dimension);
                                var result = mode switch
                                {
                                    RenderMode.HeightMap => RenderHeightMapChunk(cx, cz, dimension, minChunkX, minChunkZ, _globalWidth, minHeight, maxHeight),
                                    RenderMode.SurfaceBlocks => RenderSurfaceBlocksChunk(cx, cz, dimension, minChunkX, minChunkZ, _globalWidth),
                                    RenderMode.Biome => RenderBiomeChunk(cx, cz, dimension, minChunkX, minChunkZ, _globalWidth),
                                    RenderMode.LayerBlocks => RenderLayerBlocksChunk(cx, cz, dimension, minChunkX, minChunkZ, _globalWidth, layerY),
                                    RenderMode.CaveSlice => RenderLayerBlocksChunk(cx, cz, dimension, minChunkX, minChunkZ, _globalWidth, layerY),
                                    _ => RenderSurfaceBlocksChunk(cx, cz, dimension, minChunkX, minChunkZ, _globalWidth)
                                };

                                result.Position = pos;
                                result.MinChunkX = minChunkX;
                                result.MinChunkZ = minChunkZ;
                                result.Width = _globalWidth;

                                UpdateGlobalData(result, minChunkX, minChunkZ, _globalWidth);
                                ChunkRendered?.Invoke(result);

                                var count = Interlocked.Increment(ref renderedChunks);
                                if (count - lastProgressUpdate >= ProgressUpdateInterval)
                                {
                                    progress.RenderedChunks = count;
                                    progress.CurrentGroup = currentGroup;
                                    progress.CurrentChunk = pos;
                                    ProgressChanged?.Invoke(progress);
                                    lastProgressUpdate = count;
                                }
                            }
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, token);

                tasks.Add(task);
            }
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }

        progress.IsComplete = true;
        progress.RenderedChunks = totalChunks;
        ProgressChanged?.Invoke(progress);
    }

    private void UpdateGlobalData(ChunkRenderResult result, int minChunkX, int minChunkZ, int width)
    {
        var offsetX = (result.Position.X - minChunkX) * 16;
        var offsetZ = (result.Position.Z - minChunkZ) * 16;

        lock (_globalHeightMap!)
        {
            for (var z = 0; z < 16; z++)
            {
                for (var x = 0; x < 16; x++)
                {
                    var idx = (offsetZ + z) * width + (offsetX + x);
                    _globalBlockColors![idx] = result.PixelData[z * 16 + x];
                    if (result.HeightMapData != null)
                    {
                        _globalHeightMap[idx] = (int)result.HeightMapData[z * 16 + x] - 64;
                    }
                }
            }
        }
    }

    public uint[] ApplyShadowsToPixelData(uint[] pixelData, int[] heightMap, int width, int height)
    {
        var result = new uint[pixelData.Length];

        Parallel.For(0, height, z =>
        {
            for (var x = 0; x < width; x++)
            {
                var idx = z * width + x;
                var colorUint = pixelData[idx];
                var h = heightMap[idx];

                var r = (byte)((colorUint >> 16) & 0xff);
                var g = (byte)((colorUint >> 8) & 0xff);
                var b = (byte)(colorUint & 0xff);
                var a = (byte)((colorUint >> 24) & 0xff);

                if (a == 0)
                {
                    result[idx] = 0;
                    continue;
                }

                float shadow = 1.0f;
                if (x > 0 && z > 0)
                {
                    float dx = heightMap[z * width + x] - heightMap[z * width + (x - 1)];
                    float dz = heightMap[z * width + x] - heightMap[(z - 1) * width + x];
                    shadow = 1.0f + (dx + dz) * 0.12f;
                }

                shadow = Math.Clamp(shadow, 0.7f, 1.3f);
                float heightFactor = Math.Clamp((h + 64) / 400f + 0.8f, 0.8f, 1.05f);
                float finalIntensity = shadow * heightFactor;

                byte finalR = (byte)Math.Clamp(r * finalIntensity, 0, 255);
                byte finalG = (byte)Math.Clamp(g * finalIntensity, 0, 255);
                byte finalB = (byte)Math.Clamp(b * finalIntensity, 0, 255);

                result[idx] = (uint)((255 << 24) | (finalR << 16) | (finalG << 8) | finalB);
            }
        });

        return result;
    }

    public int[]? GlobalHeightMap => _globalHeightMap;
    public uint[]? GlobalBlockColors => _globalBlockColors;
    public int GlobalWidth => _globalWidth;
    public int GlobalHeight => _globalHeight;

    private ChunkRenderResult RenderHeightMapChunk(int chunkX, int chunkZ, Dimension dimension, int minChunkX, int minChunkZ, int width, short minHeight, short maxHeight)
    {
        var pos = new ChunkPos(chunkX, chunkZ, dimension);
        var (heightMap, _) = _world.GetChunkTerrain(pos);

        var result = new ChunkRenderResult();
        result.PixelData = new uint[16 * 16];

        var offsetX = (chunkX - minChunkX) * 16;
        var offsetZ = (chunkZ - minChunkZ) * 16;

        for (var z = 0; z < 16; z++)
        {
            for (var x = 0; x < 16; x++)
            {
                var h = heightMap?[z, x] ?? (short)0;
                var color = _palette.HeightColor(h, minHeight, maxHeight);
                result.PixelData[z * 16 + x] = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            }
        }

        return result;
    }

    private ChunkRenderResult RenderSurfaceBlocksChunk(int chunkX, int chunkZ, Dimension dimension, int minChunkX, int minChunkZ, int width)
    {
        var pos = new ChunkPos(chunkX, chunkZ, dimension);
        var (chunkHeightMap, biomes) = _world.GetChunkTerrain(pos);

        var result = new ChunkRenderResult();
        result.PixelData = new uint[16 * 16];
        result.HeightMapData = new uint[16 * 16];

        var localCache = new Dictionary<(ChunkPos, sbyte), SubChunk?>();

        for (var z = 0; z < 16; z++)
        {
            for (var x = 0; x < 16; x++)
            {
                var h = chunkHeightMap?[z, x] ?? -64;
                var biomeId = biomes?[z, x] ?? 0;

                result.HeightMapData[z * 16 + x] = (uint)(h + 64);

                var color = GetBlockColorWithWater(pos, x, z, (short)h, biomeId, localCache);
                result.PixelData[z * 16 + x] = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            }
        }

        localCache.Clear();
        return result;
    }

    private ChunkRenderResult RenderBiomeChunk(int chunkX, int chunkZ, Dimension dimension, int minChunkX, int minChunkZ, int width)
    {
        var pos = new ChunkPos(chunkX, chunkZ, dimension);
        var (_, biomes) = _world.GetChunkTerrain(pos);

        var result = new ChunkRenderResult();
        result.PixelData = new uint[16 * 16];
        result.BiomeData = new uint[16 * 16];

        for (var z = 0; z < 16; z++)
        {
            for (var x = 0; x < 16; x++)
            {
                var biomeId = biomes?[z, x] ?? 0;
                result.BiomeData[z * 16 + x] = (uint)biomeId;

                var biomeColor = _palette.BiomeColor(biomeId);
                result.PixelData[z * 16 + x] = ((uint)biomeColor.A << 24) | ((uint)biomeColor.R << 16) | ((uint)biomeColor.G << 8) | biomeColor.B;
            }
        }

        return result;
    }

    private ChunkRenderResult RenderLayerBlocksChunk(int chunkX, int chunkZ, Dimension dimension, int minChunkX, int minChunkZ, int width, int layerY)
    {
        var pos = new ChunkPos(chunkX, chunkZ, dimension);
        var (_, biomes) = _world.GetChunkTerrain(pos);

        var result = new ChunkRenderResult();
        result.PixelData = new uint[16 * 16];

        var localCache = new Dictionary<(ChunkPos, sbyte), SubChunk?>();

        for (var z = 0; z < 16; z++)
        {
            for (var x = 0; x < 16; x++)
            {
                var biomeId = biomes?[z, x] ?? 0;
                var color = GetBlockAtLayer(pos, x, z, layerY, biomeId, localCache);
                result.PixelData[z * 16 + x] = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            }
        }

        localCache.Clear();
        return result;
    }

    private RgbaColor GetBlockColorWithWater(ChunkPos pos, int localX, int localZ, short height, int biomeId, Dictionary<(ChunkPos, sbyte), SubChunk?> subChunksCache)
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
                    255
                );
            }

            return solidColor;
        }

        return _palette.VoidColor;
    }

    private string GetBlockNameAt(ChunkPos pos, int x, int y, int z, Dictionary<(ChunkPos, sbyte), SubChunk?> cache)
    {
        sbyte subY = (sbyte)(y >> 4);
        int localY = y & 0xF;
        if (!cache.TryGetValue((pos, subY), out var subChunk))
        {
            subChunk = _world.GetSubChunk(pos, subY);
            cache[(pos, subY)] = subChunk;
        }

        return subChunk?.BlockStateAt(x, localY, z)?.Name ?? "minecraft:air";
    }

    private RgbaColor GetBlockAtLayer(ChunkPos pos, int localX, int localZ, int y, int biomeId, Dictionary<(ChunkPos, sbyte), SubChunk?> subChunksCache)
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

        var cacheKey = (pos, subChunkY);
        SubChunk? subChunk;
        if (!subChunksCache.TryGetValue(cacheKey, out subChunk))
        {
            subChunk = _world.GetSubChunk(pos, subChunkY);
            subChunksCache[cacheKey] = subChunk;
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
