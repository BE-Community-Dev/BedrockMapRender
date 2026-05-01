using BedrockWorld.Chunk;
using BedrockRender.Palette;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BedrockRender;

public enum RenderMode
{
    HeightMap,
    SurfaceBlocks,
    Biome,
    LayerBlocks,
    CaveSlice
}

public class MapRenderer
{
    private readonly BedrockWorld.BedrockWorld _world;
    private readonly RenderPalette _palette;

    public MapRenderer(BedrockWorld.BedrockWorld world, RenderPalette palette)
    {
        _world = world;
        _palette = palette;
    }

    public Image<Rgba32> RenderHeightMap(Dimension dimension, int minChunkX, int minChunkZ, int maxChunkX,
        int maxChunkZ, short minHeight = -64, short maxHeight = 320)
    {
        var width = (maxChunkX - minChunkX + 1) * 16;
        var height = (maxChunkZ - minChunkZ + 1) * 16;
        var image = new Image<Rgba32>(width, height);

        for (var chunkZ = minChunkZ; chunkZ <= maxChunkZ; chunkZ++)
        {
            for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
            {
                var pos = new ChunkPos(chunkX, chunkZ, dimension);
                var (heightMap, _) = _world.GetChunkTerrain(pos);

                var offsetX = (chunkX - minChunkX) * 16;
                var offsetZ = (chunkZ - minChunkZ) * 16;

                for (var z = 0; z < 16; z++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        var h = heightMap?[z, x] ?? (short)0;
                        var color = _palette.HeightColor(h, minHeight, maxHeight);
                        image[offsetX + x, offsetZ + z] = new Rgba32(color.R, color.G, color.B, color.A);
                    }
                }
            }
        }

