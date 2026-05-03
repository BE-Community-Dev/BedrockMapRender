namespace BedrockRender.Avalonia;

public sealed class MapPointerPositionChangedEventArgs : EventArgs
{
    public MapPointerPositionChangedEventArgs(int? worldX, int? worldZ)
    {
        WorldX = worldX;
        WorldZ = worldZ;
    }

    public int? WorldX { get; }

    public int? WorldZ { get; }
}

public sealed class MapViewChangedEventArgs : EventArgs
{
    public MapViewChangedEventArgs(double scale, double offsetX, double offsetY)
    {
        Scale = scale;
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    public double Scale { get; }

    public double OffsetX { get; }

    public double OffsetY { get; }
}
