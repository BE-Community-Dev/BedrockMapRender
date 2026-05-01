namespace BedrockRender.Palette;

public struct RgbaColor : IEquatable<RgbaColor>
{
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public byte A { get; set; }
    
    public RgbaColor(byte r, byte g, byte b, byte a = 255)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }
    
    public RgbaColor Blend(RgbaColor other, float t)
    {
        return new RgbaColor(
            (byte)(R * (1 - t) + other.R * t),
            (byte)(G * (1 - t) + other.G * t),
            (byte)(B * (1 - t) + other.B * t),
            (byte)(A * (1 - t) + other.A * t)
        );
    }
    
    public static RgbaColor Lerp(RgbaColor a, RgbaColor b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        return new RgbaColor(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            (byte)(a.A + (b.A - a.A) * t)
        );
    }
    
    public uint ToUint()
    {
        return (uint)((A << 24) | (R << 16) | (G << 8) | B);
    }
    
    public bool Equals(RgbaColor other)
    {
        return R == other.R && G == other.G && B == other.B && A == other.A;
    }
    
    public override bool Equals(object? obj)
    {
        return obj is RgbaColor other && Equals(other);
    }
    
    public override int GetHashCode()
    {
        return HashCode.Combine(R, G, B, A);
    }
    
    public static bool operator ==(RgbaColor left, RgbaColor right) => left.Equals(right);
    public static bool operator !=(RgbaColor left, RgbaColor right) => !left.Equals(right);
}
