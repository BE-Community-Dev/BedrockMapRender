namespace BedrockWorld.Chunk;

public enum ChunkRecordTag : byte
{
    Data3D = 0x2b,
    Version = 0x2c,
    Data2D = 0x2d,
    Data2DLegacy = 0x2e,
    SubChunkPrefix = 0x2f,
    LegacyTerrain = 0x30,
    BlockEntity = 0x31,
    Entity = 0x32,
    PendingTicks = 0x33,
    BlockExtraData = 0x34,
    BiomeState = 0x35,
    FinalizedState = 0x36,
    ConversionData = 0x37,
    BorderBlocks = 0x38,
    HardcodedSpawners = 0x39,
    RandomTicks = 0x3a,
    Checksums = 0x3b,
    GenerationSeed = 0x3c,
    GeneratedPreCavesAndCliffsBlending = 0x3d,
    BlendingBiomeHeight = 0x3e,
    MetaDataHash = 0x3f,
    BlendingData = 0x40,
    ActorDigestVersion = 0x41,
    VersionOld = 0x76,
    LegacyVersion = 0x77,
    Unknown = 0xff
}
