using ComputeSharp;

namespace BedrockRender.Gpu;

public enum RenderEngineType
{
    Auto,
    Cpu,
    Gpu
}

public static class GpuCapabilities
{
    private static bool? _isGpuAvailable;

    public static bool IsGpuAvailable()
    {
        if (_isGpuAvailable.HasValue)
            return _isGpuAvailable.Value;

        try
        {
            using var device = GraphicsDevice.GetDefault();
            _isGpuAvailable = device != null;
            return _isGpuAvailable.Value;
        }
        catch
        {
            _isGpuAvailable = false;
            return false;
        }
    }

    public static RenderEngineType DetectBestEngine()
    {
        if (IsGpuAvailable())
        {
            return RenderEngineType.Gpu;
        }
        return RenderEngineType.Cpu;
    }

    public static void ResetDetection()
    {
        _isGpuAvailable = null;
    }
}