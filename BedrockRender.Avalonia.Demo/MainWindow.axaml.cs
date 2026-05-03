using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BedrockRender.Palette;
using BedrockWorld;
using BedrockWorld.Chunk;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BedrockRender.Avalonia.Demo;

public partial class MainWindow : Window, IDisposable
{
    private sealed record LoadedWorldState(
        StreamingWorld StreamingWorld,
        StreamingMapRenderer StreamingRenderer,
        ChunkBounds Bounds);

    private readonly record struct ChunkBounds(int MinX, int MinZ, int MaxX, int MaxZ, int Count)
    {
        public bool HasChunks => Count > 0;
    }

    private StreamingWorld? _streamingWorld;
    private StreamingMapRenderer? _streamingRenderer;
    private List<string> _recentFolders = new();
    private const int MaxRecentFolders = 10;

    private CancellationTokenSource? _renderCancellation;
    private bool _isRendering;

    private int _minChunkX = -64;
    private int _minChunkZ = -64;
    private int _maxChunkX = 64;
    private int _maxChunkZ = 64;
    private string? _currentWorldPath;

    private RenderMode _currentRenderMode = RenderMode.SurfaceBlocks;
    private int _currentLayerY = 64;
    private Dimension _currentDimension = Dimension.Overworld;

    private DispatcherTimer? _renderThrottleTimer;
    private bool _pendingRenderRequest;
    private int _renderGeneration;
    private int _imageBegunForCurrentRender;
    private int _pendingImageWidth;
    private int _pendingImageHeight;
    private int _pendingOriginWorldX;
    private int _pendingOriginWorldZ;
    private bool _preserveCurrentImageUntilNextRender;

    public MainWindow()
    {
        InitializeComponent();
        InitializeEvents();
        LoadRecentFolders();
        InitializeRenderThrottleTimer();
    }

    private void InitializeEvents()
    {
        SelectFolderButton.Click += SelectFolderButton_Click;
        OpenFolderMenuItem.Click += OpenFolderMenuItem_Click;
        ExitMenuItem.Click += (_, _) => Close();
        ZoomInMenuItem.Click += (_, _) => MapRenderView.ZoomIn();
        ZoomOutMenuItem.Click += (_, _) => MapRenderView.ZoomOut();
        ResetViewMenuItem.Click += (_, _) => MapRenderView.ResetView();
        RenderModeComboBox.SelectionChanged += RenderModeComboBox_SelectionChanged;
        OverworldRadio.IsCheckedChanged += DimensionRadio_Checked;
        NetherRadio.IsCheckedChanged += DimensionRadio_Checked;
        EndRadio.IsCheckedChanged += DimensionRadio_Checked;
        LayerYSlider.ValueChanged += LayerYSlider_ValueChanged;

        MapControls.RenderView = MapRenderView;
        MapStatusOverlay.RenderView = MapRenderView;
        MapRenderView.ImageUpdateFailed += (_, message) => StatusText.Text = message;
    }

