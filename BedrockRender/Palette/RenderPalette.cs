using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace BedrockRender.Palette;

public class RenderPalette
{
    private readonly Dictionary<string, RgbaColor> _blockColors = new Dictionary<string, RgbaColor>();
    private readonly Dictionary<int, RgbaColor> _biomeColors = new Dictionary<int, RgbaColor>();
    private readonly Dictionary<int, RgbaColor> _biomeGrassColors = new Dictionary<int, RgbaColor>();
    private readonly Dictionary<int, RgbaColor> _biomeFoliageColors = new Dictionary<int, RgbaColor>();
    private readonly Dictionary<int, RgbaColor> _biomeWaterColors = new Dictionary<int, RgbaColor>();

    private readonly ConcurrentDictionary<string, bool> _unmatchedBlocks = new ConcurrentDictionary<string, bool>();
    private readonly ConcurrentDictionary<int, bool> _unmatchedBiomes = new ConcurrentDictionary<int, bool>();

    public RgbaColor UnknownBiomeColor { get; set; } = new RgbaColor(255, 0, 255, 180);
    public RgbaColor UnknownBlockColor { get; set; } = new RgbaColor(255, 0, 255, 255);
    public RgbaColor MissingChunkColor { get; set; } = new RgbaColor(0, 0, 0, 0);
    public RgbaColor VoidColor { get; set; } = new RgbaColor(0, 0, 0, 0);
    public RgbaColor AirColor { get; set; } = new RgbaColor(0, 0, 0, 0);
    public RgbaColor DefaultGrassColor { get; set; } = new RgbaColor(142, 185, 113, 255);
    public RgbaColor DefaultFoliageColor { get; set; } = new RgbaColor(113, 167, 77, 255);
    public RgbaColor DefaultWaterColor { get; set; } = new RgbaColor(63, 118, 228, 255);
    public RgbaColor MinHeightColor { get; set; } = new RgbaColor(36, 52, 100, 255);
    public RgbaColor MaxHeightColor { get; set; } = new RgbaColor(242, 244, 232, 255);

    public IReadOnlyCollection<string> UnmatchedBlocks => _unmatchedBlocks.Keys.ToList().AsReadOnly();
    public IReadOnlyCollection<int> UnmatchedBiomes => _unmatchedBiomes.Keys.ToList().AsReadOnly();

    public void ClearUnmatched()
    {
        _unmatchedBlocks.Clear();
        _unmatchedBiomes.Clear();
    }

    public RenderPalette()
    {
        InsertDefaultBiomes();
        InsertDefaultBlocks();
    }

    public static RenderPalette Load(string blockColorPath, string biomeColorPath)
    {
        var palette = new RenderPalette();

        if (File.Exists(biomeColorPath))
        {
            try
            {
                var json = File.ReadAllText(biomeColorPath);
                palette.MergeBiomeJson(json);
            }
            catch { }
        }

        if (File.Exists(blockColorPath))
        {
            try
            {
                var json = File.ReadAllText(blockColorPath);
                palette.MergeBlockJson(json);
            }
            catch { }
        }

        return palette;
    }

