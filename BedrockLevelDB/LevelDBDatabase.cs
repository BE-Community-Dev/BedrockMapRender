using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace BedrockLevelDB;

public class LevelDBDatabase : IDisposable
{
    private readonly string _path;
    private readonly Dictionary<byte[], byte[]> _cache = new Dictionary<byte[], byte[]>(new ByteArrayComparer());
    private readonly ConcurrentDictionary<byte[], byte[]> _walCache = new ConcurrentDictionary<byte[], byte[]>(new ByteArrayComparer());
    private bool _disposed;
    private bool _cacheBuilt;
    
    private const int BlockSize = 32 * 1024;
    private const int HeaderSize = 7;
    private const ulong TableMagic = 0xdb47_7524_8b80_fb57u;
    private const int FooterSize = 48;
    private const int BlockTrailerSize = 5;
    
    private const byte ValueTypeDeletion = 0;
    private const byte ValueTypeValue = 1;
    
    private const byte ZeroType = 0;
    private const byte FullType = 1;
    private const byte FirstType = 2;
    private const byte MiddleType = 3;
    private const byte LastType = 4;
    
    public LevelDBDatabase(string path)
    {
        _path = path;
    }
    
    private void EnsureCacheBuilt()
    {
        if (_cacheBuilt)
            return;
        
        if (!Directory.Exists(_path))
        {
            _cacheBuilt = true;
            return;
        }
        
        foreach (var file in Directory.GetFiles(_path, "*.ldb"))
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
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length < FooterSize)
                return;
            
            var fileBytes = File.ReadAllBytes(filePath);
            
            var footerOffset = fileBytes.Length - FooterSize;
            var footer = new byte[FooterSize];
            Array.Copy(fileBytes, footerOffset, footer, 0, FooterSize);
            
            var magic = BitConverter.ToInt64(footer, FooterSize - 8);
            if ((ulong)magic != TableMagic)
            {
                return;
            }
            
            var offset = 0;
            var metaIndexHandle = ReadBlockHandle(footer, ref offset);
            var indexHandle = ReadBlockHandle(footer, ref offset);
            
            var indexBlock = ReadBlock(fileBytes, indexHandle);
            var indexEntries = DecodeBlock(indexBlock);
            
