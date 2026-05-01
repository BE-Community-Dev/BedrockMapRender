# Bedrock Render - C# Edition

[English](#english) | [简体中文](#chinese)

---

## English

`bedrock-render` 是 Minecraft Bedrock 世界地图渲染库的 C# 版本。它依赖 `bedrock-world` 查询 Bedrock LevelDB、NBT、chunk、subchunk、高度图和 biome 数据。渲染、调色板、图像处理独立在这个库中维护。

## 项目结构

```
src_cs/
├── BedrockLevelDB/     # Bedrock LevelDB 数据库访问
├── BedrockWorld/      # Bedrock 世界解析
├── BedrockRender/     # 地图渲染核心
└── BedrockRender.Console/  # 控制台示例程序
```

## 渲染模式

- `HeightMap`: 基于 Bedrock Data2D/Data3D 的高度渐变图
- `SurfaceBlocks`: 主俯视地形图。每个 X/Z 列从 Bedrock height map 开始，向下寻找最高可渲染方块，应用 biome tint，并把透明水体和下方实体方块混合
- `Biome`: 指定 Y 层的 biome 色彩图
- `LayerBlocks`: 指定世界 Y 层的方块平面图
- `CaveSlice`: 指定 Y 层洞穴诊断图

## API 示例

```csharp
using BedrockWorld;
using BedrockWorld.Chunk;
using BedrockRender;
using BedrockRender.Palette;
using SixLabors.ImageSharp;

// 加载世界
using var world = new BedrockWorld.BedrockWorld(worldPath);

// 加载调色板
var palette = RenderPalette.Load(blockColorPath, biomeColorPath);

// 创建渲染器
using var renderer = new MapRenderer(world, palette);

// 渲染地表方块图
using (var surfaceMap = renderer.RenderSurfaceBlocks(
    Dimension.Overworld,
    minChunkX, minChunkZ, maxChunkX, maxChunkZ
))
{
    surfaceMap.Save("surface-blocks.png");
}

// 渲染高度图
using (var heightMap = renderer.RenderHeightMap(
    Dimension.Overworld,
    minChunkX, minChunkZ, maxChunkX, maxChunkZ
))
{
    heightMap.Save("heightmap.png");
}

// 渲染生物群系图
using (var biomeMap = renderer.RenderBiome(
    Dimension.Overworld,
    minChunkX, minChunkZ, maxChunkX, maxChunkZ,
    layerY: 64
))
{
    biomeMap.Save("biomemap.png");
}

// 渲染指定 Y 层方块图
using (var layerMap = renderer.RenderLayerBlocks(
    Dimension.Overworld,
    minChunkX, minChunkZ, maxChunkX, maxChunkZ,
    layerY: 64
))
{
    layerMap.Save("layer-y64.png");
}

// 渲染洞穴切片
using (var caveMap = renderer.RenderCaveSlice(
    Dimension.Overworld,
    minChunkX, minChunkZ, maxChunkX, maxChunkZ,
    caveY: 32
))
{
    caveMap.Save("cave-y32.png");
}
```

## 使用控制台示例

运行示例程序需要先修改 `BedrockRender.Console` 项目中的 `Program.cs`，设置正确的世界路径，然后运行：

```bash
cd src_cs/BedrockRender.Console
dotnet run
```

示例程序会自动渲染所有模式的地图并保存到 output 文件夹。

## 渲染引擎

渲染器支持 CPU 和 GPU 两种渲染引擎：

- `RenderEngine.Auto`: 自动选择最佳引擎（优先 GPU 支持时优先 GPU）
- `RenderEngine.Cpu`: 纯 CPU 渲染
- `RenderEngine.Gpu`: GPU 加速渲染

检查 GPU 可用性：

```csharp
if (MapRenderer.IsGpuAvailable)
{
    // GPU 可用
}
```

## 依赖项

- .NET 8.0+
- SixLabors.ImageSharp

## 许可证

MIT License

---

## 中文

`bedrock-render` 是 Minecraft Bedrock 世界地图渲染库的 C# 版本。它依赖 `bedrock-world` 查询 Bedrock LevelDB、NBT、chunk、subchunk、高度图和 biome 数据。渲染、调色板、图像处理独立在这个库中维护。

## 项目结构

```
src_cs/
├── BedrockLevelDB/     # Bedrock LevelDB 数据库访问
├── BedrockWorld/      # Bedrock 世界解析
├── BedrockRender/     # 地图渲染核心
└── BedrockRender.Console/  # 控制台示例程序
```

## 渲染模式

- `HeightMap`: 基于 Bedrock Data2D/Data3D 的高度渐变图
- `SurfaceBlocks`: 主俯视地形图。每个 X/Z 列从 Bedrock height map 开始，向下寻找最高可渲染方块，应用 biome tint，并把透明水体和下方实体方块混合
- `Biome`: 指定 Y 层的 biome 色彩图
- `LayerBlocks`: 指定世界 Y 层的方块平面图
- `CaveSlice`: 指定 Y 层洞穴诊断图

## API 示例

```csharp
using BedrockWorld;
using BedrockWorld.Chunk;
using BedrockRender;
using BedrockRender.Palette;
using SixLabors.ImageSharp;

// 加载世界
using var world = new BedrockWorld.BedrockWorld(worldPath);

// 加载调色板
var palette = RenderPalette.Load(blockColorPath, biomeColorPath);

// 创建渲染器
using var renderer = new MapRenderer(world, palette);

// 渲染地表方块图
using (var surfaceMap = renderer.RenderSurfaceBlocks(
    Dimension.Overworld,
    minChunkX, minChunkZ, maxChunkX, maxChunkZ
))
{
    surfaceMap.Save("surface-blocks.png");
}

// 渲染高度图
using (var heightMap = renderer.RenderHeightMap(
    Dimension.Overworld,
    minChunkX, minChunkZ, maxChunkX, maxChunkZ
))
{
    heightMap.Save("heightmap.png");
}

// 渲染生物群系图
using (var biomeMap = renderer.RenderBiome(
    Dimension.Overworld,
    minChunkX, minChunkZ, maxChunkX, maxChunkZ,
    layerY: 64
))
{
    biomeMap.Save("biomemap.png");
}

// 渲染指定 Y 层方块图
using (var layerMap = renderer.RenderLayerBlocks(
    Dimension.Overworld,
    minChunkX, minChunkZ, maxChunkX, maxChunkZ,
    layerY: 64
))
{
    layerMap.Save("layer-y64.png");
}

// 渲染洞穴切片
using (var caveMap = renderer.RenderCaveSlice(
    Dimension.Overworld,
    minChunkX, minChunkZ, maxChunkX, maxChunkZ,
    caveY: 32
))
{
    caveMap.Save("cave-y32.png");
}
```

## 使用控制台示例

运行示例程序需要先修改 `BedrockRender.Console` 项目中的 `Program.cs`，设置正确的世界路径，然后运行：

```bash
cd src_cs/BedrockRender.Console
dotnet run
```

示例程序会自动渲染所有模式的地图并保存到 output 文件夹。

## 渲染引擎

渲染器支持 CPU 和 GPU 两种渲染引擎：

- `RenderEngine.Auto`: 自动选择最佳引擎（优先 GPU 支持时优先 GPU）
- `RenderEngine.Cpu`: 纯 CPU 渲染
- `RenderEngine.Gpu`: GPU 加速渲染检查 GPU 可用性：

```csharp
if (MapRenderer.IsGpuAvailable)
{
    // GPU 可用
}
```

## 依赖项

- .NET 8.0+
- SixLabors.ImageSharp

## 许可证

MIT License
