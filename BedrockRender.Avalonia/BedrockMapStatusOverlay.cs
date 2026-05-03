using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BedrockRender.Avalonia;

public sealed class BedrockMapStatusOverlay : UserControl
{
    private readonly TextBlock _coordinateText;
    private readonly TextBlock _scaleText;
    private readonly TextBlock _sizeText;
    private BedrockMapRenderView? _renderView;

    public BedrockMapStatusOverlay()
    {
        _coordinateText = new TextBlock { Text = "X: --, Z: --" };
        _scaleText = new TextBlock { Text = "缩放: 100%" };
        _sizeText = new TextBlock { Text = "图像: --" };

        Content = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(210, 32, 32, 32)),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    _coordinateText,
                    _scaleText,
                    _sizeText
                }
            }
        };
    }

    public BedrockMapRenderView? RenderView
    {
        get => _renderView;
        set
        {
            if (ReferenceEquals(_renderView, value))
                return;

            if (_renderView != null)
            {
                _renderView.PointerWorldPositionChanged -= RenderView_PointerWorldPositionChanged;
                _renderView.ViewChanged -= RenderView_ViewChanged;
            }

            _renderView = value;

            if (_renderView != null)
            {
                _renderView.PointerWorldPositionChanged += RenderView_PointerWorldPositionChanged;
                _renderView.ViewChanged += RenderView_ViewChanged;
                RefreshStaticState();
            }
            else
            {
                _coordinateText.Text = "X: --, Z: --";
                _scaleText.Text = "缩放: 100%";
                _sizeText.Text = "图像: --";
            }
        }
    }

    public void RefreshStaticState()
    {
        if (_renderView == null)
            return;

        _scaleText.Text = $"缩放: {_renderView.CurrentScale:P0}";
        _sizeText.Text = _renderView.HasImage
            ? $"图像: {_renderView.ImageWidth} × {_renderView.ImageHeight}"
            : "图像: --";
    }

    private void RenderView_PointerWorldPositionChanged(object? sender, MapPointerPositionChangedEventArgs e)
    {
        _coordinateText.Text = e.WorldX.HasValue && e.WorldZ.HasValue
            ? $"X: {e.WorldX}, Z: {e.WorldZ}"
            : "X: --, Z: --";
    }

    private void RenderView_ViewChanged(object? sender, MapViewChangedEventArgs e)
    {
        _scaleText.Text = $"缩放: {e.Scale:P0}";
    }
}
