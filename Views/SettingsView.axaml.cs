using System;
using AlcoRats.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AlcoRats.Views;

public partial class SettingsView : UserControl
{
    public event EventHandler? BackRequested;

    public SettingsView()
    {
        // Must set before InitializeComponent / attach: otherwise the control inherits
        // MainViewModel from MainView and compiled bindings cast it to SettingsViewModel.
        DataContext = new SettingsViewModel();
        InitializeComponent();
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
        => BackRequested?.Invoke(this, EventArgs.Empty);
}
