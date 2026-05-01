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
    
    public Image<Rgba32> RenderHeightMap(Dimension dimension, int minChunkX, int minChunkZ, int maxChunkX, int maxChunkZ, short minHeight = -64, short maxHeight = 320)
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
    
    public Image<Rgba32> RenderBiome(Dimension dimension, int minChunkX, int minChunkZ, int maxChunkX, int maxChunkZ, int layerY = 64, bool rawBiome = false)
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
    
    public Image<Rgba32> RenderSurfaceBlocks(Dimension dimension, int minChunkX, int minChunkZ, int maxChunkX, int maxChunkZ)
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
                var (heightMap, biomes) = _world.GetChunkTerrain(pos);
                
                var offsetX = (chunkX - minChunkX) * 16;
                var offsetZ = (chunkZ - minChunkZ) * 16;
                
                for (var z = 0; z < 16; z++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        var biomeId = biomes?[z, x] ?? 0;
                        var h = heightMap?[z, x] ?? (short)0;
                        var color = GetSurfaceBlockColor(pos, x, z, h, biomeId, subChunksCache);
                        image[offsetX + x, offsetZ + z] = new Rgba32(color.R, color.G, color.B, color.A);
                    }
                }
            }
        }
        
        return image;
    }
    
    public Image<Rgba32> RenderLayerBlocks(Dimension dimension, int minChunkX, int minChunkZ, int maxChunkX, int maxChunkZ, int layerY)
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
    
    public Image<Rgba32> RenderCaveSlice(Dimension dimension, int minChunkX, int minChunkZ, int maxChunkX, int maxChunkZ, int caveY)
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
    
    private RgbaColor GetSurfaceBlockColor(ChunkPos pos, int localX, int localZ, short height, int biomeId, Dictionary<(ChunkPos, sbyte), SubChunk?> subChunksCache)
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
}
