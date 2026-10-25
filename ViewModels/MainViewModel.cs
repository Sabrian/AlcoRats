using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlcoRats.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public const string DefaultMeetHost = "https://alkonost-our.site/";
    public const string DefaultRoomName = "AlcoRats";

    public static readonly Uri DefaultRoomUri = BuildRoomUri(DefaultRoomName);

    [ObservableProperty]
    public partial Uri RoomUrl { get; set; } = DefaultRoomUri;

    public static Uri BuildRoomUri(string? roomName)
    {
        var name = string.IsNullOrWhiteSpace(roomName) ? DefaultRoomName : roomName.Trim().Trim('/');
        return new Uri(new Uri(DefaultMeetHost), name);
    }
}
