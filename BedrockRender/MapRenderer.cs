using BedrockWorld.Chunk;
using BedrockRender.Palette;
using BedrockRender.Gpu;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Buffers;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace BedrockRender;

public enum RenderMode
{
    HeightMap,
    SurfaceBlocks,
    Biome,
    LayerBlocks,
    CaveSlice
}

public enum RenderEngine
{
    Auto,
    Cpu,
    Gpu
}

public class MapRenderer : IDisposable
{
    private readonly BedrockWorld.BedrockWorld _world;
    private readonly RenderPalette _palette;
    private readonly GpuRenderEngine? _gpuEngine;
    private RenderEngine _currentEngine;
    private bool _disposed;

    private const int MaxChunkCacheSize = 64;
    private const int ChunkBatchSize = 8;

    public static RenderEngine DefaultEngine { get; private set; } = RenderEngine.Auto;

    public static bool IsGpuAvailable => GpuCapabilities.IsGpuAvailable();

    public RenderEngine CurrentEngine => _currentEngine;

    public MapRenderer(BedrockWorld.BedrockWorld world, RenderPalette palette, RenderEngine preferredEngine = RenderEngine.Auto)
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

    public Image<Rgba32> RenderHeightMap(Dimension dimension, int minChunkX, int minChunkZ, int maxChunkX,
        int maxChunkZ, short minHeight = -64, short maxHeight = 320)
    {
        var width = (maxChunkX - minChunkX + 1) * 16;
        var height = (maxChunkZ - minChunkZ + 1) * 16;

        var pixelData = ArrayPool<uint>.Shared.Rent(width * height);
        try
        {
            Parallel.For(minChunkZ, maxChunkZ + 1, chunkZ =>
            {
                for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
                {
                    ProcessHeightMapChunk(chunkX, chunkZ, dimension, minChunkX, minChunkZ, width, minHeight, maxHeight, pixelData);
                }
            });

            var image = new Image<Rgba32>(width, height);
            CopyPixelsToImage(image, pixelData, width, height);
            return image;
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(pixelData);
        }
    }

    private void ProcessHeightMapChunk(int chunkX, int chunkZ, Dimension dimension, int minChunkX, int minChunkZ,
        int width, short minHeight, short maxHeight, uint[] pixelData)
    {
        var pos = new ChunkPos(chunkX, chunkZ, dimension);
        var (heightMap, _) = _world.GetChunkTerrain(pos);

        var offsetX = (chunkX - minChunkX) * 16;
        var offsetZ = (chunkZ - minChunkZ) * 16;

        for (var z = 0; z < 16; z++)
        {
            for (var x = 0; x < 16; x++)
            {
                var imgX = offsetX + x;
                var imgZ = offsetZ + z;
                var h = heightMap?[z, x] ?? (short)0;
                var color = _palette.HeightColor(h, minHeight, maxHeight);
                pixelData[imgZ * width + imgX] = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            }
        }
    }

    public Image<Rgba32> RenderBiome(Dimension dimension, int minChunkX, int minChunkZ, int maxChunkX, int maxChunkZ,
        int layerY = 64, bool rawBiome = false)
    {
        var width = (maxChunkX - minChunkX + 1) * 16;
        var height = (maxChunkZ - minChunkZ + 1) * 16;

        var biomeIds = ArrayPool<int>.Shared.Rent(width * height);
        var pixelData = ArrayPool<uint>.Shared.Rent(width * height);

        try
        {
            Parallel.For(minChunkZ, maxChunkZ + 1, chunkZ =>
            {
                for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
                {
                    ProcessBiomeChunk(chunkX, chunkZ, dimension, minChunkX, minChunkZ, width, biomeIds);
                }
            });

            Parallel.For(0, height, z =>
            {
                for (var x = 0; x < width; x++)
                {
                    var biomeId = biomeIds[z * width + x];
                    int colorValue;

                    if (rawBiome)
                    {
                        colorValue = biomeId;
                    }
                    else
                    {
                        var biomeColor = _palette.BiomeColor(biomeId);
                        colorValue = (biomeColor.R << 16) | (biomeColor.G << 8) | biomeColor.B;
                    }

                    var r = (byte)((colorValue >> 16) & 0xff);
                    var g = (byte)((colorValue >> 8) & 0xff);
                    var b = (byte)(colorValue & 0xff);

                    pixelData[z * width + x] = (uint)((255 << 24) | (r << 16) | (g << 8) | b);
                }
            });

            var image = new Image<Rgba32>(width, height);
            CopyPixelsToImage(image, pixelData, width, height);
            return image;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(biomeIds);
            ArrayPool<uint>.Shared.Return(pixelData);
        }
    }

