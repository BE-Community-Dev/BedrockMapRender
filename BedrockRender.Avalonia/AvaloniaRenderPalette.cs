using Avalonia.Platform;
using BedrockRender.Palette;

namespace BedrockRender.Avalonia;

public static class AvaloniaRenderPalette
{
    private static readonly Uri BlockColorUri = new("avares://BedrockRender.Avalonia/data/colors/bedrock-block-color.json");
    private static readonly Uri BiomeColorUri = new("avares://BedrockRender.Avalonia/data/colors/bedrock-biome-color.json");

    public static RenderPalette LoadDefault()
    {
        var palette = new RenderPalette();
        palette.MergeBiomeJson(ReadResourceText(BiomeColorUri));
        palette.MergeBlockJson(ReadResourceText(BlockColorUri));
        return palette;
    }

    private static string ReadResourceText(Uri uri)
    {
        using var stream = AssetLoader.Open(uri);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}