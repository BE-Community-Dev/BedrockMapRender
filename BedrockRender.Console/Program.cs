using BedrockLevelDB;
using BedrockWorld;
using BedrockWorld.Chunk;
using BedrockRender;
using BedrockRender.Palette;
using SixLabors.ImageSharp;

Console.WriteLine("Bedrock Render - C# Edition");
Console.WriteLine("==========================");

var worldPath = @"E:\JMGS36_EX1";

if (!Directory.Exists(worldPath))
{
    Console.WriteLine($"World path not found: {worldPath}");
    Console.WriteLine("Please check the path and try again.");
    return;
}

Console.WriteLine($"Loading world from: {worldPath}");
using var world = new BedrockWorld.BedrockWorld(worldPath);

Console.WriteLine($"Level name: {world.LevelDat.LevelName}");
Console.WriteLine($"Spawn: {world.LevelDat.SpawnX}, {world.LevelDat.SpawnY}, {world.LevelDat.SpawnZ}");

Console.WriteLine("\nListing chunks...");
var chunks = world.ListChunkPositions(Dimension.Overworld);
Console.WriteLine($"Found {chunks.Count} chunks");

if (chunks.Count == 0)
{
    Console.WriteLine("No chunks found.");
    return;
}

var minX = chunks.Min(c => c.X);
var maxX = chunks.Max(c => c.X);
var minZ = chunks.Min(c => c.Z);
var maxZ = chunks.Max(c => c.Z);

Console.WriteLine($"Bounds: X {minX}..{maxX}, Z {minZ}..{maxZ}");
Console.WriteLine($"World size: {(maxX - minX + 1) * 16}x{(maxZ - minZ + 1) * 16} pixels");

Console.WriteLine("\nLoading palette...");
var basePath = AppDomain.CurrentDomain.BaseDirectory;
var blockColorPath = Path.Combine(basePath, "data", "colors", "bedrock-block-color.json");
var biomeColorPath = Path.Combine(basePath, "data", "colors", "bedrock-biome-color.json");

if (!File.Exists(blockColorPath))
{
    var projPath = Path.Combine(basePath, "..", "..", "..", "..", "BedrockRender", "data", "colors", "bedrock-block-color.json");
    if (File.Exists(projPath))
    {
        blockColorPath = projPath;
        biomeColorPath = Path.Combine(Path.GetDirectoryName(projPath)!, "bedrock-biome-color.json");
    }
}

var palette = RenderPalette.Load(blockColorPath, biomeColorPath);
palette.ClearUnmatched();
Console.WriteLine($"Palette loaded with defaults and custom colors");

var renderer = new MapRenderer(world, palette);

var outputDir = Path.Combine(basePath, "output");
Directory.CreateDirectory(outputDir);

Console.WriteLine("\nRendering maps (no size limit)...");

Console.WriteLine("\n4. Rendering surface blocks...");
try
{
    using (var surfaceMap = renderer.RenderSurfaceBlocks(Dimension.Overworld, minX, minZ, maxX, maxZ))
    {
        var surfacePath = Path.Combine(outputDir, "surface-blocks.png");
        surfaceMap.Save(surfacePath);
        Console.WriteLine($"   Saved: {surfacePath} ({surfaceMap.Width}x{surfaceMap.Height})");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"   Error: {ex.Message}");
}

var missingBlocks = palette.UnmatchedBlocks;
missingBlocks.ToList().ForEach(Console.WriteLine);


Console.WriteLine("\n1. Rendering heightmap...");
using ( var heightMap = renderer.RenderHeightMap(Dimension.Overworld, minX, minZ, maxX, maxZ))
{
    var heightPath = Path.Combine(outputDir, "heightmap.png");
    heightMap.Save(heightPath);
    Console.WriteLine($"   Saved: {heightPath} ({heightMap.Width}x{heightMap.Height})");
}

Console.WriteLine("\n2. Rendering biome map...");
using (var biomeMap = renderer.RenderBiome(Dimension.Overworld, minX, minZ, maxX, maxZ))
{
    var biomePath = Path.Combine(outputDir, "biomemap.png");
    biomeMap.Save(biomePath);
    Console.WriteLine($"   Saved: {biomePath} ({biomeMap.Width}x{biomeMap.Height})");
}

Console.WriteLine("\n3. Rendering raw biome map...");
using (var rawBiomeMap = renderer.RenderBiome(Dimension.Overworld, minX, minZ, maxX, maxZ, 64, true))
{
    var rawBiomePath = Path.Combine(outputDir, "raw-biome-map.png");
    rawBiomeMap.Save(rawBiomePath);
    Console.WriteLine($"   Saved: {rawBiomePath} ({rawBiomeMap.Width}x{rawBiomeMap.Height})");
}
Console.WriteLine("\n5. Rendering layer at Y=64...");
try
{
    using (var layerMap = renderer.RenderLayerBlocks(Dimension.Overworld, minX, minZ, maxX, maxZ, 64))
    {
        var layerPath = Path.Combine(outputDir, "layer-y64.png");
        layerMap.Save(layerPath);
        Console.WriteLine($"   Saved: {layerPath} ({layerMap.Width}x{layerMap.Height})");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"   Error: {ex.Message}");
}

Console.WriteLine("\n6. Rendering cave slice at Y=32...");
try
{
    using (var caveMap = renderer.RenderCaveSlice(Dimension.Overworld, minX, minZ, maxX, maxZ, 32))
    {
        var cavePath = Path.Combine(outputDir, "cave-y32.png");
        caveMap.Save(cavePath);
        Console.WriteLine($"   Saved: {cavePath} ({caveMap.Width}x{caveMap.Height})");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"   Error: {ex.Message}");
}

Console.WriteLine($"\nDone! All maps saved to: {outputDir}");
Console.WriteLine("Open the output folder to view the rendered maps.");