using ComputeSharp;
using BedrockRender.Palette;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Buffers;

namespace BedrockRender.Gpu;

public class GpuRenderEngine : IDisposable
{
    private readonly GraphicsDevice? _device;
    private bool _disposed;

    public bool IsGpuEnabled => _device != null;

    public GpuRenderEngine()
    {
        try
        {
            _device = GraphicsDevice.GetDefault();
        }
        catch
        {
            _device = null;
        }
    }

    public static bool IsGpuAvailable()
    {
        try
        {
            using var device = GraphicsDevice.GetDefault();
            return device != null;
        }
        catch
        {
            return false;
        }
    }

    public Image<SixLabors.ImageSharp.PixelFormats.Rgba32> RenderSurfaceBlocksGpu(
        uint[] blockColors,
        int[] heightMap,
        int width,
        int height)
    {
        if (!IsGpuEnabled)
            throw new InvalidOperationException("GPU not available");

        var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);

        using var colorsBuffer = _device!.AllocateReadOnlyBuffer(blockColors);
        using var heightsBuffer = _device.AllocateReadOnlyBuffer(heightMap);
        using var outputBuffer = _device.AllocateReadWriteBuffer<uint>(width * height);

        var shader = new SurfaceBlockShader(colorsBuffer, heightsBuffer, outputBuffer, width, height);
        _device.For(width, height, shader);

        var pixels = outputBuffer.ToArray();
        CopyPixelsToImage(image, pixels, width, height);

        return image;
    }

    public Image<SixLabors.ImageSharp.PixelFormats.Rgba32> RenderHeightMapGpu(uint[] pixelData, int width, int height)
    {
        if (!IsGpuEnabled)
            throw new InvalidOperationException("GPU not available");

        var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
        CopyPixelsToImage(image, pixelData, width, height);
        return image;
    }

    public Image<SixLabors.ImageSharp.PixelFormats.Rgba32> RenderBiomeGpu(uint[] pixelData, int width, int height)
    {
        if (!IsGpuEnabled)
            throw new InvalidOperationException("GPU not available");

        var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
        CopyPixelsToImage(image, pixelData, width, height);
        return image;
    }

    private void CopyPixelsToImage(Image<SixLabors.ImageSharp.PixelFormats.Rgba32> image, uint[] pixelData, int width, int height)
    {
        var frame = image.Frames.RootFrame;
        for (var i = 0; i < pixelData.Length; i++)
        {
            var p = pixelData[i];
            var y = i / width;
            var x = i % width;
            frame[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgba32(
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

        _device?.Dispose();
        _disposed = true;
    }
}

[ThreadGroupSize(32, 32, 1)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct SurfaceBlockShader : IComputeShader
{
    public readonly ReadOnlyBuffer<uint> BlockColors;
    public readonly ReadOnlyBuffer<int> HeightMap;
    public readonly ReadWriteBuffer<uint> Output;
    public readonly int Width;
    public readonly int Height;

    public SurfaceBlockShader(ReadOnlyBuffer<uint> blockColors, ReadOnlyBuffer<int> heightMap, ReadWriteBuffer<uint> output, int width, int height)
    {
        BlockColors = blockColors;
        HeightMap = heightMap;
        Output = output;
        Width = width;
        Height = height;
    }

    public void Execute()
    {
        int x = ThreadIds.X;
        int z = ThreadIds.Y;

        if (x >= Width || z >= Height)
            return;

        int idx = z * Width + x;
        uint colorUint = BlockColors[idx];
        int h = HeightMap[idx];

        float r = ((colorUint >> 16) & 0xFF);
        float g = ((colorUint >> 8) & 0xFF);
        float b = (colorUint & 0xFF);
        float alpha = ((colorUint >> 24) & 0xFF);

        if (alpha == 0)
        {
            Output[idx] = 0;
            return;
        }

        float nw = GetHeightOrSelf(x - 1, z - 1, h);
        float n = GetHeightOrSelf(x, z - 1, h);
        float w = GetHeightOrSelf(x - 1, z, h);
        float se = GetHeightOrSelf(x + 1, z + 1, h);
        float s = GetHeightOrSelf(x, z + 1, h);
        float e = GetHeightOrSelf(x + 1, z, h);

        float shadeDrop = clamp(Max(Max(nw - h, n - h), w - h), 0.0f, 12.0f);
        float lightDrop = clamp(Max(Max(h - se, h - s), h - e), 0.0f, 12.0f);
        float shadow = clamp(1.0f - shadeDrop * 0.18f + lightDrop * 0.081f, 0.55f, 1.18f);
        float heightFactor = clamp((h + 64) / 400f + 0.8f, 0.8f, 1.05f);
        float finalIntensity = shadow * heightFactor;

        int finalR = (int)clamp(r * finalIntensity, 0, 255);
        int finalG = (int)clamp(g * finalIntensity, 0, 255);
        int finalB = (int)clamp(b * finalIntensity, 0, 255);

        uint finalColor = (255u << 24) | ((uint)finalR << 16) | ((uint)finalG << 8) | (uint)finalB;
        Output[idx] = finalColor;
    }

    static float clamp(float v, float min, float max)
    {
        return v < min ? min : v > max ? max : v;
    }

    static float Max(float a, float b)
    {
        return a > b ? a : b;
    }

    float GetHeightOrSelf(int x, int z, int fallback)
    {
        if (x < 0 || z < 0 || x >= Width || z >= Height)
            return fallback;

        return HeightMap[z * Width + x];
    }
}