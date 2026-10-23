using AlcoRats.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlcoRats.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private AppSettings _settings;

    public SettingsViewModel()
        : this(new AppSettings())
    {
    }

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings ?? new AppSettings();
    }
}
