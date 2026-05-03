using Avalonia.Controls;

namespace BedrockRender.Avalonia;

public sealed partial class BedrockMapControls : UserControl
{
    private BedrockMapRenderView? _renderView;

    public BedrockMapControls()
    {
        InitializeComponent();
        ZoomInButton.Click += (_, _) => RenderView?.ZoomIn();
        ZoomOutButton.Click += (_, _) => RenderView?.ZoomOut();
        ResetButton.Click += (_, _) => RenderView?.ResetView();
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
                _renderView.ViewChanged -= RenderView_ViewChanged;
            }

            _renderView = value;

            if (_renderView != null)
            {
                _renderView.ViewChanged += RenderView_ViewChanged;
                UpdateScaleText(_renderView.CurrentScale);
            }
            else
            {
                UpdateScaleText(1.0);
            }
        }
    }

    private void RenderView_ViewChanged(object? sender, MapViewChangedEventArgs e)
    {
        UpdateScaleText(e.Scale);
    }

    private void UpdateScaleText(double scale)
    {
        ScaleText.Text = $"{scale:P0}";
    }
}