    public void MergeBlockJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("blocks", out var blocks) || root.TryGetProperty("block_colors", out blocks))
            {
                MergeBlockEntries(blocks);
            }

            if (root.TryGetProperty("defaults", out var defaults))
            {
                if (defaults.TryGetProperty("grass", out var grass))
                    DefaultGrassColor = ParseColor(grass) ?? DefaultGrassColor;
                if (defaults.TryGetProperty("leaves", out var leaves) || defaults.TryGetProperty("foliage", out leaves))
                    DefaultFoliageColor = ParseColor(leaves) ?? DefaultFoliageColor;
                if (defaults.TryGetProperty("water", out var water))
                    DefaultWaterColor = ParseColor(water) ?? DefaultWaterColor;
            }
        }
        catch { }
    }

    private void MergeBlockEntries(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var color = ParseBlockColorEntry(prop.Value);
                if (color.HasValue)
                {
                    var name = NormalizeBlockName(prop.Name);
                    _blockColors[name] = color.Value;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    string? name = null;

                    if (item.TryGetProperty("name", out var nameEl) || item.TryGetProperty("id", out nameEl) || item.TryGetProperty("identifier", out nameEl))
                        name = nameEl.GetString();

                    if (name != null)
                    {
                        var color = ParseBlockColorEntry(item);
                        if (color.HasValue)
                            _blockColors[NormalizeBlockName(name)] = color.Value;
                    }
                }
            }
        }
    }

    public void MergeBiomeJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("biomes", out var biomes) || root.TryGetProperty("biome_colors", out biomes))
            {
                MergeBiomeEntries(biomes);
            }

            if (root.TryGetProperty("defaults", out var defaults))
            {
                if (defaults.TryGetProperty("grass", out var grass))
                    DefaultGrassColor = ParseColor(grass) ?? DefaultGrassColor;
                if (defaults.TryGetProperty("leaves", out var leaves) || defaults.TryGetProperty("foliage", out leaves))
                    DefaultFoliageColor = ParseColor(leaves) ?? DefaultFoliageColor;
                if (defaults.TryGetProperty("water", out var water))
                    DefaultWaterColor = ParseColor(water) ?? DefaultWaterColor;
            }
        }
        catch { }
    }

    private void MergeBiomeEntries(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                int? id = null;
                if (int.TryParse(prop.Name, out var parsedId))
                    id = parsedId;

                var entry = prop.Value;
                if (entry.ValueKind == JsonValueKind.Object)
                {
                    if (entry.TryGetProperty("id", out var idEl))
                        id = idEl.GetInt32();

                    var color = ParseColor(entry);
                    if (color.HasValue && id.HasValue)
                    {
                        _biomeColors[id.Value] = color.Value;

                        if (entry.TryGetProperty("grass", out var grass))
                        {
                            var c = ParseColor(grass);
                            if (c.HasValue) _biomeGrassColors[id.Value] = c.Value;
                        }
                        if (entry.TryGetProperty("leaves", out var leaves))
                        {
                            var c = ParseColor(leaves);
                            if (c.HasValue) _biomeFoliageColors[id.Value] = c.Value;
                        }
                        if (entry.TryGetProperty("water", out var water))
                        {
                            var c = ParseColor(water);
                            if (c.HasValue) _biomeWaterColors[id.Value] = c.Value;
                        }
                    }
                }
            }
        }
    }

    private RgbaColor? ParseBlockColorEntry(JsonElement element)
    {
        var color = ParseColor(element);
        if (color.HasValue) return color;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "default", "color", "map_color", "rgba", "rgb" })
            {
                if (element.TryGetProperty(key, out var c))
                {
                    color = ParseColor(c);
                    if (color.HasValue) return color;
                }
            }
        }

        return null;
    }

    private RgbaColor? ParseColor(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return ParseHexColor(element.GetString() ?? "");
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var arr = element.EnumerateArray().ToArray();
            if (arr.Length >= 3)
            {
                var r = arr[0].GetByte();
                var g = arr[1].GetByte();
                var b = arr[2].GetByte();
                var a = arr.Length >= 4 ? arr[3].GetByte() : (byte)255;
                return new RgbaColor(r, g, b, a);
            }
        }
        else if (element.ValueKind == JsonValueKind.Number)
        {
            var val = element.GetInt32();
            return new RgbaColor(
                (byte)((val >> 16) & 0xff),
                (byte)((val >> 8) & 0xff),
                (byte)(val & 0xff),
                255
            );
        }

        return null;
    }

    private RgbaColor? ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#').TrimStart("0x".ToCharArray());

        if (hex.Length == 6)
        {
            try
            {
                var r = Convert.ToByte(hex.Substring(0, 2), 16);
                var g = Convert.ToByte(hex.Substring(2, 2), 16);
                var b = Convert.ToByte(hex.Substring(4, 2), 16);
                return new RgbaColor(r, g, b, 255);
            }
            catch { }
        }
        else if (hex.Length == 8)
        {
            try
            {
                var r = Convert.ToByte(hex.Substring(0, 2), 16);
                var g = Convert.ToByte(hex.Substring(2, 2), 16);
                var b = Convert.ToByte(hex.Substring(4, 2), 16);
                var a = Convert.ToByte(hex.Substring(6, 2), 16);
                return new RgbaColor(r, g, b, a);
            }
            catch { }
        }

        return null;
    }

    private string NormalizeBlockName(string name)
    {
        return name.Contains(':') ? name : "minecraft:" + name;
    }

    private void InsertDefaultBiomes()
    {
        var defaults = new (int id, byte r, byte g, byte b)[]
        {
            (0, 141, 179, 96),
            (1, 250, 148, 24),
            (2, 250, 240, 192),
            (3, 96, 96, 96),
            (4, 5, 102, 33),
            (5, 11, 102, 89),
            (6, 7, 249, 178),
            (7, 0, 0, 255),
            (8, 255, 0, 0),
            (9, 128, 128, 255),
            (10, 160, 160, 255),
            (11, 255, 255, 255),
            (12, 160, 160, 160),
            (13, 255, 255, 160),
            (14, 0, 160, 0),
            (15, 255, 200, 128),
            (16, 255, 220, 160),
            (17, 48, 116, 68),
            (18, 27, 82, 54),
            (19, 89, 102, 81),
            (20, 69, 79, 62),
            (21, 80, 112, 80),
            (22, 129, 161, 129),
            (23, 91, 95, 69),
            (24, 30, 144, 255),
            (25, 98, 140, 120),
            (26, 112, 141, 129),
            (27, 160, 167, 140),
            (28, 119, 156, 98),
            (29, 180, 180, 180),
            (30, 160, 160, 180),
            (31, 40, 60, 40),
            (32, 50, 70, 50),
            (33, 189, 178, 95),
            (34, 167, 157, 100),
            (35, 120, 120, 120),
            (36, 80, 80, 80),
            (37, 90, 120, 80),
            (38, 130, 130, 100),
            (39, 85, 107, 47),
            (40, 255, 255, 255),
            (41, 0, 0, 172),
            (42, 45, 85, 180),
            (43, 32, 70, 150),
            (44, 32, 85, 150),
            (45, 28, 65, 130),
            (46, 90, 160, 190),
            (47, 70, 120, 160),
            (48, 80, 150, 70),
            (49, 55, 120, 55),
            (50, 189, 178, 95),
            (81, 70, 100, 35),
            (82, 80, 85, 38),
            (129, 220, 220, 220),
            (130, 255, 188, 64),
            (131, 80, 80, 80),
            (132, 34, 139, 34),
            (133, 20, 120, 95),
            (134, 35, 92, 70),
            (140, 200, 220, 255),
            (149, 30, 120, 30),
            (151, 105, 130, 105),
            (155, 120, 145, 90),
            (156, 175, 175, 175),
            (157, 110, 110, 130),
            (158, 45, 70, 45),
            (160, 177, 170, 90),
            (161, 96, 120, 70),
            (162, 178, 164, 90),
            (163, 160, 140, 80),
            (164, 140, 105, 65),
            (165, 175, 120, 80),
            (166, 150, 90, 70),
            (167, 155, 120, 100),
            (168, 60, 150, 80),
            (169, 40, 120, 65),
        };

        foreach (var (id, r, g, b) in defaults)
        {
            _biomeColors[id] = new RgbaColor(r, g, b, 255);
        }
    }

    private void InsertDefaultBlocks()
    {
        var defaults = new (string name, byte r, byte g, byte b, byte a)[]
        {
            ("minecraft:air", 0, 0, 0, 0),
            ("air", 0, 0, 0, 0),
            ("minecraft:cave_air", 0, 0, 0, 0),
            ("minecraft:void_air", 0, 0, 0, 0),
            ("minecraft:grass", 88, 150, 62, 255),
            ("minecraft:grass_block", 102, 158, 74, 255),
            ("minecraft:dirt", 134, 96, 67, 255),
            ("minecraft:coarse_dirt", 119, 85, 61, 255),
            ("minecraft:rooted_dirt", 113, 82, 57, 255),
            ("minecraft:stone", 125, 125, 125, 255),
            ("minecraft:deepslate", 80, 80, 82, 255),
            ("minecraft:cobblestone", 109, 109, 109, 255),
            ("minecraft:granite", 149, 103, 85, 255),
            ("minecraft:diorite", 190, 190, 190, 255),
            ("minecraft:andesite", 136, 136, 136, 255),
            ("minecraft:tuff", 92, 92, 86, 255),
            ("minecraft:calcite", 224, 222, 214, 255),
            ("minecraft:sand", 218, 210, 158, 255),
            ("minecraft:sandstone", 216, 203, 145, 255),
            ("minecraft:red_sand", 190, 103, 33, 255),
            ("minecraft:red_sandstone", 178, 88, 30, 255),
            ("minecraft:gravel", 126, 122, 118, 255),
            ("minecraft:clay", 160, 166, 176, 255),
            ("minecraft:mud", 63, 54, 50, 255),
            ("minecraft:water", 43, 92, 210, 190),
            ("minecraft:flowing_water", 43, 92, 210, 190),
            ("minecraft:ice", 160, 210, 255, 210),
            ("minecraft:packed_ice", 130, 185, 242, 235),
            ("minecraft:blue_ice", 102, 167, 240, 235),
            ("minecraft:snow", 245, 250, 250, 255),
            ("minecraft:snow_layer", 245, 250, 250, 230),
            ("minecraft:oak_leaves", 60, 112, 42, 245),
            ("minecraft:spruce_leaves", 46, 86, 46, 245),
            ("minecraft:birch_leaves", 85, 124, 49, 245),
            ("minecraft:jungle_leaves", 42, 108, 44, 245),
            ("minecraft:acacia_leaves", 72, 112, 44, 245),
            ("minecraft:dark_oak_leaves", 38, 82, 36, 245),
            ("minecraft:mangrove_leaves", 44, 98, 44, 245),
            ("minecraft:cherry_leaves", 244, 174, 188, 245),
            ("minecraft:leaves", 60, 112, 42, 245),
            ("minecraft:log", 102, 76, 45, 255),
            ("minecraft:oak_log", 102, 76, 45, 255),
            ("minecraft:birch_log", 197, 184, 135, 255),
            ("minecraft:spruce_log", 76, 55, 34, 255),
            ("minecraft:jungle_log", 105, 78, 43, 255),
            ("minecraft:acacia_log", 138, 75, 42, 255),
            ("minecraft:dark_oak_log", 58, 39, 23, 255),
            ("minecraft:mangrove_log", 91, 48, 43, 255),
            ("minecraft:cherry_log", 126, 78, 85, 255),
            ("minecraft:planks", 157, 128, 79, 255),
            ("minecraft:oak_planks", 157, 128, 79, 255),
            ("minecraft:birch_planks", 196, 178, 116, 255),
            ("minecraft:spruce_planks", 114, 84, 48, 255),
            ("minecraft:jungle_planks", 154, 109, 77, 255),
            ("minecraft:acacia_planks", 174, 92, 50, 255),
            ("minecraft:dark_oak_planks", 75, 50, 28, 255),
            ("minecraft:bedrock", 84, 84, 84, 255),
            ("minecraft:netherrack", 110, 53, 51, 255),
            ("minecraft:basalt", 74, 72, 76, 255),
            ("minecraft:blackstone", 42, 36, 43, 255),
            ("minecraft:soul_sand", 82, 64, 56, 255),
            ("minecraft:soul_soil", 77, 60, 52, 255),
            ("minecraft:warped_nylium", 43, 106, 103, 255),
            ("minecraft:crimson_nylium", 117, 33, 45, 255),
            ("minecraft:end_stone", 220, 222, 158, 255),
            ("minecraft:obsidian", 26, 20, 39, 255),
            ("minecraft:terracotta", 152, 94, 67, 255),
            ("minecraft:lava", 255, 90, 0, 255),
            ("minecraft:flowing_lava", 255, 90, 0, 255),
            ("minecraft:cactus", 35, 116, 49, 255),
        };

        foreach (var (name, r, g, b, a) in defaults)
        {
            _blockColors[name] = new RgbaColor(r, g, b, a);
        }
    }

    public RgbaColor BiomeColor(int biomeId)
    {
        if (_biomeColors.TryGetValue(biomeId, out var color))
            return color;
        _unmatchedBiomes[biomeId] = true;
        return UnknownBiomeColor;
    }

    public RgbaColor BlockColor(string blockName)
    {
        if (IsAirBlock(blockName))
            return AirColor;

        if (_blockColors.TryGetValue(blockName, out var color))
            return color;

        var shortName = blockName.Contains(':') ? blockName.Split(':')[1] : blockName;
        if (_blockColors.TryGetValue("minecraft:" + shortName, out color))
            return color;

        return CategoryBlockColor(blockName);
    }

    public RgbaColor SurfaceBlockColor(string blockName, int? biomeId, bool biomeTint)
    {
        var color = BlockColor(blockName);

        if (!biomeTint)
            return WithAlpha(color, 255);

        if (IsGrassTintedBlock(blockName))
        {
            var tint = biomeId.HasValue && _biomeGrassColors.TryGetValue(biomeId.Value, out var t) ? t : DefaultGrassColor;
            return MultiplyWithTint(color, tint);
        }

        if (IsFoliageTintedBlock(blockName))
        {
            var tint = biomeId.HasValue && _biomeFoliageColors.TryGetValue(biomeId.Value, out var t) ? t : DefaultFoliageColor;
            return MultiplyWithTint(color, tint);
        }

        if (IsWaterBlock(blockName))
        {
            var tint = biomeId.HasValue && _biomeWaterColors.TryGetValue(biomeId.Value, out var t) ? t : DefaultWaterColor;
            return MultiplyWithTint(color, tint);
        }

        return WithAlpha(color, 255);
    }

    public RgbaColor HeightColor(short height, short minHeight, short maxHeight)
    {
        if (minHeight >= maxHeight)
            return MaxHeightColor;

        var t = (float)(height - minHeight) / (maxHeight - minHeight);
        t = Math.Clamp(t, 0, 1);

        return LerpColor(MinHeightColor, MaxHeightColor, t);
    }

    public bool IsAirBlock(string name)
    {
        return name == "air" || name == "minecraft:air" ||
               name == "minecraft:cave_air" || name == "minecraft:void_air" ||
               name == "minecraft:light_block" || name == "minecraft:light";
    }

    private bool IsWaterBlock(string name)
    {
        return name.Contains("water");
    }

    private bool IsGrassTintedBlock(string name)
    {
        return name.Contains("grass_block") || name.EndsWith(":grass") ||
               name.EndsWith(":short_grass") || name.EndsWith(":tall_grass") ||
               name.Contains("fern") || name.Contains("vine");
    }

    private bool IsFoliageTintedBlock(string name)
    {
        return name.Contains("leaves") || name.Contains("leaf") || name.Contains("foliage");
    }

    private RgbaColor CategoryBlockColor(string name)
    {
        var shortName = name.StartsWith("minecraft:") ? name.Substring(10) : name;

        if (shortName.Contains("coral")) return new RgbaColor(210, 88, 110, 255);
        if (shortName.Contains("copper")) return new RgbaColor(179, 109, 77, 255);
        if (shortName.Contains("resin")) return new RgbaColor(226, 112, 32, 255);
        if (shortName.Contains("amethyst")) return new RgbaColor(154, 112, 210, 255);
        if (shortName.Contains("prismarine")) return new RgbaColor(86, 154, 146, 255);
        if (shortName.Contains("basalt") || shortName.Contains("blackstone")) return new RgbaColor(42, 38, 45, 255);
        if (shortName.Contains("netherrack") || shortName.Contains("nylium") || shortName.Contains("wart")) return new RgbaColor(112, 42, 44, 255);
        if (shortName.Contains("end_stone") || shortName.Contains("end_brick")) return new RgbaColor(218, 222, 158, 255);
        if (shortName.Contains("ore")) return new RgbaColor(118, 118, 118, 255);
        if (shortName.Contains("stone") || shortName.Contains("slate") || shortName.Contains("brick") ||
            shortName.Contains("polished") || shortName.Contains("smooth") || shortName.Contains("chiseled") ||
            shortName.Contains("tile")) return new RgbaColor(122, 122, 122, 255);
        if (shortName.Contains("leaves") || shortName.Contains("leaf")) return new RgbaColor(58, 105, 42, 245);
        if (shortName.Contains("log") || shortName.Contains("stem") || shortName.Contains("wood") ||
            shortName.Contains("hyphae") || shortName.Contains("bamboo")) return new RgbaColor(99, 70, 42, 255);
        if (shortName.Contains("planks")) return new RgbaColor(154, 118, 70, 255);
        if (shortName.Contains("stairs") || shortName.Contains("slab") || shortName.Contains("fence") ||
            shortName.Contains("door") || shortName.Contains("trapdoor") || shortName.Contains("button") ||
            shortName.Contains("pressure_plate") || shortName.Contains("sign") || shortName.Contains("ladder") ||
            shortName.Contains("chest") || shortName.Contains("barrel") || shortName.Contains("bookshelf")) return new RgbaColor(145, 105, 62, 255);
        if (shortName.Contains("flower") || shortName.Contains("tulip") || shortName.Contains("daisy") ||
            shortName.Contains("orchid") || shortName.Contains("allium") || shortName.Contains("cornflower") ||
            shortName.Contains("peony") || shortName.Contains("lilac") || shortName.Contains("rose") ||
            shortName.Contains("petals") || shortName.Contains("spore_blossom") ||
            shortName.Contains("dandelion") || shortName.Contains("poppy") || shortName.Contains("azure_bluet") ||
            shortName.Contains("oxeye") || shortName.Contains("lily_of_the_valley") || shortName.Contains("sunflower") ||
            shortName.Contains("lilac") || shortName.Contains("rose")) return new RgbaColor(210, 120, 80, 255);
        if (shortName.Contains("grass") || shortName.Contains("fern") || shortName.Contains("bush") ||
            shortName.Contains("sapling") || shortName.Contains("crop") || shortName.Contains("wheat") ||
            shortName.Contains("carrots") || shortName.Contains("potatoes") || shortName.Contains("beetroot") ||
            shortName.Contains("melon_stem") || shortName.Contains("pumpkin_stem") || shortName.Contains("seagrass") ||
            shortName.Contains("kelp") || shortName.Contains("lily_pad") || shortName.Contains("moss") ||
            shortName.Contains("roots") || shortName.Contains("azalea") || shortName.Contains("cactus") ||
            shortName.Contains("sugar_cane") || shortName.Contains("vine") || shortName.Contains("reeds") ||
            shortName.Contains("bamboo") || shortName.Contains("pumpkin") || shortName.Contains("glow_lichen")) return new RgbaColor(77, 140, 56, 255);
        if (shortName.Contains("hay") || shortName.Contains("sponge") || shortName.Contains("honey")) return new RgbaColor(204, 164, 62, 255);
        if (shortName.Contains("mushroom")) return new RgbaColor(150, 96, 76, 255);
        if (shortName.Contains("torch") || shortName.Contains("lantern") || shortName.Contains("rail") ||
            shortName.Contains("redstone") || shortName.Contains("repeater") || shortName.Contains("comparator")) return new RgbaColor(168, 136, 84, 255);
        if (shortName.Contains("sand") || shortName.Contains("beach")) return new RgbaColor(214, 199, 140, 255);
        if (shortName.Contains("mud") || shortName.Contains("dirt") || shortName.Contains("path")) return new RgbaColor(124, 90, 62, 255);
        if (shortName.Contains("terracotta")) return new RgbaColor(145, 88, 63, 255);
        if (shortName.Contains("concrete")) return new RgbaColor(136, 136, 136, 255);
        if (shortName.Contains("wool") || shortName.Contains("carpet")) return new RgbaColor(190, 190, 190, 255);
        if (shortName.Contains("glass")) return new RgbaColor(180, 220, 235, 128);
        if (shortName.Contains("snow")) return new RgbaColor(245, 250, 250, 255);
        if (shortName.Contains("ice") || shortName.Contains("frosted")) return new RgbaColor(145, 200, 245, 220);
        if (shortName.Contains("water") || shortName.Contains("bubble")) return new RgbaColor(43, 92, 210, 190);
        if (shortName.Contains("lava") || shortName.Contains("magma")) return new RgbaColor(255, 90, 0, 255);
        if (shortName.Contains("obsidian")) return new RgbaColor(25, 20, 36, 255);
        if (shortName.Contains("bedrock")) return new RgbaColor(82, 82, 82, 255);

        _unmatchedBlocks[name] = true;
        return UnknownBlockColor;
    }

    private RgbaColor MultiplyWithTint(RgbaColor baseColor, RgbaColor tint)
    {
        return new RgbaColor(
            (byte)((baseColor.R * tint.R) / 255),
            (byte)((baseColor.G * tint.G) / 255),
            (byte)((baseColor.B * tint.B) / 255),
            255
        );
    }

    private RgbaColor WithAlpha(RgbaColor color, byte alpha)
    {
        return new RgbaColor(color.R, color.G, color.B, alpha);
    }

    private RgbaColor LerpColor(RgbaColor a, RgbaColor b, float t)
    {
        return new RgbaColor(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            (byte)(a.A + (b.A - a.A) * t)
        );
    }
}