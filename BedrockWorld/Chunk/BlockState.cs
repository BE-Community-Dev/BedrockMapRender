using BedrockWorld.Nbt;

namespace BedrockWorld.Chunk;

public class BlockState
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, NbtTag> States { get; set; } = new Dictionary<string, NbtTag>();
    public int? Version { get; set; }
    
    public static BlockState FromNbt(NbtCompound compound)
    {
        var state = new BlockState();
        var nameTag = compound.Get<NbtString>("name") ?? compound.Get<NbtString>("Name");
        state.Name = nameTag?.Value ?? string.Empty;
        
        var statesTag = compound.Get<NbtCompound>("states") ?? compound.Get<NbtCompound>("States");
        if (statesTag != null)
        {
            state.States = new Dictionary<string, NbtTag>(statesTag.Tags);
        }
        
        var versionTag = compound.Get<NbtInt>("version") ?? compound.Get<NbtInt>("Version");
        state.Version = versionTag?.Value;
        
        return state;
    }
}
