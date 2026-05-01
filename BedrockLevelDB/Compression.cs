using System.IO.Compression;

namespace BedrockLevelDB;

public static class Compression
{
    public static byte[] Decompress(byte[] input, int compressedSize, int uncompressedSize)
    {
        if (input.Length == 0)
            return Array.Empty<byte>();
        
        var firstByte = input[0];
        
        if (firstByte == 0x02)
        {
            return DecompressSnappy(input, compressedSize, uncompressedSize);
        }
        else if (firstByte == 0x78)
        {
            return DecompressZlib(input);
        }
        else if (firstByte == 0x1f && input.Length > 1 && input[1] == 0x8b)
        {
            return DecompressGzip(input);
        }
        
        if (IsSnappyCompressed(input))
        {
            return DecompressSnappy(input, compressedSize, uncompressedSize);
        }
        
        try
        {
            return DecompressZlib(input);
        }
        catch
        {
            return input;
        }
    }
    
    private static bool IsSnappyCompressed(byte[] data)
    {
        if (data.Length < 4)
            return false;
        
        var magic = data[0];
        if (magic != 0x02 && magic != 0x03 && magic != 0x04 && magic != 0x05 && magic != 0x06)
            return false;
        
        return true;
    }
    
    private static byte[] DecompressSnappy(byte[] input, int compressedSize, int uncompressedSize)
    {
        if (input.Length == 0)
            return Array.Empty<byte>();
        
        try
        {
            var result = new byte[uncompressedSize > 0 ? uncompressedSize : input.Length * 10];
            var offset = 1;
            var outOffset = 0;
            
            while (offset < input.Length && outOffset < result.Length)
            {
                var b = input[offset++];
                
                if ((b & 0x03) == 0)
                {
                    var length = (b >> 2) + 1;
                    if (offset + length > input.Length)
                        break;
                    
                    for (var i = 0; i < length && outOffset < result.Length; i++)
                    {
                        result[outOffset++] = input[offset++];
                    }
                }
                else if ((b & 0x03) == 0x01)
                {
                    var length = ((b >> 2) & 0x07) + 4;
                    if (offset + 1 > input.Length)
                        break;
                    
                    var copyOffset = input[offset++] | ((b >> 5) << 8);
                    var copyStart = outOffset - copyOffset - 1;
                    
                    for (var i = 0; i < length && outOffset < result.Length; i++)
                    {
                        if (copyStart + i < 0 || copyStart + i >= outOffset)
                            break;
                        result[outOffset++] = result[copyStart + i];
                    }
                }
                else if ((b & 0x03) == 0x02)
                {
                    var length = (b >> 2) + 1;
                    if (offset + 2 > input.Length)
                        break;
                    
                    var copyOffset = input[offset++] | (input[offset++] << 8);
                    var copyStart = outOffset - copyOffset - 1;
                    
                    for (var i = 0; i < length && outOffset < result.Length; i++)
                    {
                        if (copyStart + i < 0 || copyStart + i >= outOffset)
                            break;
                        result[outOffset++] = result[copyStart + i];
                    }
                }
                else
                {
                    var length = ((b >> 2) & 0x1f) + 1;
                    if (offset + 3 > input.Length)
                        break;
                    
                    var copyOffset = input[offset++] | (input[offset++] << 8) | (input[offset++] << 16);
                    var copyStart = outOffset - copyOffset - 1;
                    
                    for (var i = 0; i < length && outOffset < result.Length; i++)
                    {
                        if (copyStart + i < 0 || copyStart + i >= outOffset)
                            break;
                        result[outOffset++] = result[copyStart + i];
                    }
                }
            }
            
            return result.Take(outOffset).ToArray();
        }
        catch
        {
            return input;
        }
    }
    
    private static byte[] DecompressZlib(byte[] input)
    {
        using var ms = new MemoryStream(input);
        using var zlib = new DeflateStream(ms, CompressionMode.Decompress);
        using var result = new MemoryStream();
        zlib.CopyTo(result);
        return result.ToArray();
    }
    
    private static byte[] DecompressGzip(byte[] input)
    {
        using var ms = new MemoryStream(input);
        using var gzip = new GZipStream(ms, CompressionMode.Decompress);
        using var result = new MemoryStream();
        gzip.CopyTo(result);
        return result.ToArray();
    }
}
