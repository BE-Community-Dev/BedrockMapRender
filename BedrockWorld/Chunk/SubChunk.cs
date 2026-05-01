using BedrockWorld.Nbt;

namespace BedrockWorld.Chunk;

public class SubChunk
{
    public sbyte Y { get; set; }
    public List<BlockPalette> Palettes { get; set; } = new List<BlockPalette>();
    
    public static SubChunk? Parse(sbyte y, byte[] data)
    {
        if (data == null || data.Length < 1)
            return null;

        var version = data[0];
        var subChunk = new SubChunk { Y = y };

        if (version == 1)
        {
            ParsePaletted(data, 2, 1, subChunk);
        }
        else if (version >= 8 && version <= 43)
        {
            var offset = 1;
            var storageCount = data[offset++];

            if (version >= 20)
            {
                offset++;
            }

            if (version == 9)
            {
                offset++;
            }

            if (version >= 28)
            {
                if (offset + 4 > data.Length)
                    return subChunk;
                var extraSize = BitConverter.ToInt32(data, offset);
                offset += 4 + extraSize;
            }

            ParsePaletted(data, offset, storageCount, subChunk);
        }

        return subChunk;
    }
    
    private static void ParsePaletted(byte[] data, int offset, int storageCount, SubChunk subChunk)
    {
        for (var i = 0; i < storageCount; i++)
        {
            if (offset >= data.Length)
                break;
            
            var header = data[offset++];
            var bitsPerBlock = header >> 1;
            
            if (bitsPerBlock == 0)
            {
                if (offset + 4 > data.Length)
                    break;
                var palLen = BitConverter.ToInt32(data, offset);
                offset += 4;

                var pal = new BlockPalette();

                for (var j = 0; j < palLen; j++)
                {
                    if (offset >= data.Length)
                        break;
                    
                    var memStream = new MemoryStream(data, offset, data.Length - offset);
                    var reader = new NbtReader(memStream);
                    var compound = reader.ReadRoot();
                    var bytesRead = (int)memStream.Position;
                    pal.States.Add(BlockState.FromNbt(compound));
                    
                    offset += bytesRead;
                }

                pal.Indices = new ushort[4096];
                Array.Fill(pal.Indices, (ushort)0);
                pal.Counts = new ushort[palLen];
                if (palLen > 0)
                    pal.Counts[0] = 4096;

                subChunk.Palettes.Add(pal);
                continue;
            }
            
            var wordCount = PackedWordCount(bitsPerBlock);
            var wordsByteLen = wordCount * 4;
            
            if (offset + wordsByteLen > data.Length)
                break;

            var words = new byte[wordsByteLen];
            Array.Copy(data, offset, words, 0, wordsByteLen);
            offset += wordsByteLen;

            if (offset + 4 > data.Length)
                break;

            var palLen2 = BitConverter.ToInt32(data, offset);
            offset += 4;
            
            var pal2 = new BlockPalette();
            
            for (var j = 0; j < palLen2; j++)
            {
                if (offset >= data.Length)
                    break;
                
                try
                {
                    var memStream2 = new MemoryStream(data, offset, data.Length - offset);
                    var reader2 = new NbtReader(memStream2);
                    var compound2 = reader2.ReadRoot();
                    var bytesRead2 = (int)memStream2.Position;
                    pal2.States.Add(BlockState.FromNbt(compound2));
                    
                    offset += bytesRead2;
                }
                catch
                {
                    break;
                }
            }
            
            pal2.Indices = UnpackPaletteIndices(words, bitsPerBlock, palLen2);
            pal2.Counts = new ushort[palLen2];
            
            if (pal2.Indices != null)
            {
                foreach (var idx in pal2.Indices)
                {
                    if (idx < pal2.Counts.Length)
                        pal2.Counts[idx]++;
                }
            }
            
            subChunk.Palettes.Add(pal2);
        }
    }
    
    private static int EstimateNbtSize(NbtTag tag)
    {
        if (tag == null)
            return 0;
        
        switch (tag.Type)
        {
            case NbtTagType.End:
                return 1;
            case NbtTagType.Byte:
                return 2;
            case NbtTagType.Short:
                return 3;
            case NbtTagType.Int:
                return 5;
            case NbtTagType.Long:
                return 9;
            case NbtTagType.Float:
                return 5;
            case NbtTagType.Double:
                return 9;
            case NbtTagType.ByteArray:
                var ba = (NbtByteArray)tag;
                return 5 + ba.Value.Length;
            case NbtTagType.String:
                var s = (NbtString)tag;
                return 3 + s.Value.Length;
            case NbtTagType.List:
                var list = (NbtList)tag;
                return 5 + list.Tags.Sum(EstimateNbtSize);
            case NbtTagType.Compound:
                var comp = (NbtCompound)tag;
                var size = 1;
                foreach (var kvp in comp.Tags)
                {
                    size += 3 + kvp.Key.Length;
                    size += EstimateNbtSize(kvp.Value);
                }
                size += 1;
                return size;
            case NbtTagType.IntArray:
                var ia = (NbtIntArray)tag;
                return 5 + ia.Value.Length * 4;
            case NbtTagType.LongArray:
                var la = (NbtLongArray)tag;
                return 5 + la.Value.Length * 8;
            default:
                return 10;
        }
    }
    
    private static int PackedWordCount(int bitsPerBlock)
    {
        if (bitsPerBlock == 0)
            return 0;
        
        var valuesPerWord = 32 / bitsPerBlock;
        return (4096 + valuesPerWord - 1) / valuesPerWord;
    }
    
    private static ushort[]? UnpackPaletteIndices(byte[] words, int bitsPerBlock, int paletteLen)
    {
        if (bitsPerBlock == 0)
        {
            var result = new ushort[4096];
            Array.Fill(result, (ushort)0);
            return result;
        }
        
        if (bitsPerBlock >= 16)
        {
            var indices = new ushort[4096];
            var valuesPerWord = 2;
            var mask = 0xFFFF;
            
            for (var i = 0; i < words.Length / 4 && i * valuesPerWord < 4096; i++)
            {
                var word = BitConverter.ToUInt32(words, i * 4);
                for (var j = 0; j < valuesPerWord && (i * valuesPerWord + j) < 4096; j++)
                {
                    indices[i * valuesPerWord + j] = (ushort)((word >> (j * 16)) & mask);
                }
            }
            
            return indices;
        }
        
        var result2 = new ushort[4096];
        var vpw = 32 / bitsPerBlock;
        var mask2 = (1u << bitsPerBlock) - 1;
        var index = 0;
        
        for (var i = 0; i < words.Length; i += 4)
        {
            if (index >= 4096)
                break;
            
            var word = BitConverter.ToUInt32(words, i);
            for (var j = 0; j < vpw; j++)
            {
                if (index >= 4096)
                    break;
                
                result2[index++] = (ushort)((word >> (j * bitsPerBlock)) & mask2);
            }
        }
        
        return result2;
    }
    
    public BlockState? BlockStateAt(int localX, int localY, int localZ)
    {
        if (Palettes.Count == 0)
            return null;
        
        return Palettes[0].BlockStateAt(localX, localY, localZ);
    }
}