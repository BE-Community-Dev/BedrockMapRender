using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Buffers;
using AvaloniaImage = Avalonia.Controls.Image;
using AvaloniaPoint = Avalonia.Point;

namespace BedrockRender.Avalonia;

public sealed class BedrockMapRenderView : UserControl, IDisposable
{
    public const double DefaultMinScale = 0.1;
    public const double DefaultMaxScale = 10.0;

    private const int BitmapTileSize = 512;
    private const int TilePixelCount = BitmapTileSize * BitmapTileSize;

    private readonly Grid _root;
    private readonly Canvas _mapCanvas;
    private readonly AvaloniaImage _mapImage;
    private readonly TextBlock _emptyText;
    private readonly Border _progressHost;
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _progressText;
    private readonly ScaleTransform _scaleTransform = new();
    private readonly TranslateTransform _translateTransform = new();
    private readonly TransformGroup _transformGroup = new();
    private readonly Dictionary<(int X, int Y), (WriteableBitmap Bitmap, AvaloniaImage Image)> _bitmapTiles = new();
    private readonly object _imageLock = new();
    private readonly object _updateLock = new();
    private readonly DispatcherTimer _imageUpdateTimer;

    private WriteableBitmap? _writeableBitmap;
    private int _currentWidth;
    private int _currentHeight;
    private int _originWorldX;
    private int _originWorldZ;
    private bool _hasImage;
    private bool _isDragging;
    private bool _isDisposed;
    private bool _imageUpdateNeeded;
    private bool _imageUpdateInProgress;
    private readonly HashSet<(int X, int Y)> _dirtyTiles = new();
    private readonly Dictionary<(int X, int Y), uint[]> _tilePixelBuffers = new();
    private AvaloniaPoint _dragStartPoint;
    private AvaloniaPoint _offset = new(0, 0);
    private AvaloniaPoint? _lastPointerContainerPosition;
    private double _currentScale = 1.0;

    public BedrockMapRenderView()
    {
        Focusable = true;
        ClipToBounds = true;
        Background = Brushes.Transparent;

        _transformGroup.Children.Add(_scaleTransform);
        _transformGroup.Children.Add(_translateTransform);

        _mapImage = new AvaloniaImage
        {
            Stretch = Stretch.None,
            IsVisible = false
        };
        RenderOptions.SetBitmapInterpolationMode(_mapImage, BitmapInterpolationMode.None);
        RenderOptions.SetEdgeMode(_mapImage, EdgeMode.Aliased);

        _mapCanvas = new Canvas
        {
            RenderTransform = _transformGroup,
            RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative),
            Background = Brushes.Transparent
        };
        _mapCanvas.Children.Add(_mapImage);

        _emptyText = new TextBlock
        {
            Text = EmptyText,
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 8
        };

