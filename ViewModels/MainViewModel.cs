using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlcoRats.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public static readonly Uri DefaultRoomUri = new("https://alkonost-our.site/AlcoRats");

    [ObservableProperty]
    public partial Uri RoomUrl { get; set; } = DefaultRoomUri;
}