    private void ProcessBiomeChunk(int chunkX, int chunkZ, Dimension dimension, int minChunkX, int minChunkZ,
        int width, int[] biomeIds)
    {
        var pos = new ChunkPos(chunkX, chunkZ, dimension);
        var (_, biomes) = _world.GetChunkTerrain(pos);

        var offsetX = (chunkX - minChunkX) * 16;
        var offsetZ = (chunkZ - minChunkZ) * 16;

        for (var z = 0; z < 16; z++)
        {
            for (var x = 0; x < 16; x++)
            {
                var imgX = offsetX + x;
                var imgZ = offsetZ + z;
                biomeIds[imgZ * width + imgX] = biomes?[z, x] ?? 0;
            }
        }
    }

    public Image<Rgba32> RenderSurfaceBlocks(Dimension dimension, int minChunkX, int minChunkZ, int maxChunkX,
        int maxChunkZ)
    {
        var width = (maxChunkX - minChunkX + 1) * 16;
        var height = (maxChunkZ - minChunkZ + 1) * 16;

        var blockColors = ArrayPool<uint>.Shared.Rent(width * height);
        var heightMap = ArrayPool<int>.Shared.Rent(width * height);

        try
        {
            ProcessSurfaceBlocksData(dimension, minChunkX, minChunkZ, maxChunkX, maxChunkZ, width, height, blockColors, heightMap);

            if (_currentEngine == RenderEngine.Gpu && _gpuEngine != null)
            {
                return _gpuEngine.RenderSurfaceBlocksGpu(blockColors, heightMap, width, height);
            }

            return RenderSurfaceBlocksCpu(blockColors, heightMap, width, height);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(blockColors);
            ArrayPool<int>.Shared.Return(heightMap);
        }
    }

    private void ProcessSurfaceBlocksData(Dimension dimension, int minChunkX, int minChunkZ, int maxChunkX, int maxChunkZ,
        int width, int height, uint[] blockColors, int[] heightMap)
    {
        var chunkCountX = maxChunkX - minChunkX + 1;
        var chunkCountZ = maxChunkZ - minChunkZ + 1;
        var totalChunks = chunkCountX * chunkCountZ;

        var processedChunks = 0;

        while (processedChunks < totalChunks)
        {
            var batchSize = Math.Min(ChunkBatchSize, totalChunks - processedChunks);
            var tasks = new Task[batchSize];

            for (var i = 0; i < batchSize; i++)
            {
                var idx = processedChunks + i;
                var cz = idx / chunkCountX + minChunkZ;
                var cx = idx % chunkCountX + minChunkX;
                tasks[i] = Task.Run(() => ProcessSurfaceChunk(cx, cz, dimension, minChunkX, minChunkZ, width, blockColors, heightMap));
            }

            Task.WaitAll(tasks);
            processedChunks += batchSize;
        }
    }

    private void ProcessSurfaceChunk(int cx, int cz, Dimension dimension, int minChunkX, int minChunkZ,
        int width, uint[] blockColors, int[] heightMap)
    {
        var pos = new ChunkPos(cx, cz, dimension);
        var (chunkHeightMap, biomes) = _world.GetChunkTerrain(pos);

        var offsetX = (cx - minChunkX) * 16;
        var offsetZ = (cz - minChunkZ) * 16;

        var localCache = new Dictionary<(ChunkPos, sbyte), SubChunk?>();

        for (var z = 0; z < 16; z++)
        {
            for (var x = 0; x < 16; x++)
            {
                var imgX = offsetX + x;
                var imgZ = offsetZ + z;
                var h = chunkHeightMap?[z, x] ?? -64;
                var biomeId = biomes?[z, x] ?? 0;

                heightMap[imgZ * width + imgX] = h;

                var color = GetBlockColorWithWater(pos, x, z, (short)h, biomeId, localCache);
                blockColors[imgZ * width + imgX] = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            }
        }

        localCache.Clear();
    }

