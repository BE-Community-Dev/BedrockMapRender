using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BedrockRender.Avalonia;

public sealed class BedrockMapControls : UserControl
{
    private readonly Button _zoomInButton;
    private readonly Button _zoomOutButton;
    private readonly Button _resetButton;
    private readonly TextBlock _scaleText;
    private BedrockMapRenderView? _renderView;

    public BedrockMapControls()
    {
        _zoomInButton = new Button { Content = "+", MinWidth = 36 };
        _zoomOutButton = new Button { Content = "−", MinWidth = 36 };
        _resetButton = new Button { Content = "重置", MinWidth = 56 };
        _scaleText = new TextBlock
        {
            Text = "100%",
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 52,
            TextAlignment = TextAlignment.Center
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                _zoomInButton,
                _zoomOutButton,
                _resetButton,
                _scaleText
            }
        };

        Content = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(210, 32, 32, 32)),
            Child = panel
        };

        _zoomInButton.Click += (_, _) => RenderView?.ZoomIn();
        _zoomOutButton.Click += (_, _) => RenderView?.ZoomOut();
        _resetButton.Click += (_, _) => RenderView?.ResetView();
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
        _scaleText.Text = $"{scale:P0}";
    }
}