        return image;
    }

    public Image<Rgba32> RenderBiome(Dimension dimension, int minChunkX, int minChunkZ, int maxChunkX, int maxChunkZ,
        int layerY = 64, bool rawBiome = false)
    {
        var width = (maxChunkX - minChunkX + 1) * 16;
        var height = (maxChunkZ - minChunkZ + 1) * 16;
        var image = new Image<Rgba32>(width, height);

        for (var chunkZ = minChunkZ; chunkZ <= maxChunkZ; chunkZ++)
        {
            for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
            {
                var pos = new ChunkPos(chunkX, chunkZ, dimension);
                var (_, biomes) = _world.GetChunkTerrain(pos);

                var offsetX = (chunkX - minChunkX) * 16;
                var offsetZ = (chunkZ - minChunkZ) * 16;

                for (var z = 0; z < 16; z++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        int colorValue;
                        if (rawBiome)
                        {
                            colorValue = biomes?[z, x] ?? 0;
                        }
                        else
                        {
                            var biomeId = biomes?[z, x] ?? 0;
                            var biomeColor = _palette.BiomeColor(biomeId);
                            colorValue = (biomeColor.R << 16) | (biomeColor.G << 8) | biomeColor.B;
                        }

                        var r = (byte)((colorValue >> 16) & 0xff);
                        var g = (byte)((colorValue >> 8) & 0xff);
                        var b = (byte)(colorValue & 0xff);

                        image[offsetX + x, offsetZ + z] = new Rgba32(r, g, b, 255);
                    }
                }
            }
        }

        return image;
    }

    public Image<Rgba32> RenderSurfaceBlocks(Dimension dimension, int minChunkX, int minChunkZ, int maxChunkX,
        int maxChunkZ)
    {
        var width = (maxChunkX - minChunkX + 1) * 16;
        var height = (maxChunkZ - minChunkZ + 1) * 16;
        var image = new Image<Rgba32>(width, height);

        var subChunksCache = new Dictionary<(ChunkPos, sbyte), SubChunk?>();
        var heightMap = new short[width, height];
        var baseColors = new RgbaColor[width, height];

        // --- 第一阶段：基础采样 ---
        for (var cz = minChunkZ; cz <= maxChunkZ; cz++)
        {
            for (var cx = minChunkX; cx <= maxChunkX; cx++)
            {
                var pos = new ChunkPos(cx, cz, dimension);
                var (chunkHeightMap, biomes) = _world.GetChunkTerrain(pos);

                int offsetX = (cx - minChunkX) * 16;
                int offsetZ = (cz - minChunkZ) * 16;

                for (var z = 0; z < 16; z++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        int imgX = offsetX + x;
                        int imgZ = offsetZ + z;

                        short h = chunkHeightMap?[z, x] ?? -64;
                        int biomeId = biomes?[z, x] ?? 0;

                        heightMap[imgX, imgZ] = h;
                        baseColors[imgX, imgZ] = GetBlockColorWithWater(pos, x, z, h, biomeId, subChunksCache);
                    }
                }
            }
        }

        // --- 第二阶段：Hillshading 3D 效果应用 ---
        for (var z = 0; z < height; z++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = baseColors[x, z];
                if (color.A == 0) continue;

                // 计算光照强度 (模拟太阳在左上方)
                // 我们比较当前像素与左侧和上方像素的高度差
                float shadow = 1.0f;
                if (x > 0 && z > 0)
                {
                    // dx, dz 是坡度。值越大说明向光面，值越小说明背光面
                    float dx = heightMap[x, z] - heightMap[x - 1, z];
                    float dz = heightMap[x, z] - heightMap[x, z - 1];

                    // 模拟 45 度角的阳光
                    // 增加系数 0.15f 可以让阴影更深
                    shadow = 1.0f + (dx + dz) * 0.12f;
                }

                // 限制阴影范围，防止纯黑或纯白
                shadow = Math.Clamp(shadow, 0.7f, 1.3f);

                // 可选：根据海拔高度轻微着色（模拟大气厚度或高度感）
                float heightFactor = Math.Clamp((heightMap[x, z] + 64) / 400f + 0.8f, 0.8f, 1.05f);
                float finalIntensity = shadow * heightFactor;

                image[x, z] = new Rgba32(
                    (byte)Math.Clamp(color.R * finalIntensity, 0, 255),
                    (byte)Math.Clamp(color.G * finalIntensity, 0, 255),
                    (byte)Math.Clamp(color.B * finalIntensity, 0, 255),
                    255
                );
            }
        }

        return image;
    }

    private RgbaColor GetBlockColorWithWater(ChunkPos pos, int localX, int localZ, short height, int biomeId,
        Dictionary<(ChunkPos, sbyte), SubChunk?> subChunksCache)
    {
        if (height < -64) return _palette.VoidColor;

        bool foundWater = false;
        int waterSurfaceY = 0;
        RgbaColor waterColor = new RgbaColor(44, 88, 178, 255); // 默认水色，实际应从 palette 获取

        for (var y = (int)height; y >= -64; y--)
        {
            var blockName = GetBlockNameAt(pos, localX, y, localZ, subChunksCache);

            if (_palette.IsAirBlock(blockName)) continue;

            // 处理水体
            if (blockName == "minecraft:water" || blockName == "minecraft:flowing_water")
            {
                if (!foundWater)
                {
                    foundWater = true;
                    waterSurfaceY = y;
                    waterColor = _palette.SurfaceBlockColor(blockName, biomeId, true);
                }

                // 继续向下探测水底
                continue;
            }

            // 探测到固体（水底或陆地）
            var solidColor = _palette.SurfaceBlockColor(blockName, biomeId, true);

            if (foundWater)
            {
                // 计算深度：深度越大，水色占比越高
                int depth = waterSurfaceY - y;
                // 简单的线性混合 (lerp)
                // 深度 0-15 格之间变化，超过 15 格基本看不见底
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

// 辅助方法：获取指定高度的方块名
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
        var image = new Image<Rgba32>(width, height);

        var subChunksCache = new Dictionary<(ChunkPos, sbyte), SubChunk?>();

        for (var chunkZ = minChunkZ; chunkZ <= maxChunkZ; chunkZ++)
        {
            for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
            {
                var pos = new ChunkPos(chunkX, chunkZ, dimension);
                var (_, biomes) = _world.GetChunkTerrain(pos);

                var offsetX = (chunkX - minChunkX) * 16;
                var offsetZ = (chunkZ - minChunkZ) * 16;

                for (var z = 0; z < 16; z++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        var biomeId = biomes?[z, x] ?? 0;
                        var color = GetBlockAtLayer(pos, x, z, layerY, biomeId, subChunksCache);
                        image[offsetX + x, offsetZ + z] = new Rgba32(color.R, color.G, color.B, color.A);
                    }
                }
            }
        }

        return image;
    }

    public Image<Rgba32> RenderCaveSlice(Dimension dimension, int minChunkX, int minChunkZ, int maxChunkX,
        int maxChunkZ, int caveY)
    {
        var width = (maxChunkX - minChunkX + 1) * 16;
        var height = (maxChunkZ - minChunkZ + 1) * 16;
        var image = new Image<Rgba32>(width, height);

        var subChunksCache = new Dictionary<(ChunkPos, sbyte), SubChunk?>();

        for (var chunkZ = minChunkZ; chunkZ <= maxChunkZ; chunkZ++)
        {
            for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
            {
                var pos = new ChunkPos(chunkX, chunkZ, dimension);
                var (_, biomes) = _world.GetChunkTerrain(pos);

                var offsetX = (chunkX - minChunkX) * 16;
                var offsetZ = (chunkZ - minChunkZ) * 16;

                for (var z = 0; z < 16; z++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        var biomeId = biomes?[z, x] ?? 0;
                        var color = GetBlockAtLayer(pos, x, z, caveY, biomeId, subChunksCache);
                        image[offsetX + x, offsetZ + z] = new Rgba32(color.R, color.G, color.B, color.A);
                    }
                }
            }
        }

        return image;
    }

    private RgbaColor GetSurfaceBlockColor(ChunkPos pos, int localX, int localZ, short height, int biomeId,
        Dictionary<(ChunkPos, sbyte), SubChunk?> subChunksCache)
    {
        if (height < -64)
            return _palette.VoidColor;

        for (var y = height; y >= -64; y--)
        {
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
                continue;

            var blockState = subChunk.BlockStateAt(localX, localY, localZ);
            if (blockState == null)
                continue;

            var blockName = blockState.Name;

            if (_palette.IsAirBlock(blockName))
                continue;

            return _palette.SurfaceBlockColor(blockName, biomeId, true);
        }

        return _palette.VoidColor;
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
}