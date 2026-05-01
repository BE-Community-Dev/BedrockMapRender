using System.Collections.Concurrent;
using System.Text;

namespace BedrockLevelDB;

public class LevelDB : IDisposable
{
    private readonly string _path;
    private readonly Dictionary<string, byte[]> _cache = new Dictionary<string, byte[]>();
    private readonly ConcurrentDictionary<string, byte[]> _walCache = new ConcurrentDictionary<string, byte[]>();
    private bool _disposed;
    private bool _cacheBuilt;
    
    public LevelDB(string path)
    {
        _path = path;
    }
    
    private void EnsureCacheBuilt()
    {
        if (_cacheBuilt)
            return;
        
        var dbPath = _path;
        
        if (!Directory.Exists(dbPath))
            return;
        
        foreach (var file in Directory.GetFiles(dbPath, "*.ldb"))
        {
            try
            {
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith("MANIFEST") || fileName.StartsWith("CURRENT"))
                    continue;
                
                ReadTableFile(file);
            }
            catch
            {
            }
        }
        
        ReadWalFiles();
        
        _cacheBuilt = true;
    }
    
    private void ReadTableFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);
            
            stream.Seek(-4, SeekOrigin.End);
            var blockSize = ReadUInt32LittleEndian(reader);
            
            stream.Seek(0, SeekOrigin.Begin);
            
            ReadDataBlocks(stream, reader);
        }
        catch
        {
        }
    }
    
    private void ReadDataBlocks(Stream stream, BinaryReader reader)
    {
        while (stream.Position < stream.Length - 4)
        {
            var header = ReadVarInt32(reader);
            if (header == 0)
                break;
            
            var blockType = (byte)(header & 0xff);
            var compressedSize = ReadVarInt32(reader);
            
            var blockStart = stream.Position;
            
            if (compressedSize > 0 && compressedSize < stream.Length - stream.Position)
            {
                try
                {
                    var data = ReadUncompressedBlock(stream, reader, compressedSize);
                    ParseDataBlock(data);
                }
                catch
                {
                }
            }
            
            stream.Position = blockStart + compressedSize;
        }
    }
    
    private byte[] ReadUncompressedBlock(Stream stream, BinaryReader reader, int compressedSize)
    {
        var compressedData = reader.ReadBytes(compressedSize);
        
        if (compressedSize < 16 * 1024 * 1024)
        {
            try
            {
                using var ms = new MemoryStream(compressedData);
                using var zlib = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
                using var result = new MemoryStream();
                zlib.CopyTo(result);
                return result.ToArray();
            }
            catch
            {
            }
        }
        
        return compressedData;
    }
    
    private void ParseDataBlock(byte[] data)
    {
        var offset = 0;
        var key = new List<byte>();
        var value = new List<byte>();
        var inKey = true;
        
        while (offset < data.Length)
        {
            var b = data[offset++];
            
            if (b == 0x00)
            {
                if (key.Count > 0)
                {
                    var keyStr = Encoding.UTF8.GetString(key.ToArray());
                    _cache[keyStr] = value.ToArray();
                }
                key.Clear();
                value.Clear();
                inKey = true;
            }
            else if (b == 0x01 && inKey)
            {
                inKey = false;
            }
            else
            {
                if (inKey)
                    key.Add(b);
                else
                    value.Add(b);
            }
        }
    }
    
    private void ReadWalFiles()
    {
        if (!Directory.Exists(_path))
            return;
        
        foreach (var file in Directory.GetFiles(_path, "*.log"))
        {
            try
            {
                ReadWalFile(file);
            }
            catch
            {
            }
        }
    }
    
    private void ReadWalFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);
            
            while (stream.Position < stream.Length - 4)
            {
                var recordSize = ReadUInt32LittleEndian(reader);
                if (recordSize == 0 || recordSize > stream.Length - stream.Position)
                    break;
                
                var recordData = reader.ReadBytes((int)recordSize);
                ParseWalRecord(recordData);
            }
        }
        catch
        {
        }
    }
    
    private void ParseWalRecord(byte[] data)
    {
        var offset = 0;
        
        while (offset < data.Length)
        {
            if (offset + 4 > data.Length)
                break;
            
            var keyLength = ReadUInt16LittleEndian(data, offset);
            offset += 2;
            
            if (offset + 4 > data.Length)
                break;
            
            var valueLength = ReadUInt32LittleEndian(data, offset);
            offset += 4;
            
            if (offset + keyLength + (int)valueLength > data.Length)
                break;
            
            var keyBytes = new byte[keyLength];
            Array.Copy(data, offset, keyBytes, 0, keyLength);
            offset += keyLength;
            
            var valueBytes = new byte[valueLength];
            Array.Copy(data, offset, valueBytes, 0, (int)valueLength);
            offset += (int)valueLength;
            
            var keyStr = Encoding.UTF8.GetString(keyBytes);
            
            if (valueLength == 0)
            {
                _walCache.TryRemove(keyStr, out _);
            }
            else
            {
                _walCache[keyStr] = valueBytes;
            }
        }
    }
    
    private uint ReadUInt32LittleEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        return BitConverter.ToUInt32(bytes, 0);
    }
    
    private ushort ReadUInt16LittleEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(2);
        return BitConverter.ToUInt16(bytes, 0);
    }
    
    private ushort ReadUInt16LittleEndian(byte[] data, int offset)
    {
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }
    
    private uint ReadUInt32LittleEndian(byte[] data, int offset)
    {
        return BitConverter.ToUInt32(data, offset);
    }
    
    private int ReadVarInt32(BinaryReader reader)
    {
        int result = 0;
        int shift = 0;
        
        while (shift < 32)
        {
            var b = reader.ReadByte();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
        }
        
        return result;
    }
    
    public byte[]? Get(string key)
    {
        EnsureCacheBuilt();
        
        if (_walCache.TryGetValue(key, out var walValue))
        {
            if (walValue.Length == 0)
                return null;
            return walValue;
        }
        
        if (_cache.TryGetValue(key, out var value))
            return value;
        
        return null;
    }
    
    public byte[]? Get(byte[] key)
    {
        return Get(Encoding.UTF8.GetString(key));
    }
    
    public IEnumerable<KeyValuePair<string, byte[]>> Iterate()
    {
        EnsureCacheBuilt();
        
        var seen = new HashSet<string>();
        
        foreach (var kvp in _walCache)
        {
            if (kvp.Value.Length > 0 && seen.Add(kvp.Key))
            {
                yield return kvp;
            }
        }
        
        foreach (var kvp in _cache)
        {
            if (seen.Add(kvp.Key))
            {
                yield return kvp;
            }
        }
    }
    
    public IEnumerable<KeyValuePair<string, byte[]>> Iterate(byte[] prefix)
    {
        var prefixStr = Encoding.UTF8.GetString(prefix);
        return Iterate(prefixStr);
    }
    
    public IEnumerable<KeyValuePair<string, byte[]>> Iterate(string prefix)
    {
        EnsureCacheBuilt();
        
        var seen = new HashSet<string>();
        
        foreach (var kvp in _walCache)
        {
            if (kvp.Key.StartsWith(prefix) && kvp.Value.Length > 0 && seen.Add(kvp.Key))
            {
                yield return kvp;
            }
        }
        
        foreach (var kvp in _cache)
        {
            if (kvp.Key.StartsWith(prefix) && seen.Add(kvp.Key))
            {
                yield return kvp;
            }
        }
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;
    }
}