        _progressText = new TextBlock
        {
            Text = string.Empty,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4)
        };

        _progressHost = new Border
        {
            IsVisible = false,
            Padding = new Thickness(12, 8),
            Background = new SolidColorBrush(Color.FromArgb(190, 24, 24, 24)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    _progressText,
                    _progressBar
                }
            }
        };

        _root = new Grid
        {
            ClipToBounds = true,
            Background = Brushes.Transparent,
            Children =
            {
                _mapCanvas,
                _emptyText,
                _progressHost
            }
        };
        Content = _root;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        PointerWheelChanged += OnPointerWheelChanged;

        _imageUpdateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _imageUpdateTimer.Tick += ImageUpdateTimer_Tick;
        _imageUpdateTimer.Start();
    }

    public event EventHandler<MapPointerPositionChangedEventArgs>? PointerWorldPositionChanged;

    public event EventHandler<MapViewChangedEventArgs>? ViewChanged;

    public event EventHandler<string>? ImageUpdateFailed;

    public double CurrentScale => _currentScale;

    public double OffsetX => _offset.X;

    public double OffsetY => _offset.Y;

    public int ImageWidth => _currentWidth;

    public int ImageHeight => _currentHeight;

    public bool HasImage => _hasImage;

    public string EmptyText
    {
        get => _emptyText?.Text ?? "请选择存档文件夹开始渲染";
        set
        {
            if (_emptyText != null)
            {
                _emptyText.Text = value;
            }
        }
    }

    public void ShowIndeterminateProgress(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _progressText.Text = text;
            _progressBar.IsIndeterminate = true;
            _progressBar.Value = 0;
            _progressHost.IsVisible = true;
        });
    }

    public void ShowProgress(double value, string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _progressText.Text = text;
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = Math.Clamp(value, 0, 100);
            _progressHost.IsVisible = true;
        });
    }

    public void HideProgress()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = 0;
            _progressText.Text = string.Empty;
            _progressHost.IsVisible = false;
        });
    }

    public void BeginImage(int width, int height, int originWorldX, int originWorldZ)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "图像尺寸必须大于 0。");

        lock (_imageLock)
        {
            ClearTilePixelBuffers();
            _currentWidth = width;
            _currentHeight = height;
            _originWorldX = originWorldX;
            _originWorldZ = originWorldZ;
            _hasImage = true;
        }

        lock (_updateLock)
        {
            _dirtyTiles.Clear();
            _imageUpdateNeeded = false;
        }

        Dispatcher.UIThread.Post(() => _emptyText.IsVisible = false);
        Dispatcher.UIThread.Post(() =>
        {
            _mapCanvas.Width = width;
            _mapCanvas.Height = height;
        }, DispatcherPriority.Loaded);
    }

    public void UpdateChunk(ChunkRenderResult result)
    {
        if (result.PixelData.Length < 16 * 16)
            return;

        var offsetX = (result.Position.X - result.MinChunkX) * 16;
        var offsetZ = (result.Position.Z - result.MinChunkZ) * 16;
        var copiedAny = false;

        lock (_imageLock)
        {
            if (_currentWidth <= 0 || _currentHeight <= 0)
                return;

            for (var z = 0; z < 16; z++)
            {
                var imgZ = offsetZ + z;
                if (imgZ < 0 || imgZ >= _currentHeight) continue;
                if (offsetX < 0 || offsetX + 16 > _currentWidth) continue;

                var srcOffset = z * 16;
                CopyPixelsToTiles(result.PixelData, srcOffset, offsetX, imgZ, 16);
                copiedAny = true;
            }
        }

        if (copiedAny)
        {
            MarkDirty(offsetX, offsetZ, offsetX + 15, offsetZ + 15);
        }
    }

    public void ZoomIn() => ZoomAtViewportPoint(GetViewportCenter(), 1.2);

    public void ZoomOut() => ZoomAtViewportPoint(GetViewportCenter(), 0.8);

    public void ResetView()
    {
        _currentScale = 1.0;
        _offset = new AvaloniaPoint(0, 0);
        ApplyTransform();
    }

    public void ClearImage(bool returnPixelBuffer = true)
    {
        lock (_imageLock)
        {
            if (returnPixelBuffer)
            {
                ClearTilePixelBuffers();
            }

            _currentWidth = 0;
            _currentHeight = 0;
            _originWorldX = 0;
            _originWorldZ = 0;
            _hasImage = false;
        }

        _writeableBitmap?.Dispose();
        _writeableBitmap = null;
        _mapImage.Source = null;
        _mapImage.IsVisible = false;
        ClearBitmapTiles();
        _emptyText.IsVisible = true;
        PointerWorldPositionChanged?.Invoke(this, new MapPointerPositionChangedEventArgs(null, null));
    }

    public bool TryGetWorldPosition(AvaloniaPoint viewportPoint, out int worldX, out int worldZ)
    {
        var imageX = (int)Math.Floor((viewportPoint.X - _offset.X) / _currentScale);
        var imageZ = (int)Math.Floor((viewportPoint.Y - _offset.Y) / _currentScale);

        if (_hasImage && imageX >= 0 && imageX < _currentWidth && imageZ >= 0 && imageZ < _currentHeight)
        {
            worldX = _originWorldX + imageX;
            worldZ = _originWorldZ + imageZ;
            return true;
        }

        worldX = 0;
        worldZ = 0;
        return false;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            Focus();
            _isDragging = true;
            _dragStartPoint = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        _lastPointerContainerPosition = e.GetPosition(this);
        RaisePointerPositionChanged(_lastPointerContainerPosition.Value);

        if (_isDragging)
        {
            var currentPoint = e.GetPosition(this);
            var delta = currentPoint - _dragStartPoint;
            _offset = new AvaloniaPoint(_offset.X + delta.X, _offset.Y + delta.Y);
            _dragStartPoint = currentPoint;
            ApplyTransform();
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isDragging = false;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var delta = e.Delta.Y;
        if (delta == 0)
            return;

        ZoomAtViewportPoint(e.GetPosition(this), delta > 0 ? 1.2 : 0.8);
        e.Handled = true;
    }

    private void ZoomAtViewportPoint(AvaloniaPoint viewportPoint, double factor)
    {
        var oldScale = _currentScale;
        var imagePointBeforeZoom = new AvaloniaPoint(
            (viewportPoint.X - _offset.X) / oldScale,
            (viewportPoint.Y - _offset.Y) / oldScale);

        _currentScale = Math.Clamp(_currentScale * factor, DefaultMinScale, DefaultMaxScale);
        if (Math.Abs(oldScale - _currentScale) < double.Epsilon)
            return;

        _offset = new AvaloniaPoint(
            viewportPoint.X - imagePointBeforeZoom.X * _currentScale,
            viewportPoint.Y - imagePointBeforeZoom.Y * _currentScale);

        ApplyTransform();
    }

    private void ApplyTransform()
    {
        _scaleTransform.ScaleX = _currentScale;
        _scaleTransform.ScaleY = _currentScale;
        _translateTransform.X = _offset.X;
        _translateTransform.Y = _offset.Y;
        ViewChanged?.Invoke(this, new MapViewChangedEventArgs(_currentScale, _offset.X, _offset.Y));

        if (_lastPointerContainerPosition.HasValue)
        {
            RaisePointerPositionChanged(_lastPointerContainerPosition.Value);
        }
    }

    private AvaloniaPoint GetViewportCenter() => new(Bounds.Width / 2, Bounds.Height / 2);

    private void RaisePointerPositionChanged(AvaloniaPoint viewportPoint)
    {
        if (TryGetWorldPosition(viewportPoint, out var worldX, out var worldZ))
        {
            PointerWorldPositionChanged?.Invoke(this, new MapPointerPositionChangedEventArgs(worldX, worldZ));
        }
        else
        {
            PointerWorldPositionChanged?.Invoke(this, new MapPointerPositionChangedEventArgs(null, null));
        }
    }

    private void MarkDirty(int minX, int minY, int maxX, int maxY)
    {
        lock (_updateLock)
        {
            minX = Math.Clamp(minX, 0, Math.Max(0, _currentWidth - 1));
            minY = Math.Clamp(minY, 0, Math.Max(0, _currentHeight - 1));
            maxX = Math.Clamp(maxX, 0, Math.Max(0, _currentWidth - 1));
            maxY = Math.Clamp(maxY, 0, Math.Max(0, _currentHeight - 1));
            for (var tileY = minY / BitmapTileSize; tileY <= maxY / BitmapTileSize; tileY++)
            {
                for (var tileX = minX / BitmapTileSize; tileX <= maxX / BitmapTileSize; tileX++)
                {
                    _dirtyTiles.Add((tileX, tileY));
                }
            }
            _imageUpdateNeeded = true;
        }
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
        int width;
        int height;
        List<(int X, int Y)> dirtyTiles;

        lock (_updateLock)
        {
            if (!_imageUpdateNeeded)
                return;

            dirtyTiles = _dirtyTiles.ToList();
            _dirtyTiles.Clear();
            _imageUpdateNeeded = false;
        }

        lock (_imageLock)
        {
            if (_currentWidth <= 0 || _currentHeight <= 0)
                return;

            width = _currentWidth;
            height = _currentHeight;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                UpdateTiledImageFromBuffers(width, height, dirtyTiles);
            }
            catch (Exception ex)
            {
                ImageUpdateFailed?.Invoke(this, $"图像刷新失败: {width}x{height}，{ex.Message}");
            }
        });
    }

    private void UpdateTiledImageFromBuffers(int width, int height, List<(int X, int Y)> dirtyTiles)
    {
        if (dirtyTiles.Count == 0)
            return;

        _writeableBitmap?.Dispose();
        _writeableBitmap = null;
        _mapImage.Source = null;
        _mapImage.IsVisible = false;
        _mapCanvas.Width = width;
        _mapCanvas.Height = height;
        RemoveOutOfBoundsBitmapTiles(width, height);

        foreach (var (tileX, tileY) in dirtyTiles)
        {
            var tileOriginX = tileX * BitmapTileSize;
            var tileOriginY = tileY * BitmapTileSize;
            if (tileOriginX >= width || tileOriginY >= height)
                continue;

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
                _mapCanvas.Children.Add(image);
                tile = (bitmap, image);
                _bitmapTiles[(tileX, tileY)] = tile;
            }

            uint[]? tileBuffer;
            lock (_imageLock)
            {
                _tilePixelBuffers.TryGetValue((tileX, tileY), out tileBuffer);
            }

            if (tileBuffer == null)
                continue;

            using var frameBuffer = tile.Bitmap.Lock();
            unsafe
            {
                var dst = (uint*)frameBuffer.Address.ToPointer();
                var dstStride = frameBuffer.RowBytes / 4;
                fixed (uint* src = tileBuffer)
                {
                    for (var y = 0; y < tileHeight; y++)
                    {
                        var srcRow = src + y * BitmapTileSize;
                        var dstRow = dst + y * dstStride;
                        for (var x = 0; x < tileWidth; x++)
                        {
                            dstRow[x] = ToBgra(srcRow[x]);
                        }
                    }
                }
            }

            tile.Image.InvalidateVisual();
        }

        _mapCanvas.InvalidateVisual();
    }

    private void CopyPixelsToTiles(uint[] source, int sourceOffset, int imageX, int imageY, int length)
    {
        var remaining = length;
        var sourceIndex = sourceOffset;
        var x = imageX;
        while (remaining > 0)
        {
            var tileX = x / BitmapTileSize;
            var tileY = imageY / BitmapTileSize;
            var localX = x - tileX * BitmapTileSize;
            var localY = imageY - tileY * BitmapTileSize;
            var copyLength = Math.Min(remaining, BitmapTileSize - localX);

            if (!_tilePixelBuffers.TryGetValue((tileX, tileY), out var tileBuffer))
            {
                tileBuffer = ArrayPool<uint>.Shared.Rent(TilePixelCount);
                Array.Clear(tileBuffer, 0, TilePixelCount);
                _tilePixelBuffers[(tileX, tileY)] = tileBuffer;
            }

            Array.Copy(source, sourceIndex, tileBuffer, localY * BitmapTileSize + localX, copyLength);
            sourceIndex += copyLength;
            x += copyLength;
            remaining -= copyLength;
        }
    }

    private void ClearTilePixelBuffers()
    {
        foreach (var buffer in _tilePixelBuffers.Values)
        {
            ArrayPool<uint>.Shared.Return(buffer, clearArray: false);
        }

        _tilePixelBuffers.Clear();
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
            _mapCanvas.Children.Remove(tile.Image);
            tile.Bitmap.Dispose();
            _bitmapTiles.Remove(key);
        }
    }

    private void ClearBitmapTiles()
    {
        foreach (var tile in _bitmapTiles.Values)
        {
            _mapCanvas.Children.Remove(tile.Image);
            tile.Bitmap.Dispose();
        }

        _bitmapTiles.Clear();
    }

    private static uint ToBgra(uint argb)
    {
        var a = (byte)(argb >> 24);
        var r = (byte)(argb >> 16);
        var g = (byte)(argb >> 8);
        var b = (byte)argb;
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _imageUpdateTimer.Stop();
        _imageUpdateTimer.Tick -= ImageUpdateTimer_Tick;
        ClearImage(returnPixelBuffer: true);
        _writeableBitmap?.Dispose();
    }
}
