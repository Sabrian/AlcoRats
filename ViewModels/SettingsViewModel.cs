using System.Collections.Generic;
using System.Linq;
using AlcoRats.Services.Speech;
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

    public IReadOnlyList<PiperVoiceInfo> TtsVoices => PiperVoiceCatalog.All;

    public PiperVoiceInfo SelectedTtsVoice
    {
        get => PiperVoiceCatalog.Resolve(Settings.TtsVoice);
        set
        {
            if (value is null)
                return;
            if (string.Equals(Settings.TtsVoice, value.Id, System.StringComparison.OrdinalIgnoreCase))
                return;
            Settings.TtsVoice = value.Id;
            OnPropertyChanged();
        }
    }
}