            foreach (var (internalKey, value) in indexEntries)
            {
                var (userKey, isValue) = SplitInternalKey(internalKey);
                if (userKey == null || !isValue)
                    continue;
                
                var tempOffset = 0;
                var dataHandle = ReadBlockHandle(value, ref tempOffset);
                var dataBlock = ReadBlock(fileBytes, dataHandle);
                var dataEntries = DecodeBlock(dataBlock);
                
                foreach (var (key, val) in dataEntries)
                {
                    var (uk, iv) = SplitInternalKey(key);
                    if (uk == null || !iv)
                        continue;
                    
                    _cache[uk] = val.ToArray();
                }
            }
        }
        catch
        {
        }
    }
    
    private (uint offset, uint size) ReadBlockHandle(byte[] data, ref int offset)
    {
        var (offsetVal, consumed1) = ReadVarInt64(data, ref offset);
        var (size, consumed2) = ReadVarInt64(data, ref offset);
        return ((uint)offsetVal, (uint)size);
    }
    
    private byte[] ReadBlock(byte[] fileBytes, (uint offset, uint size) handle)
    {
        var offset = (int)handle.offset;
        var size = (int)handle.size;
        
        if (offset + size + BlockTrailerSize > fileBytes.Length)
            return Array.Empty<byte>();
        
        var payload = new byte[size];
        Array.Copy(fileBytes, offset, payload, 0, size);
        var compression = fileBytes[offset + size];
        
        return compression switch
        {
            0 => payload,
            1 => DecompressSnappy(payload),
            2 => DecompressZlib(payload),
            4 => DecompressDeflate(payload),
            _ => payload
        };
    }
    
    private List<(byte[], byte[])> DecodeBlock(byte[] block)
    {
        var entries = new List<(byte[], byte[])>();
        
        if (block.Length < 4)
            return entries;
        
        var restartOffset = block.Length - 4;
        var numRestarts = BitConverter.ToInt32(block, restartOffset);
        
        if (numRestarts < 0 || numRestarts > 10000)
            return entries;
        
        var dataEnd = restartOffset - numRestarts * 4;
        var offset = 0;
        var lastKey = new byte[0];
        
        for (var i = 0; i < numRestarts && offset < dataEnd; i++)
        {
            var restartPos = BitConverter.ToInt32(block, restartOffset + 4 + i * 4);
            if (restartPos < offset || restartPos >= dataEnd)
                break;
            
            offset = restartPos;
            
            while (offset < dataEnd)
            {
                var (shared, nShared, nNonShared, vLen) = DecodeEntryHeader(block, offset);
                
                if (offset + nNonShared + vLen > dataEnd)
                    break;
                
                byte[] key;
                if (shared > 0 && lastKey.Length >= shared)
                {
                    key = new byte[shared + nNonShared];
                    Array.Copy(lastKey, 0, key, 0, shared);
                }
                else
                {
                    key = new byte[nNonShared];
                }
                
                Array.Copy(block, offset, key, shared, nNonShared);
                offset += nNonShared;
                
                var value = new byte[vLen];
                Array.Copy(block, offset, value, 0, vLen);
                offset += vLen;
                
                entries.Add((key, value));
                lastKey = key;
            }
        }
        
        return entries;
    }
    
    private (int shared, int nonShared, int valueLen, int nextOffset) DecodeEntryHeader(byte[] data, int offset)
    {
        var (shared, s1) = ReadVarInt32(data, ref offset);
        var (nonShared, s2) = ReadVarInt32(data, ref offset);
        var (valueLen, s3) = ReadVarInt32(data, ref offset);
        return (shared, nonShared, valueLen, offset);
    }
    
    private (int value, int consumed) ReadVarInt32(byte[] data, ref int offset)
    {
        int result = 0;
        int shift = 0;
        int startOffset = offset;
        
        while (offset < data.Length)
        {
            var b = data[offset++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return (result, offset - startOffset);
            shift += 7;
        }
        
        return (result, offset - startOffset);
    }
    
    private (int value, int consumed) ReadVarInt64(byte[] data, ref int offset)
    {
        int result = 0;
        int shift = 0;
        int startOffset = offset;
        
        while (offset < data.Length)
        {
            var b = data[offset++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return (result, offset - startOffset);
            shift += 7;
        }
        
        return (result, offset - startOffset);
    }
    
    private (byte[]? userKey, bool isValue) SplitInternalKey(byte[] internalKey)
    {
        if (internalKey.Length < 8)
            return (null, false);
        
        var userKey = new byte[internalKey.Length - 8];
        Array.Copy(internalKey, userKey, internalKey.Length - 8);
        
        var tag = BitConverter.ToInt64(internalKey, internalKey.Length - 8);
        var valueType = (int)(tag & 0xff);
        
        return (userKey, valueType == 1);
    }
    
    private byte[] DecompressSnappy(byte[] input)
    {
        if (input.Length == 0)
            return Array.Empty<byte>();
        
        try
        {
            var result = new List<byte>();
            var offset = 0;
            
            while (offset < input.Length)
            {
                var b = input[offset++];
                
                if ((b & 0x03) == 0)
                {
                    var length = (b >> 2) + 1;
                    if (offset + length > input.Length)
                        break;
                    
                    for (var i = 0; i < length; i++)
                        result.Add(input[offset++]);
                }
                else if ((b & 0x03) == 0x01)
                {
                    var length = ((b >> 2) & 0x07) + 4;
                    if (offset + 1 > input.Length)
                        break;
                    
                    var copyOffset = input[offset++] | ((b >> 5) << 8);
                    var copyStart = result.Count - copyOffset - 1;
                    
                    for (var i = 0; i < length; i++)
                    {
                        if (copyStart + i < 0 || copyStart + i >= result.Count)
                            break;
                        result.Add(result[copyStart + i]);
                    }
                }
                else if ((b & 0x03) == 0x02)
                {
                    var length = (b >> 2) + 1;
                    if (offset + 2 > input.Length)
                        break;
                    
                    var copyOffset = input[offset++] | (input[offset++] << 8);
                    var copyStart = result.Count - copyOffset - 1;
                    
                    for (var i = 0; i < length; i++)
                    {
                        if (copyStart + i < 0 || copyStart + i >= result.Count)
                            break;
                        result.Add(result[copyStart + i]);
                    }
                }
                else
                {
                    var length = ((b >> 2) & 0x1f) + 1;
                    if (offset + 3 > input.Length)
                        break;
                    
                    var copyOffset = input[offset++] | (input[offset++] << 8) | (input[offset++] << 16);
                    var copyStart = result.Count - copyOffset - 1;
                    
                    for (var i = 0; i < length; i++)
                    {
                        if (copyStart + i < 0 || copyStart + i >= result.Count)
                            break;
                        result.Add(result[copyStart + i]);
                    }
                }
            }
            
            return result.ToArray();
        }
        catch
        {
            return input;
        }
    }
    
    private byte[] DecompressZlib(byte[] input)
    {
        try
        {
            using var ms = new MemoryStream(input);
            using var zlib = new DeflateStream(ms, CompressionMode.Decompress);
            using var result = new MemoryStream();
            zlib.CopyTo(result);
            return result.ToArray();
        }
        catch
        {
            return input;
        }
    }
    
    private byte[] DecompressDeflate(byte[] input)
    {
        return DecompressZlib(input);
    }
    
    private void ReadWalFiles()
    {
        if (!Directory.Exists(_path))
            return;
        
        var logFiles = Directory.GetFiles(_path, "*.log");
        Array.Sort(logFiles);
        
        foreach (var file in logFiles)
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
            var fileBytes = File.ReadAllBytes(filePath);
            var pos = 0;
            var scratch = new List<byte>();
            
            while (pos + HeaderSize <= fileBytes.Length)
            {
                var blockOffset = pos % BlockSize;
                if (BlockSize - blockOffset < HeaderSize)
                {
                    pos += BlockSize - blockOffset;
                    continue;
                }
                
                var checksum = BitConverter.ToUInt32(fileBytes, pos);
                var length = BitConverter.ToUInt16(fileBytes, pos + 4);
                var recordType = fileBytes[pos + 6];
                pos += HeaderSize;
                
                if (recordType == ZeroType && length == 0)
                    break;
                
                if (pos + length > fileBytes.Length)
                    break;
                
                var payload = new byte[length];
                Array.Copy(fileBytes, pos, payload, 0, length);
                pos += length;
                
                switch (recordType)
                {
                    case FullType:
                        ParseWriteBatch(payload);
                        break;
                    case FirstType:
                        scratch.Clear();
                        scratch.AddRange(payload);
                        break;
                    case MiddleType:
                        scratch.AddRange(payload);
                        break;
                    case LastType:
                        scratch.AddRange(payload);
                        var record = scratch.ToArray();
                        ParseWriteBatch(record);
                        scratch.Clear();
                        break;
                }
            }
        }
        catch
        {
        }
    }
    
    private void ParseWriteBatch(byte[] data)
    {
        if (data.Length < 12)
            return;
        
        var sequence = BitConverter.ToUInt64(data, 0);
        var count = BitConverter.ToInt32(data, 8);
        
        var offset = 12;
        
        for (var i = 0; i < count && offset < data.Length; i++)
        {
            var tag = data[offset++];
            
            if (tag == ValueTypeValue)
            {
                var (keyLen, _) = ReadVarInt32(data, ref offset);
                if (offset + keyLen > data.Length)
                    break;
                
                var keyBytes = new byte[keyLen];
                Array.Copy(data, offset, keyBytes, 0, keyLen);
                offset += keyLen;
                
                var (valueLen, _) = ReadVarInt32(data, ref offset);
                if (offset + valueLen > data.Length)
                    break;
                
                var valueBytes = new byte[valueLen];
                Array.Copy(data, offset, valueBytes, 0, valueLen);
                offset += valueLen;
                
                _walCache[keyBytes] = valueBytes;
            }
            else if (tag == ValueTypeDeletion)
            {
                var (keyLen, _) = ReadVarInt32(data, ref offset);
                if (offset + keyLen > data.Length)
                    break;
                
                var keyBytes = new byte[keyLen];
                Array.Copy(data, offset, keyBytes, 0, keyLen);
                offset += keyLen;
                
                _walCache.TryRemove(keyBytes, out _);
            }
        }
    }
    
    public byte[]? Get(byte[] key)
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
    
    public IEnumerable<KeyValuePair<byte[], byte[]>> Iterate()
    {
        EnsureCacheBuilt();
        
        var seen = new HashSet<byte[]>(new ByteArrayComparer());
        
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
    
    public IEnumerable<KeyValuePair<byte[], byte[]>> Iterate(byte[] prefix)
    {
        EnsureCacheBuilt();
        
        var seen = new HashSet<byte[]>(new ByteArrayComparer());
        
        foreach (var kvp in _walCache)
        {
            if (StartsWith(kvp.Key, prefix) && kvp.Value.Length > 0 && seen.Add(kvp.Key))
            {
                yield return kvp;
            }
        }
        
        foreach (var kvp in _cache)
        {
            if (StartsWith(kvp.Key, prefix) && seen.Add(kvp.Key))
            {
                yield return kvp;
            }
        }
    }
    
    private bool StartsWith(byte[] data, byte[] prefix)
    {
        if (data.Length < prefix.Length)
            return false;
        
        for (var i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i])
                return false;
        }
        
        return true;
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;
    }
    
    private class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            if (x.Length != y.Length) return false;
            
            for (var i = 0; i < x.Length; i++)
            {
                if (x[i] != y[i]) return false;
            }
            
            return true;
        }
        
        public int GetHashCode(byte[] obj)
        {
            if (obj == null) return 0;
            
            var hash = 17;
            foreach (var b in obj)
            {
                hash = hash * 31 + b;
            }
            
            return hash;
        }
    }
}