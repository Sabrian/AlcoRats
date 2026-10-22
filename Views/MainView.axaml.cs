using Avalonia.Controls;

namespace AlcoRats.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        RoomWebView.NavigationCompleted += OnNavigationCompleted;
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        LoadingOverlay.IsVisible = false;
        LoadingOverlay.IsHitTestVisible = false;
    }
}
