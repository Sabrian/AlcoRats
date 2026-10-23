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
        // Avoid inheriting MainViewModel from the parent — compiled bindings cast to SettingsViewModel.
        InitializeComponent();
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
        => BackRequested?.Invoke(this, EventArgs.Empty);
}
