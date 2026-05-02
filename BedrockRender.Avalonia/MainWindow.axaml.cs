using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BedrockRender.Palette;
using BedrockWorld;
using BedrockWorld.Chunk;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AvaloniaImage = Avalonia.Controls.Image;
using AvaloniaPoint = Avalonia.Point;

namespace BedrockRender.Avalonia;

public partial class MainWindow : Window, IDisposable
{
    private StreamingWorld? _streamingWorld;
    private StreamingMapRenderer? _streamingRenderer;
    private BedrockWorld.BedrockWorld? _world;
    private MapRenderer? _renderer;
    private Image<Rgba32>? _currentImage;
    private WriteableBitmap? _writeableBitmap;
    private Bitmap? _avaloniaBitmap;
    private readonly Dictionary<(int X, int Y), (WriteableBitmap Bitmap, AvaloniaImage Image)> _bitmapTiles = new();
    private List<string> _recentFolders = new();
    private const int MaxRecentFolders = 10;

    private ScaleTransform _scaleTransform = new();
    private TranslateTransform _translateTransform = new();
    private TransformGroup _transformGroup = new();
    private bool _isDragging;
    private AvaloniaPoint _dragStartPoint;
    private double _currentScale = 1.0;
    private const double MinScale = 0.1;
    private const double MaxScale = 10.0;
    private AvaloniaPoint _offset = new(0, 0);
    private DispatcherTimer? _coordinateTimer;
    private CancellationTokenSource? _renderCancellation;
    private bool _isRendering;

    private int _minChunkX = -64;
    private int _minChunkZ = -64;
    private int _maxChunkX = 64;
    private int _maxChunkZ = 64;
    private List<ChunkPos>? _cachedChunkList;

    private readonly object _imageLock = new();
    private uint[]? _currentPixelBuffer;
    private int _currentPixelBufferLength;
    private int _currentWidth;
    private int _currentHeight;
    private RenderMode _currentRenderMode = RenderMode.SurfaceBlocks;
    private int _currentLayerY = 64;
    private Dimension _currentDimension = Dimension.Overworld;

    private DispatcherTimer? _renderThrottleTimer;
    private bool _pendingRenderRequest;