    private void InitializeRenderThrottleTimer()
    {
        _renderThrottleTimer = new DispatcherTimer();
        _renderThrottleTimer.Interval = TimeSpan.FromMilliseconds(100);
        _renderThrottleTimer.Tick += RenderThrottleTimer_Tick;
        _renderThrottleTimer.Start();
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
        LoadedWorldState? loadedState = null;

        try
        {
            StatusText.Text = "正在加载世界...";
            MapRenderView.ShowIndeterminateProgress("正在读取存档...");

            _renderCancellation?.Cancel();
            _streamingRenderer?.Dispose();
            _streamingWorld?.Dispose();
            MapRenderView.ClearImage(returnPixelBuffer: true);

            var dimension = _currentDimension;
            loadedState = await Task.Run(() =>
            {
                var streamingWorld = new StreamingWorld(folderPath);

                var basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "colors");
                var blockColorPath = Path.Combine(basePath, "bedrock-block-color.json");
                var biomeColorPath = Path.Combine(basePath, "bedrock-biome-color.json");
                var palette = RenderPalette.Load(blockColorPath, biomeColorPath);
                var streamingRenderer = new StreamingMapRenderer(streamingWorld, palette);
                var bounds = ScanChunkBounds(streamingWorld, dimension);

                return new LoadedWorldState(streamingWorld, streamingRenderer, bounds);
            });

            _streamingWorld = loadedState.StreamingWorld;
            _streamingRenderer = loadedState.StreamingRenderer;
            var bounds = loadedState.Bounds;
            loadedState = null;
            _currentWorldPath = folderPath;

            AddToRecentFolders(folderPath);
            CurrentFolderText.Text = folderPath;

            StatusText.Text = "正在扫描地图边界...";
            if (bounds.HasChunks)
            {
                _minChunkX = bounds.MinX;
                _minChunkZ = bounds.MinZ;
                _maxChunkX = bounds.MaxX;
                _maxChunkZ = bounds.MaxZ;
                var width = ((long)_maxChunkX - _minChunkX + 1) * 16;
                var height = ((long)_maxChunkZ - _minChunkZ + 1) * 16;
                StatusText.Text =
                    $"发现地图: {_minChunkX}..{_maxChunkX}, {_minChunkZ}..{_maxChunkZ} ({bounds.Count} 个区块, {width}x{height})";
            }
            await StartStreamingRender();

            StatusText.Text = "世界加载成功";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载失败: {ex.Message}";
            MapRenderView.HideProgress();
            loadedState?.StreamingWorld.Dispose();
            loadedState?.StreamingRenderer.Dispose();
        }
    }

    private static ChunkBounds ScanChunkBounds(StreamingWorld streamingWorld, Dimension dimension)
    {
        var minX = int.MaxValue;
        var minZ = int.MaxValue;
        var maxX = int.MinValue;
        var maxZ = int.MinValue;
        var count = 0;
        var seen = new HashSet<ChunkPos>();

        foreach (var chunk in streamingWorld.EnumerateChunkPositions(dimension))
        {
            if (!seen.Add(chunk))
                continue;

            count++;
            minX = Math.Min(minX, chunk.X);
            minZ = Math.Min(minZ, chunk.Z);
            maxX = Math.Max(maxX, chunk.X);
            maxZ = Math.Max(maxZ, chunk.Z);
        }

        return count == 0
            ? new ChunkBounds(0, 0, 0, 0, 0)
            : new ChunkBounds(minX, minZ, maxX, maxZ, count);
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
            RequestRender(preserveCurrentImage: false);
        }
    }

    private async void DimensionRadio_Checked(object? sender, EventArgs e)
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
            var bounds = await Task.Run(() => ScanChunkBounds(_streamingWorld, _currentDimension));
            if (bounds.HasChunks)
            {
                _minChunkX = bounds.MinX;
                _minChunkZ = bounds.MinZ;
                _maxChunkX = bounds.MaxX;
                _maxChunkZ = bounds.MaxZ;
                var width = ((long)_maxChunkX - _minChunkX + 1) * 16;
                var height = ((long)_maxChunkZ - _minChunkZ + 1) * 16;
                StatusText.Text =
                    $"发现地图: {_minChunkX}..{_maxChunkX}, {_minChunkZ}..{_maxChunkZ} ({bounds.Count} 个区块, {width}x{height})";
            }
        }

        RequestRender(preserveCurrentImage: false);
    }

    private void LayerYSlider_ValueChanged(object? sender, EventArgs e)
    {
        if (LayerYValue != null)
        {
            LayerYValue.Text = ((int)LayerYSlider.Value).ToString();
            _currentLayerY = (int)LayerYSlider.Value;
            RequestRender(preserveCurrentImage: true);
        }
    }

    private void RequestRender(bool preserveCurrentImage = false)
    {
        _preserveCurrentImageUntilNextRender = preserveCurrentImage;
        _pendingRenderRequest = true;

        if (!preserveCurrentImage)
        {
            MapRenderView.ClearImage(returnPixelBuffer: true);
            Volatile.Write(ref _imageBegunForCurrentRender, 0);
        }

        if (_isRendering)
        {
            StatusText.Text = "正在切换渲染内容...";
            MapRenderView.ShowIndeterminateProgress("正在停止当前渲染...");
            _renderCancellation?.Cancel();
            _streamingRenderer?.CancelCurrentRender();
            return;
        }

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

        if (_isRendering)
        {
            _renderCancellation?.Cancel();
            _streamingRenderer.CancelCurrentRender();
            _pendingRenderRequest = true;
            return;
        }

        _isRendering = true;
        _pendingRenderRequest = false;
        var renderGeneration = Interlocked.Increment(ref _renderGeneration);
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

            _pendingImageWidth = (int)widthLong;
            _pendingImageHeight = (int)heightLong;
            _pendingOriginWorldX = _minChunkX * 16;
            _pendingOriginWorldZ = _minChunkZ * 16;
            if (_preserveCurrentImageUntilNextRender)
            {
                Volatile.Write(ref _imageBegunForCurrentRender, 0);
            }
            else
            {
                MapRenderView.BeginImage(_pendingImageWidth, _pendingImageHeight, _pendingOriginWorldX, _pendingOriginWorldZ);
                MapRenderView.ResetView();
                MapStatusOverlay.RefreshStaticState();
                Volatile.Write(ref _imageBegunForCurrentRender, 1);
            }
            MapRenderView.ShowProgress(0, "准备渲染...");

            // 2. 挂载事件
            _streamingRenderer.ProgressChanged += OnRenderProgressChanged;
            _streamingRenderer.ChunkRendered += OnChunkRendered;

            // 3. 执行异步渲染
            await _streamingRenderer.RenderChunksProgressiveAsync(
                _currentDimension,
                _minChunkX, _minChunkZ, _maxChunkX, _maxChunkZ,
                null,
                -64, 320,
                _currentLayerY,
                _currentRenderMode,
                token);

            StatusText.Text = token.IsCancellationRequested ? "渲染已取消" : "渲染完成";
            if (token.IsCancellationRequested)
            {
                if (!_pendingRenderRequest && renderGeneration == _renderGeneration)
                {
                    MapRenderView.HideProgress();
                }
            }
            else
            {
                MapRenderView.ShowProgress(100, "渲染完成");
                Dispatcher.UIThread.Post(async () =>
                {
                    await Task.Delay(600);
                    if (!_pendingRenderRequest && renderGeneration == _renderGeneration)
                    {
                        MapRenderView.HideProgress();
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            if (!_pendingRenderRequest && renderGeneration == _renderGeneration)
            {
                StatusText.Text = "渲染已取消";
                MapRenderView.HideProgress();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"渲染异常: {ex.Message}";
            if (renderGeneration == _renderGeneration)
            {
                MapRenderView.HideProgress();
            }
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
                await StartStreamingRender();
            }
        }
    }

    private void OnRenderProgressChanged(RenderProgress progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text =
                $"渲染中... {progress.RenderedChunks}/{progress.TotalChunks} ({progress.ProgressPercent:F1}%)";
            MapRenderView.ShowProgress(
                progress.ProgressPercent,
                $"渲染中... {progress.RenderedChunks}/{progress.TotalChunks} ({progress.ProgressPercent:F1}%)");
        });
    }

    private void OnChunkRendered(ChunkRenderResult result)
    {
        // 关键修复：使用 using 确保在方法结束时执行 result.Dispose() 从而归还 ArrayPool
        using (result)
        {
            try
            {
                EnsureImageBegunForCurrentRender();
                MapRenderView.UpdateChunk(result);
            }
            catch (Exception ex)
            {
                // 捕获异步流水线中的异常，避免因单个 Chunk 错误导致整个程序崩溃
                System.Diagnostics.Debug.WriteLine($"[Render Error] Chunk {result.Position}: {ex.Message}");
            }
        } // 此处 result 自动销毁，内部数组通过 ArrayPool.Return 回收
    }

    private void EnsureImageBegunForCurrentRender()
    {
        if (Interlocked.CompareExchange(ref _imageBegunForCurrentRender, 1, 0) != 0)
            return;

        Dispatcher.UIThread.InvokeAsync(() =>
        {
            MapRenderView.BeginImage(_pendingImageWidth, _pendingImageHeight, _pendingOriginWorldX, _pendingOriginWorldZ);
            MapRenderView.ResetView();
            MapStatusOverlay.RefreshStaticState();
        }).GetAwaiter().GetResult();
    }

    protected override void OnClosed(EventArgs e)
    {
        _renderThrottleTimer?.Stop();
        _streamingWorld?.Dispose();
        _streamingRenderer?.Dispose();
        MapRenderView.Dispose();
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        _renderThrottleTimer?.Stop();
        _streamingWorld?.Dispose();
        _streamingRenderer?.Dispose();
        MapRenderView.Dispose();
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
    }
}