    private Image<Rgba32> RenderSurfaceBlocksCpu(uint[] blockColors, int[] heightMap, int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        var pixels = ArrayPool<uint>.Shared.Rent(width * height);

        try
        {
            Parallel.For(0, height, z =>
            {
                for (var x = 0; x < width; x++)
                {
                    var idx = z * width + x;
                    var colorUint = blockColors[idx];
                    var h = heightMap[idx];

                    var r = (byte)((colorUint >> 16) & 0xff);
                    var g = (byte)((colorUint >> 8) & 0xff);
                    var b = (byte)(colorUint & 0xff);
                    var a = (byte)((colorUint >> 24) & 0xff);

                    if (a == 0)
                    {
                        pixels[idx] = 0;
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

                    pixels[idx] = (uint)((255 << 24) | (finalR << 16) | (finalG << 8) | finalB);
                }
            });

            CopyPixelsToImage(image, pixels, width, height);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(pixels);
        }

        return image;
    }

    private RgbaColor GetBlockColorWithWater(ChunkPos pos, int localX, int localZ, short height, int biomeId,
        Dictionary<(ChunkPos, sbyte), SubChunk?> subChunksCache)
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

    public Image<Rgba32> RenderLayerBlocks(Dimension dimension, int minChunkX, int minChunkZ, int maxChunkX,
        int maxChunkZ, int layerY)
    {
        var width = (maxChunkX - minChunkX + 1) * 16;
        var height = (maxChunkZ - minChunkZ + 1) * 16;

        var pixelData = ArrayPool<uint>.Shared.Rent(width * height);

        try
        {
            Parallel.For(minChunkZ, maxChunkZ + 1, chunkZ =>
            {
                for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
                {
                    ProcessLayerChunk(chunkX, chunkZ, dimension, minChunkX, minChunkZ, width, layerY, pixelData);
                }
            });

            var image = new Image<Rgba32>(width, height);
            CopyPixelsToImage(image, pixelData, width, height);
            return image;
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(pixelData);
        }
    }

    private void ProcessLayerChunk(int chunkX, int chunkZ, Dimension dimension, int minChunkX, int minChunkZ,
        int width, int layerY, uint[] pixelData)
    {
        var pos = new ChunkPos(chunkX, chunkZ, dimension);
        var (_, biomes) = _world.GetChunkTerrain(pos);

        var offsetX = (chunkX - minChunkX) * 16;
        var offsetZ = (chunkZ - minChunkZ) * 16;

        var localCache = new Dictionary<(ChunkPos, sbyte), SubChunk?>();

        for (var z = 0; z < 16; z++)
        {
            for (var x = 0; x < 16; x++)
            {
                var biomeId = biomes?[z, x] ?? 0;
                var color = GetBlockAtLayer(pos, x, z, layerY, biomeId, localCache);
                pixelData[(offsetZ + z) * width + (offsetX + x)] = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            }
        }

        localCache.Clear();
    }

    public Image<Rgba32> RenderCaveSlice(Dimension dimension, int minChunkX, int minChunkZ, int maxChunkX,
        int maxChunkZ, int caveY)
    {
        return RenderLayerBlocks(dimension, minChunkX, minChunkZ, maxChunkX, maxChunkZ, caveY);
    }

    private RgbaColor GetBlockAtLayer(ChunkPos pos, int localX, int localZ, int y, int biomeId,
        Dictionary<(ChunkPos, sbyte), SubChunk?> subChunksCache)
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

    private void CopyPixelsToImage(Image<Rgba32> image, uint[] pixelData, int width, int height)
    {
        var frame = image.Frames.RootFrame;
        for (var i = 0; i < pixelData.Length && i < width * height; i++)
        {
            var p = pixelData[i];
            var y = i / width;
            var x = i % width;
            frame[x, y] = new Rgba32(
                (byte)((p >> 16) & 0xFF),
                (byte)((p >> 8) & 0xFF),
                (byte)(p & 0xFF),
                (byte)((p >> 24) & 0xFF)
            );
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _gpuEngine?.Dispose();
        _disposed = true;
    }
}