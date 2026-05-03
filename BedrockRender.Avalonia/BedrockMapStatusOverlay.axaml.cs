using Avalonia.Controls;

namespace BedrockRender.Avalonia;

public sealed partial class BedrockMapStatusOverlay : UserControl
{
    private BedrockMapRenderView? _renderView;

    public BedrockMapStatusOverlay()
    {
        InitializeComponent();
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
                CoordinateText.Text = "X: --, Z: --";
                ScaleText.Text = "缩放: 100%";
                SizeText.Text = "图像: --";
            }
        }
    }

    public void RefreshStaticState()
    {
        if (_renderView == null)
            return;

        ScaleText.Text = $"缩放: {_renderView.CurrentScale:P0}";
        SizeText.Text = _renderView.HasImage
            ? $"图像: {_renderView.ImageWidth} × {_renderView.ImageHeight}"
            : "图像: --";
    }

    private void RenderView_PointerWorldPositionChanged(object? sender, MapPointerPositionChangedEventArgs e)
    {
        CoordinateText.Text = e.WorldX.HasValue && e.WorldZ.HasValue
            ? $"X: {e.WorldX}, Z: {e.WorldZ}"
            : "X: --, Z: --";
    }

    private void RenderView_ViewChanged(object? sender, MapViewChangedEventArgs e)
    {
        ScaleText.Text = $"缩放: {e.Scale:P0}";
    }
}