    private readonly object _updateLock = new();
    private bool _imageUpdateNeeded;
    private DispatcherTimer? _imageUpdateTimer;
    private int _dirtyMinX = int.MaxValue;
    private int _dirtyMinY = int.MaxValue;
    private int _dirtyMaxX = -1;
    private int _dirtyMaxY = -1;
    private bool _imageUpdateInProgress;
    private const int BitmapTileSize = 4096;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTransforms();
        InitializeEvents();
        LoadRecentFolders();
        InitializeCoordinateTimer();
        InitializeRenderThrottleTimer();
        InitializeImageUpdateTimer();
    }

    private void InitializeTransforms()
    {
        _transformGroup.Children.Add(_scaleTransform);
        _transformGroup.Children.Add(_translateTransform);
        MapCanvas.RenderTransform = _transformGroup;
    }

    private void InitializeEvents()
    {
        SelectFolderButton.Click += SelectFolderButton_Click;
        OpenFolderMenuItem.Click += OpenFolderMenuItem_Click;
        ExitMenuItem.Click += (_, _) => Close();
        ZoomInMenuItem.Click += (_, _) => ZoomIn();
        ZoomOutMenuItem.Click += (_, _) => ZoomOut();
        ResetViewMenuItem.Click += (_, _) => ResetView();
        RenderModeComboBox.SelectionChanged += RenderModeComboBox_SelectionChanged;
        OverworldRadio.IsCheckedChanged += DimensionRadio_Checked;
        NetherRadio.IsCheckedChanged += DimensionRadio_Checked;
        EndRadio.IsCheckedChanged += DimensionRadio_Checked;
        LayerYSlider.ValueChanged += LayerYSlider_ValueChanged;

        MapContainer.PointerPressed += MapContainer_PointerPressed;
        MapContainer.PointerMoved += MapContainer_PointerMoved;
        MapContainer.PointerReleased += MapContainer_PointerReleased;
        MapContainer.PointerWheelChanged += MapContainer_PointerWheelChanged;
        MapContainer.PointerExited += MapContainer_PointerLeave;
    }

    private void InitializeCoordinateTimer()
    {
        _coordinateTimer = new DispatcherTimer();
        _coordinateTimer.Interval = TimeSpan.FromMilliseconds(100);
        _coordinateTimer.Tick += CoordinateTimer_Tick;
        _coordinateTimer.Start();
    }

    private void InitializeRenderThrottleTimer()
    {
        _renderThrottleTimer = new DispatcherTimer();
        _renderThrottleTimer.Interval = TimeSpan.FromMilliseconds(100);
        _renderThrottleTimer.Tick += RenderThrottleTimer_Tick;
        _renderThrottleTimer.Start();
    }

    private void InitializeImageUpdateTimer()
    {
        _imageUpdateTimer = new DispatcherTimer();
        _imageUpdateTimer.Interval = TimeSpan.FromMilliseconds(100);
        _imageUpdateTimer.Tick += ImageUpdateTimer_Tick;
        _imageUpdateTimer.Start();
    }

    private async void SelectFolderButton_Click(object? sender, EventArgs e)
    {
        await OpenFolder();
    }

    private async void OpenFolderMenuItem_Click(object? sender, EventArgs e)
    {
        await OpenFolder();
    }

    private async Task OpenFolder()
    {
        var storageProvider = StorageProvider;
        if (storageProvider == null)
            return;

        var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择存档文件夹",
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            await LoadWorld(result[0].Path.LocalPath);
        }
    }

    private async Task LoadWorld(string folderPath)
    {
        try
        {
            StatusText.Text = "正在加载世界...";

            _renderCancellation?.Cancel();
            _renderer?.Dispose();
            _streamingRenderer?.Dispose();
            _world?.Dispose();
            _streamingWorld?.Dispose();
            ReleaseImageResources(returnPixelBuffer: true);

            _world = new BedrockWorld.BedrockWorld(folderPath);
            _streamingWorld = new StreamingWorld(folderPath);

            var basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "colors");
            var blockColorPath = Path.Combine(basePath, "bedrock-block-color.json");
            var biomeColorPath = Path.Combine(basePath, "bedrock-biome-color.json");
            var palette = RenderPalette.Load(blockColorPath, biomeColorPath);

            _renderer = new MapRenderer(_world, palette);
            _streamingRenderer = new StreamingMapRenderer(_streamingWorld, palette);

            AddToRecentFolders(folderPath);
            CurrentFolderText.Text = folderPath;
            NoImageText.IsVisible = false;

            StatusText.Text = "正在扫描地图边界...";
            var chunks = _streamingWorld.ListChunkPositions(_currentDimension);
            _cachedChunkList = chunks;
            if (chunks.Count > 0)
            {
                _minChunkX = chunks.Min(c => c.X);
                _minChunkZ = chunks.Min(c => c.Z);
                _maxChunkX = chunks.Max(c => c.X);
                _maxChunkZ = chunks.Max(c => c.Z);
                var width = ((long)_maxChunkX - _minChunkX + 1) * 16;
                var height = ((long)_maxChunkZ - _minChunkZ + 1) * 16;
                var memoryMb = width * height * sizeof(uint) / 1024d / 1024d;
                StatusText.Text =
                    $"发现地图: {_minChunkX}..{_maxChunkX}, {_minChunkZ}..{_maxChunkZ} ({chunks.Count} 个区块, {width}x{height}, 缓冲约 {memoryMb:F1} MB)";
            }

            await Task.Delay(500);
            await StartStreamingRender();

            StatusText.Text = "世界加载成功";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载失败: {ex.Message}";
        }
    }

    private void AddToRecentFolders(string folderPath)
    {
        _recentFolders.Remove(folderPath);
        _recentFolders.Insert(0, folderPath);
        if (_recentFolders.Count > MaxRecentFolders)
            _recentFolders.RemoveAt(_recentFolders.Count - 1);
        SaveRecentFolders();
        UpdateRecentFoldersMenu();
    }

    private void LoadRecentFolders()
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BedrockRender",
            "config.txt");

        if (File.Exists(configPath))
        {
            _recentFolders = File.ReadAllLines(configPath)
                .Where(l => !string.IsNullOrEmpty(l))
                .Take(MaxRecentFolders)
                .ToList();
        }

        UpdateRecentFoldersMenu();
    }

    private void SaveRecentFolders()
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BedrockRender");
        Directory.CreateDirectory(configDir);

        var configPath = Path.Combine(configDir, "config.txt");
        File.WriteAllLines(configPath, _recentFolders);
    }

    private void UpdateRecentFoldersMenu()
    {
        RecentFoldersMenuItem.Items.Clear();

        if (_recentFolders.Count == 0)
        {
            var item = new MenuItem { Header = "无最近文件夹", IsEnabled = false };
            RecentFoldersMenuItem.Items.Add(item);
            return;
        }

        foreach (var folderPath in _recentFolders)
        {
            var item = new MenuItem { Header = Path.GetFileName(folderPath), Tag = folderPath };
            item.Click += async (s, e) =>
            {
                if (s is MenuItem mi && mi.Tag is string path)
                {
                    await LoadWorld(path);
                }
            };
            RecentFoldersMenuItem.Items.Add(item);
        }
    }

    private void RenderModeComboBox_SelectionChanged(object? sender, EventArgs e)
    {
        if (RenderModeComboBox.SelectedIndex >= 0)
        {
            _currentRenderMode = (RenderMode)RenderModeComboBox.SelectedIndex;
            LayerYLabel.IsVisible = _currentRenderMode >= RenderMode.LayerBlocks;
            LayerYSlider.IsVisible = _currentRenderMode >= RenderMode.LayerBlocks;
            LayerYValue.IsVisible = _currentRenderMode >= RenderMode.LayerBlocks;
            RequestRender();
        }
    }

    private void DimensionRadio_Checked(object? sender, EventArgs e)
    {
        if (OverworldRadio.IsChecked == true)
            _currentDimension = Dimension.Overworld;
        else if (NetherRadio.IsChecked == true)
            _currentDimension = Dimension.Nether;
        else if (EndRadio.IsChecked == true)
            _currentDimension = Dimension.End;

        if (_streamingWorld != null)
        {
            StatusText.Text = "正在扫描地图边界...";
            var chunks = _streamingWorld.ListChunkPositions(_currentDimension);
            _cachedChunkList = chunks;
            if (chunks.Count > 0)
            {
                _minChunkX = chunks.Min(c => c.X);
                _minChunkZ = chunks.Min(c => c.Z);
                _maxChunkX = chunks.Max(c => c.X);
                _maxChunkZ = chunks.Max(c => c.Z);
                var width = ((long)_maxChunkX - _minChunkX + 1) * 16;
                var height = ((long)_maxChunkZ - _minChunkZ + 1) * 16;
                var memoryMb = width * height * sizeof(uint) / 1024d / 1024d;
                StatusText.Text =
                    $"发现地图: {_minChunkX}..{_maxChunkX}, {_minChunkZ}..{_maxChunkZ} ({chunks.Count} 个区块, {width}x{height}, 缓冲约 {memoryMb:F1} MB)";
            }
        }

        RequestRender();
    }

    private void LayerYSlider_ValueChanged(object? sender, EventArgs e)
    {
        if (LayerYValue != null)
        {
            LayerYValue.Text = ((int)LayerYSlider.Value).ToString();
            _currentLayerY = (int)LayerYSlider.Value;
            RequestRender();
        }
    }

    private void RequestRender()
    {
        _pendingRenderRequest = true;
    }

    private async void RenderThrottleTimer_Tick(object? sender, EventArgs e)
    {
        if (_pendingRenderRequest && !_isRendering)
        {
            _pendingRenderRequest = false;
            await StartStreamingRender();
        }
    }

    private async Task StartStreamingRender()
    {
        if (_streamingRenderer == null || _streamingWorld == null)
            return;

        // 1. 如果正在渲染，取消它并请求重排
        if (_isRendering)
        {
            _renderCancellation?.Cancel();
            _pendingRenderRequest = true;
            return;
        }

        _isRendering = true;
        _renderCancellation = new CancellationTokenSource();
        var token = _renderCancellation.Token;

        try
        {
            // 预计算尺寸
            var widthLong = ((long)_maxChunkX - _minChunkX + 1) * 16;
            var heightLong = ((long)_maxChunkZ - _minChunkZ + 1) * 16;
            var pixelCount = widthLong * heightLong;

            // 限制最大分配，防止因为地图过大直接分配几个 G 的数组
            if (pixelCount > 512 * 1024 * 1024) // 限制为 512M 像素 (约 2GB RAM)
            {
                StatusText.Text = "地图范围过大，超出了当前内存优化限制。";
                return;
            }

            lock (_imageLock)
            {
                EnsurePixelBuffer((int)pixelCount);
                Array.Clear(_currentPixelBuffer!, 0, (int)pixelCount);
                _currentWidth = (int)widthLong;
                _currentHeight = (int)heightLong;
            }

            // 重置脏矩形
            lock (_updateLock)
            {
                _dirtyMinX = 0;
                _dirtyMinY = 0;
                _dirtyMaxX = _currentWidth - 1;
                _dirtyMaxY = _currentHeight - 1;
                _imageUpdateNeeded = true;
            }

            // 2. 挂载事件
            _streamingRenderer.ProgressChanged += OnRenderProgressChanged;
            _streamingRenderer.ChunkRendered += OnChunkRendered;

            // 3. 执行异步渲染
            await _streamingRenderer.RenderChunksProgressiveAsync(
                _currentDimension,
                _minChunkX, _minChunkZ, _maxChunkX, _maxChunkZ,
                _cachedChunkList,
                -64, 320,
                _currentLayerY,
                _currentRenderMode,
                token);

            StatusText.Text = token.IsCancellationRequested ? "渲染已取消" : "渲染完成";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"渲染异常: {ex.Message}";
        }
        finally
        {
            // 4. 重要：卸载事件，防止内存泄漏和重复调用
            if (_streamingRenderer != null)
            {
                _streamingRenderer.ProgressChanged -= OnRenderProgressChanged;
                _streamingRenderer.ChunkRendered -= OnChunkRendered;
            }

            _isRendering = false;

            // 如果渲染中途有新的请求，再次触发
            if (_pendingRenderRequest)
            {
                _pendingRenderRequest = false;
                RequestRender();
            }
        }
    }

    private void OnRenderProgressChanged(RenderProgress progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text =
                $"渲染中... {progress.RenderedChunks}/{progress.TotalChunks} ({progress.ProgressPercent:F1}%)";
        });
    }

    private void OnChunkRendered(ChunkRenderResult result)
    {
        // 关键修复：使用 using 确保在方法结束时执行 result.Dispose() 从而归还 ArrayPool
        using (result)
        {
            try
            {
                // 安全检查：如果窗口已关闭或缓冲区已释放则跳过
                if (_currentPixelBuffer == null)
                    return;

                var pos = result.Position;
                // 计算当前 Chunk 在总图像缓冲中的起始偏移坐标
                var offsetX = (pos.X - _minChunkX) * 16;
                var offsetZ = (pos.Z - _minChunkZ) * 16;
                var copiedAny = false;

                lock (_imageLock)
                {
                    // 二次检查缓冲区有效性
                    if (_currentPixelBuffer == null) return;

                    // 逐行将 Chunk 的 16x16 像素数据拷贝到主像素缓冲区
                    for (var z = 0; z < 16; z++)
                    {
                        var imgZ = offsetZ + z;

                        // 边界安全检查，防止坐标越界导致崩溃
                        if (imgZ < 0 || imgZ >= _currentHeight) continue;
                        if (offsetX < 0 || offsetX + 16 > _currentWidth) continue;

                        var srcOffset = z * 16;
                        var dstOffset = imgZ * _currentWidth + offsetX;

                        // 使用高效的 Array.Copy 代替逐个循环赋值，降低 CPU 开销
                        Array.Copy(result.PixelData, srcOffset, _currentPixelBuffer, dstOffset, 16);
                        copiedAny = true;
                    }
                }

                // 如果有像素写入，更新脏矩形区域以触发 UI 刷新
                if (copiedAny)
                {
                    lock (_updateLock)
                    {
                        _dirtyMinX = Math.Min(_dirtyMinX, offsetX);
                        _dirtyMinY = Math.Min(_dirtyMinY, offsetZ);
                        _dirtyMaxX = Math.Max(_dirtyMaxX, offsetX + 15);
                        _dirtyMaxY = Math.Max(_dirtyMaxY, offsetZ + 15);
                        _imageUpdateNeeded = true;
                    }
                }
            }
            catch (Exception ex)
            {
                // 捕获异步流水线中的异常，避免因单个 Chunk 错误导致整个程序崩溃
                System.Diagnostics.Debug.WriteLine($"[Render Error] Chunk {result.Position}: {ex.Message}");
            }
        } // 此处 result 自动销毁，内部数组通过 ArrayPool.Return 回收
    }

    private async void ImageUpdateTimer_Tick(object? sender, EventArgs e)
    {
        bool needsUpdate;
        lock (_updateLock)
        {
            needsUpdate = _imageUpdateNeeded;
            if (!needsUpdate || _imageUpdateInProgress)
                return;

            _imageUpdateInProgress = true;
        }

        try
        {
            await UpdateImageFromBufferAsync();
        }
        finally
        {
            lock (_updateLock)
            {
                _imageUpdateInProgress = false;
            }
        }
    }

    private async Task UpdateImageFromBufferAsync()
    {
        int width = 0, height = 0;
        int dirtyMinX, dirtyMinY, dirtyMaxX, dirtyMaxY;

        lock (_updateLock)
        {
            if (!_imageUpdateNeeded || _dirtyMaxX < _dirtyMinX || _dirtyMaxY < _dirtyMinY)
                return;

            dirtyMinX = _dirtyMinX;
            dirtyMinY = _dirtyMinY;
            dirtyMaxX = _dirtyMaxX;
            dirtyMaxY = _dirtyMaxY;

            _dirtyMinX = int.MaxValue;
            _dirtyMinY = int.MaxValue;
            _dirtyMaxX = -1;
            _dirtyMaxY = -1;
            _imageUpdateNeeded = false;
        }

        lock (_imageLock)
        {
            if (_currentPixelBuffer == null || _currentWidth <= 0 || _currentHeight <= 0)
                return;

            width = _currentWidth;
            height = _currentHeight;
        }

        dirtyMinX = Math.Clamp(dirtyMinX, 0, width - 1);
        dirtyMinY = Math.Clamp(dirtyMinY, 0, height - 1);
        dirtyMaxX = Math.Clamp(dirtyMaxX, 0, width - 1);
        dirtyMaxY = Math.Clamp(dirtyMaxY, 0, height - 1);

        // 直接在 UI 线程用 WriteableBitmap 写像素；只更新脏区域，避免整图复制和整图重写
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                // 若尺寸变化则重建 WriteableBitmap
                if (_writeableBitmap == null ||
                    _writeableBitmap.PixelSize.Width != width ||
                    _writeableBitmap.PixelSize.Height != height)
                {
                    if (width > BitmapTileSize || height > BitmapTileSize)
                    {
                        UpdateTiledImageFromBuffer(width, height, dirtyMinX, dirtyMinY, dirtyMaxX, dirtyMaxY);
                        return;
                    }

                    ClearBitmapTiles();
                    _writeableBitmap?.Dispose();
                    _writeableBitmap = new WriteableBitmap(
                        new PixelSize(width, height),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Premul);
                    MapImage.Source = _writeableBitmap;
                    MapImage.IsVisible = true;
                    MapCanvas.Width = width;
                    MapCanvas.Height = height;
                    _avaloniaBitmap?.Dispose();
                    _avaloniaBitmap = null;
                }

                using var frameBuffer = _writeableBitmap.Lock();
                unsafe
                {
                    var dst = (uint*)frameBuffer.Address.ToPointer();
                    int stride = frameBuffer.RowBytes / 4;
                    lock (_imageLock)
                    {
                        if (_currentPixelBuffer == null)
                            return;

                        fixed (uint* src = _currentPixelBuffer)
                        {
                            for (int row = dirtyMinY; row <= dirtyMaxY; row++)
                            {
                                var srcRow = src + row * width;
                                var dstRow = dst + row * stride;
                                // 将 ARGB (A<<24|R<<16|G<<8|B) 转换为 BGRA8888
                                for (int col = dirtyMinX; col <= dirtyMaxX; col++)
                                {
                                    uint p = srcRow[col];
                                    byte a = (byte)(p >> 24);
                                    byte r = (byte)(p >> 16);
                                    byte g = (byte)(p >> 8);
                                    byte b = (byte)p;
                                    dstRow[col] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
                                }
                            }
                        }
                    }
                }

                MapImage.InvalidateVisual();
                MapCanvas.InvalidateVisual();
            }
            catch
            {
                StatusText.Text = $"图像刷新失败: {width}x{height}，当前单张位图可能超过 Avalonia/GPU 纹理限制，需要切片显示";
            }
        });
    }

    private void UpdateTiledImageFromBuffer(int width, int height, int dirtyMinX, int dirtyMinY, int dirtyMaxX,
        int dirtyMaxY)
    {
        _writeableBitmap?.Dispose();
        _writeableBitmap = null;
        _avaloniaBitmap?.Dispose();
        _avaloniaBitmap = null;
        MapImage.Source = null;
        MapImage.IsVisible = false;
        MapCanvas.Width = width;
        MapCanvas.Height = height;
        RemoveOutOfBoundsBitmapTiles(width, height);

        var minTileX = dirtyMinX / BitmapTileSize;
        var minTileY = dirtyMinY / BitmapTileSize;
        var maxTileX = dirtyMaxX / BitmapTileSize;
        var maxTileY = dirtyMaxY / BitmapTileSize;

        for (var tileY = minTileY; tileY <= maxTileY; tileY++)
        {
            for (var tileX = minTileX; tileX <= maxTileX; tileX++)
            {
                var tileOriginX = tileX * BitmapTileSize;
                var tileOriginY = tileY * BitmapTileSize;
                var tileWidth = Math.Min(BitmapTileSize, width - tileOriginX);
                var tileHeight = Math.Min(BitmapTileSize, height - tileOriginY);

                if (!_bitmapTiles.TryGetValue((tileX, tileY), out var tile))
                {
                    var bitmap = new WriteableBitmap(
                        new PixelSize(tileWidth, tileHeight),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Premul);
                    var image = new AvaloniaImage
                    {
                        Source = bitmap,
                        Width = tileWidth,
                        Height = tileHeight,
                        Stretch = Stretch.None
                    };
                    RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
                    RenderOptions.SetEdgeMode(image, EdgeMode.Aliased);
                    Canvas.SetLeft(image, tileOriginX);
                    Canvas.SetTop(image, tileOriginY);
                    MapCanvas.Children.Add(image);
                    tile = (bitmap, image);
                    _bitmapTiles[(tileX, tileY)] = tile;
                }

                var copyMinX = Math.Max(dirtyMinX, tileOriginX);
                var copyMinY = Math.Max(dirtyMinY, tileOriginY);
                var copyMaxX = Math.Min(dirtyMaxX, tileOriginX + tileWidth - 1);
                var copyMaxY = Math.Min(dirtyMaxY, tileOriginY + tileHeight - 1);
                if (copyMinX > copyMaxX || copyMinY > copyMaxY)
                    continue;

                using var frameBuffer = tile.Bitmap.Lock();
                unsafe
                {
                    var dst = (uint*)frameBuffer.Address.ToPointer();
                    int dstStride = frameBuffer.RowBytes / 4;
                    lock (_imageLock)
                    {
                        if (_currentPixelBuffer == null)
                            return;

                        fixed (uint* src = _currentPixelBuffer)
                        {
                            for (var y = copyMinY; y <= copyMaxY; y++)
                            {
                                var srcRow = src + y * width;
                                var dstRow = dst + (y - tileOriginY) * dstStride;
                                for (var x = copyMinX; x <= copyMaxX; x++)
                                {
                                    uint p = srcRow[x];
                                    byte a = (byte)(p >> 24);
                                    byte r = (byte)(p >> 16);
                                    byte g = (byte)(p >> 8);
                                    byte b = (byte)p;
                                    dstRow[x - tileOriginX] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
                                }
                            }
                        }
                    }
                }

                tile.Image.InvalidateVisual();
            }
        }

        MapCanvas.InvalidateVisual();
    }

    private void EnsurePixelBuffer(int pixelCount)
    {
        if (_currentPixelBuffer != null && _currentPixelBufferLength >= pixelCount)
            return;

        // 如果已有旧缓冲，先归还
        if (_currentPixelBuffer != null)
        {
            ArrayPool<uint>.Shared.Return(_currentPixelBuffer, clearArray: false);
        }

        // 租用新缓冲
        _currentPixelBuffer = ArrayPool<uint>.Shared.Rent(pixelCount);
        _currentPixelBufferLength = _currentPixelBuffer.Length;
    }

    private void RemoveOutOfBoundsBitmapTiles(int width, int height)
    {
        var maxTileX = Math.Max(0, (width - 1) / BitmapTileSize);
        var maxTileY = Math.Max(0, (height - 1) / BitmapTileSize);
        var keysToRemove = _bitmapTiles.Keys
            .Where(key => key.X > maxTileX || key.Y > maxTileY)
            .ToList();

        foreach (var key in keysToRemove)
        {
            var tile = _bitmapTiles[key];
            MapCanvas.Children.Remove(tile.Image);
            tile.Bitmap.Dispose();
            _bitmapTiles.Remove(key);
        }
    }

    private void ReleaseImageResources(bool returnPixelBuffer)
    {
        lock (_imageLock)
        {
            if (returnPixelBuffer && _currentPixelBuffer != null)
            {
                ArrayPool<uint>.Shared.Return(_currentPixelBuffer, clearArray: false);
                _currentPixelBuffer = null;
                _currentPixelBufferLength = 0;
            }

            _currentWidth = 0;
            _currentHeight = 0;
        }

        _writeableBitmap?.Dispose();
        _writeableBitmap = null;
        _avaloniaBitmap?.Dispose();
        _avaloniaBitmap = null;
        _currentImage?.Dispose();
        _currentImage = null;
        MapImage.Source = null;
        MapImage.IsVisible = false;
        ClearBitmapTiles();
    }

    private void ClearBitmapTiles()
    {
        foreach (var tile in _bitmapTiles.Values)
        {
            MapCanvas.Children.Remove(tile.Image);
            tile.Bitmap.Dispose();
        }

        _bitmapTiles.Clear();
    }

    private async Task RenderMap()
    {
        if (_renderer == null)
            return;

        if (_isRendering)
        {
            _renderCancellation?.Cancel();
            await Task.Delay(100);
        }

        _isRendering = true;
        _renderCancellation?.Cancel();
        _renderCancellation = new CancellationTokenSource();
        var token = _renderCancellation.Token;

        try
        {
            StatusText.Text = "正在渲染地图...";

            var dimension = Dimension.Overworld;
            if (NetherRadio.IsChecked == true)
                dimension = Dimension.Nether;
            else if (EndRadio.IsChecked == true)
                dimension = Dimension.End;

            var renderMode = RenderModeComboBox.SelectedIndex;
            var layerY = (int)LayerYSlider.Value;

            var image = await Task.Run(() =>
            {
                switch (renderMode)
                {
                    case 0:
                        return _renderer.RenderSurfaceBlocks(dimension, _minChunkX, _minChunkZ, _maxChunkX, _maxChunkZ);
                    case 1:
                        return _renderer.RenderHeightMap(dimension, _minChunkX, _minChunkZ, _maxChunkX, _maxChunkZ);
                    case 2:
                        return _renderer.RenderBiome(dimension, _minChunkX, _minChunkZ, _maxChunkX, _maxChunkZ);
                    case 3:
                        return _renderer.RenderLayerBlocks(dimension, _minChunkX, _minChunkZ, _maxChunkX, _maxChunkZ,
                            layerY);
                    case 4:
                        return _renderer.RenderCaveSlice(dimension, _minChunkX, _minChunkZ, _maxChunkX, _maxChunkZ,
                            layerY);
                    default:
                        return _renderer.RenderSurfaceBlocks(dimension, _minChunkX, _minChunkZ, _maxChunkX, _maxChunkZ);
                }
            }, token);

            if (token.IsCancellationRequested)
            {
                image.Dispose();
                return;
            }

            _currentImage?.Dispose();
            _avaloniaBitmap?.Dispose();

            _currentImage = image;
            _avaloniaBitmap = ConvertToAvaloniaBitmap(image);

            MapImage.Source = _avaloniaBitmap;

            StatusText.Text = "渲染完成";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "渲染已取消";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"渲染失败: {ex.Message}";
        }
        finally
        {
            _isRendering = false;
        }
    }

    private Bitmap ConvertToAvaloniaBitmap(Image<Rgba32> image)
    {
        using var memoryStream = new MemoryStream();
        image.SaveAsPng(memoryStream);
        memoryStream.Position = 0;
        return new Bitmap(memoryStream);
    }

    private WriteableBitmap ConvertToWriteableBitmap(uint[] pixels, int width, int height)
    {
        var wb = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using var fb = wb.Lock();
        unsafe
        {
            var dst = (uint*)fb.Address.ToPointer();
            int stride = fb.RowBytes / 4;
            fixed (uint* src = pixels)
            {
                for (int row = 0; row < height; row++)
                {
                    var srcRow = src + row * width;
                    var dstRow = dst + row * stride;
                    for (int col = 0; col < width; col++)
                    {
                        uint p = srcRow[col];
                        byte a = (byte)(p >> 24);
                        byte r = (byte)(p >> 16);
                        byte g = (byte)(p >> 8);
                        byte b = (byte)p;
                        dstRow[col] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
                    }
                }
            }
        }

        return wb;
    }

    private void MapContainer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(MapContainer).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStartPoint = e.GetCurrentPoint(MapContainer).Position;
            e.Handled = true;
        }
    }

    private void MapContainer_PointerMoved(object? sender, PointerEventArgs e)
    {
        _lastPointerPos = e.GetCurrentPoint(MapCanvas).Position;

        if (_isDragging)
        {
            var currentPoint = e.GetCurrentPoint(MapContainer).Position;
            var delta = currentPoint - _dragStartPoint;
            _offset = new AvaloniaPoint(_offset.X + delta.X, _offset.Y + delta.Y);
            _translateTransform.X = _offset.X;
            _translateTransform.Y = _offset.Y;
            _dragStartPoint = currentPoint;
            e.Handled = true;
        }
    }

    private void MapContainer_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
        e.Handled = true;
    }

    private void MapContainer_PointerLeave(object? sender, PointerEventArgs e)
    {
        _isDragging = false;
    }

    private void MapContainer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var delta = e.Delta.Y;
        if (delta != 0)
        {
            var zoomFactor = delta > 0 ? 1.2 : 0.8;
            var pos = e.GetCurrentPoint(MapCanvas).Position;
            ZoomAtPoint(pos, zoomFactor);
            e.Handled = true;
        }
    }

    private void ZoomAtPoint(AvaloniaPoint point, double factor)
    {
        var oldScale = _currentScale;
        _currentScale = Math.Clamp(_currentScale * factor, MinScale, MaxScale);
        var scaleRatio = _currentScale / oldScale;

        _offset = new AvaloniaPoint(
            point.X - (point.X - _offset.X) * scaleRatio,
            point.Y - (point.Y - _offset.Y) * scaleRatio);

        _scaleTransform.ScaleX = _currentScale;
        _scaleTransform.ScaleY = _currentScale;
        _translateTransform.X = _offset.X;
        _translateTransform.Y = _offset.Y;
    }

    private void ZoomIn()
    {
        var center = new AvaloniaPoint(MapContainer.Bounds.Width / 2, MapContainer.Bounds.Height / 2);
        ZoomAtPoint(center, 1.2);
    }

    private void ZoomOut()
    {
        var center = new AvaloniaPoint(MapContainer.Bounds.Width / 2, MapContainer.Bounds.Height / 2);
        ZoomAtPoint(center, 0.8);
    }

    private void ResetView()
    {
        _currentScale = 1.0;
        _offset = new AvaloniaPoint(0, 0);
        _scaleTransform.ScaleX = _currentScale;
        _scaleTransform.ScaleY = _currentScale;
        _translateTransform.X = _offset.X;
        _translateTransform.Y = _offset.Y;
    }

    private AvaloniaPoint? _lastPointerPos;

    private void CoordinateTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentWidth <= 0 || _world == null || !_lastPointerPos.HasValue)
        {
            CoordinatesText.Text = "X: --, Z: --";
            return;
        }

        var pos = _lastPointerPos.Value;
        var x = (int)((pos.X - _offset.X) / _currentScale);
        var z = (int)((pos.Y - _offset.Y) / _currentScale);

        if (x >= 0 && x < _currentWidth && z >= 0 && z < _currentHeight)
        {
            var worldX = _minChunkX * 16 + x;
            var worldZ = _minChunkZ * 16 + z;
            CoordinatesText.Text = $"X: {worldX}, Z: {worldZ}";
        }
        else
        {
            CoordinatesText.Text = "X: --, Z: --";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _coordinateTimer?.Stop();
        _renderThrottleTimer?.Stop();
        _imageUpdateTimer?.Stop();
        _world?.Dispose();
        _streamingWorld?.Dispose();
        _renderer?.Dispose();
        _streamingRenderer?.Dispose();
        ReleaseImageResources(returnPixelBuffer: true);
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        _coordinateTimer?.Stop();
        _renderThrottleTimer?.Stop();
        _imageUpdateTimer?.Stop();
        _world?.Dispose();
        _streamingWorld?.Dispose();
        _renderer?.Dispose();
        _streamingRenderer?.Dispose();
        ReleaseImageResources(returnPixelBuffer: true);
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
    }
}