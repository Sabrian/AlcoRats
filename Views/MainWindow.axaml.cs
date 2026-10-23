using Avalonia.Controls;

namespace AlcoRats.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public void SetMainView(MainView mainView)
    {
        RootHost.Children.Clear();
        RootHost.Children.Add(mainView);
    }
}
