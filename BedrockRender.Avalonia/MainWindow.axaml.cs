using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BedrockRender.Palette;
using BedrockWorld;
using BedrockWorld.Chunk;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AvaloniaPoint = Avalonia.Point;
using ImageSharpPoint = SixLabors.ImageSharp.Point;

namespace BedrockRender.Avalonia;

public partial class MainWindow : Window
{
    private StreamingWorld? _streamingWorld;
    private StreamingMapRenderer? _streamingRenderer;
    private BedrockWorld.BedrockWorld? _world;
    private MapRenderer? _renderer;
    private Image<Rgba32>? _currentImage;
    private Bitmap? _avaloniaBitmap;
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

    private int _minChunkX = -32;
    private int _minChunkZ = -32;
    private int _maxChunkX = 32;
    private int _maxChunkZ = 32;

    private readonly object _imageLock = new();
    private uint[]? _currentPixelBuffer;
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
    private DateTime _lastYSliderChange;
    private CancellationTokenSource? _ySliderCancellation;

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
        MapImage.RenderTransform = _transformGroup;
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
        _coordinateTimer.Interval = TimeSpan.FromMilliseconds(33);
        _coordinateTimer.Tick += CoordinateTimer_Tick;
        _coordinateTimer.Start();
    }

    private void InitializeRenderThrottleTimer()
    {
        _renderThrottleTimer = new DispatcherTimer();
        _renderThrottleTimer.Interval = TimeSpan.FromMilliseconds(50);
        _renderThrottleTimer.Tick += RenderThrottleTimer_Tick;
        _renderThrottleTimer.Start();
    }

    private void InitializeImageUpdateTimer()
    {
        _imageUpdateTimer = new DispatcherTimer();
        _imageUpdateTimer.Interval = TimeSpan.FromMilliseconds(66);
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

            _world?.Dispose();
            _streamingWorld?.Dispose();

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

        RequestRender();
    }

    private void LayerYSlider_ValueChanged(object? sender, EventArgs e)
    {
        if (LayerYValue != null)
        {
            LayerYValue.Text = ((int)LayerYSlider.Value).ToString();
            _currentLayerY = (int)LayerYSlider.Value;
            _lastYSliderChange = DateTime.Now;
            RequestYSliderRender();
        }
    }

    private void RequestRender()
    {
        _pendingRenderRequest = true;
    }

    private async void RequestYSliderRender()
    {
        _ySliderCancellation?.Cancel();
        _ySliderCancellation = new CancellationTokenSource();
        var token = _ySliderCancellation.Token;

        try
        {
            await Task.Delay(80, token);
            if (!token.IsCancellationRequested)
            {
                RequestRender();
            }
        }
        catch (OperationCanceledException)
        {
        }
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

        if (_isRendering)
        {
            _renderCancellation?.Cancel();
            await Task.Delay(50);
        }

        _isRendering = true;
        _renderCancellation?.Cancel();
        _renderCancellation = new CancellationTokenSource();
        var token = _renderCancellation.Token;

        try
        {
            var width = (_maxChunkX - _minChunkX + 1) * 16;
            var height = (_maxChunkZ - _minChunkZ + 1) * 16;

            lock (_imageLock)
            {
                if (_currentPixelBuffer == null || _currentWidth != width || _currentHeight != height)
                {
                    _currentPixelBuffer = new uint[width * height];
                    Array.Fill(_currentPixelBuffer, 0u);
                    _currentWidth = width;
                    _currentHeight = height;
                }
            }

            StatusText.Text = "正在渲染地图...";

            _streamingRenderer.ProgressChanged += OnRenderProgressChanged;
            _streamingRenderer.ChunkRendered += OnChunkRendered;

            await Task.Run(async () =>
            {
                await _streamingRenderer.RenderChunksProgressiveAsync(
                    _currentDimension,
                    _minChunkX,
                    _minChunkZ,
                    _maxChunkX,
                    _maxChunkZ,
                    -64,
                    320,
                    _currentLayerY,
                    _currentRenderMode,
                    token);
            }, token);

            if (!token.IsCancellationRequested)
            {
                lock (_updateLock)
                {
                    _imageUpdateNeeded = true;
                }
                StatusText.Text = "渲染完成";
            }
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
            if (_streamingRenderer != null)
            {
                _streamingRenderer.ProgressChanged -= OnRenderProgressChanged;
                _streamingRenderer.ChunkRendered -= OnChunkRendered;
            }
        }
    }

    private void OnRenderProgressChanged(RenderProgress progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = $"渲染中... {progress.RenderedChunks}/{progress.TotalChunks} ({progress.ProgressPercent:F1}%)";
        });
    }

    private void OnChunkRendered(ChunkRenderResult result)
    {
        if (_currentPixelBuffer == null)
            return;

        var pos = result.Position;
        var offsetX = (pos.X - _minChunkX) * 16;
        var offsetZ = (pos.Z - _minChunkZ) * 16;

        lock (_imageLock)
        {
            for (var z = 0; z < 16; z++)
            {
                for (var x = 0; x < 16; x++)
                {
                    var imgX = offsetX + x;
                    var imgZ = offsetZ + z;

                    if (imgX >= 0 && imgX < _currentWidth && imgZ >= 0 && imgZ < _currentHeight)
                    {
                        _currentPixelBuffer[imgZ * _currentWidth + imgX] = result.PixelData[z * 16 + x];
                    }
                }
            }
        }

        lock (_updateLock)
        {
            _imageUpdateNeeded = true;
        }
    }

    private async void ImageUpdateTimer_Tick(object? sender, EventArgs e)
    {
        bool needsUpdate = false;
        lock (_updateLock)
        {
            needsUpdate = _imageUpdateNeeded;
            _imageUpdateNeeded = false;
        }

        if (needsUpdate)
        {
            await UpdateImageFromBufferAsync();
        }
    }

    private async Task UpdateImageFromBufferAsync()
    {
        uint[]? bufferCopy = null;
        int[]? heightMapCopy = null;
        int width, height;

        lock (_imageLock)
        {
            if (_currentPixelBuffer == null)
                return;

            width = _currentWidth;
            height = _currentHeight;
            bufferCopy = new uint[_currentPixelBuffer.Length];
            Array.Copy(_currentPixelBuffer, bufferCopy, _currentPixelBuffer.Length);
        }

        if (_streamingRenderer != null && _streamingRenderer.GlobalHeightMap != null && _currentRenderMode == RenderMode.SurfaceBlocks)
        {
            lock (_imageLock)
            {
                heightMapCopy = new int[_streamingRenderer.GlobalHeightMap.Length];
                Array.Copy(_streamingRenderer.GlobalHeightMap, heightMapCopy, _streamingRenderer.GlobalHeightMap.Length);
            }
        }

        byte[]? pngBytes = null;
        await Task.Run(() =>
        {
            uint[]? finalPixels = bufferCopy;

            if (heightMapCopy != null && _streamingRenderer != null && _currentRenderMode == RenderMode.SurfaceBlocks)
            {
                finalPixels = _streamingRenderer.ApplyShadowsToPixelData(bufferCopy, heightMapCopy, width, height);
            }

            using var image = new Image<Rgba32>(width, height);
            var frame = image.Frames.RootFrame;

            for (var i = 0; i < finalPixels.Length && i < width * height; i++)
            {
                var p = finalPixels[i];
                var y = i / width;
                var x = i % width;
                frame[x, y] = new Rgba32(
                    (byte)((p >> 16) & 0xff),
                    (byte)((p >> 8) & 0xff),
                    (byte)(p & 0xff),
                    (byte)((p >> 24) & 0xff));
            }

            using var memoryStream = new MemoryStream();
            image.SaveAsPng(memoryStream);
            pngBytes = memoryStream.ToArray();
        });

        if (pngBytes != null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    using var stream = new MemoryStream(pngBytes);
                    var newBitmap = new Bitmap(stream);
                    var oldBitmap = _avaloniaBitmap;
                    _avaloniaBitmap = newBitmap;
                    MapImage.Source = newBitmap;
                    oldBitmap?.Dispose();
                }
                catch
                {
                }
            });
        }
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
                        return _renderer.RenderLayerBlocks(dimension, _minChunkX, _minChunkZ, _maxChunkX, _maxChunkZ, layerY);
                    case 4:
                        return _renderer.RenderCaveSlice(dimension, _minChunkX, _minChunkZ, _maxChunkX, _maxChunkZ, layerY);
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

    private void MapContainer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(MapContainer).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStartPoint = e.GetPosition(MapContainer);
            e.Handled = true;
        }
    }

    private void MapContainer_PointerMoved(object? sender, PointerEventArgs e)
    {
        _lastPointerPos = e.GetPosition(MapImage);

        if (_isDragging)
        {
            var currentPoint = e.GetPosition(MapContainer);
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
            var pos = e.GetPosition(MapImage);
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
        if (_currentImage == null || _world == null)
        {
            CoordinatesText.Text = "X: --, Z: --";
            return;
        }

        if (!_lastPointerPos.HasValue)
        {
            CoordinatesText.Text = "X: --, Z: --";
            return;
        }

        var pos = _lastPointerPos.Value;
        var x = (int)((pos.X - _offset.X) / _currentScale);
        var z = (int)((pos.Y - _offset.Y) / _currentScale);

        if (x >= 0 && x < _currentImage.Width && z >= 0 && z < _currentImage.Height)
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
        _currentImage?.Dispose();
        _avaloniaBitmap?.Dispose();
        _renderCancellation?.Cancel();
        _ySliderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        _ySliderCancellation?.Dispose();
        base.OnClosed(e);
    }
}
