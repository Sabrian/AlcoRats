#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Android.Content;
using Android.Media;
using Android.OS;

namespace AlcoRats.Android;

/// <summary>
/// Call audio output routing (earpiece / speaker / wired / Bluetooth) via
/// <see cref="AudioManager"/> communication-device APIs (API 31+) with legacy fallbacks.
/// </summary>
public static class ConferenceAudio
{
    public enum RouteKind
    {
        Earpiece,
        Speaker,
        Wired,
        Bluetooth,
        Usb,
        Other
    }

    public sealed record RouteOption(string Key, string Label, RouteKind Kind, AudioDeviceInfo? Device);

    public static IReadOnlyList<RouteOption> ListRoutes(Context context)
    {
        var audio = GetAudioManager(context);
        if (audio is null)
            return Array.Empty<RouteOption>();

        EnsureCommunicationMode(audio);

        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(31))
            return ListRoutesModern(audio);

        return ListRoutesLegacy(audio);
    }

    public static string CurrentLabel(Context context)
    {
        var audio = GetAudioManager(context);
        if (audio is null)
            return "Звук";

        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            try
            {
                var current = audio.CommunicationDevice;
                if (current is not null)
                    return LabelFor(current);
            }
            catch
            {
                // Fall through.
            }
        }

#pragma warning disable CS0618
        if (audio.SpeakerphoneOn)
            return "Громкая связь";
        if (audio.BluetoothScoOn)
            return "Bluetooth";
        if (audio.WiredHeadsetOn)
            return "Гарнитура";
#pragma warning restore CS0618
        return "Трубка";
    }

    public static bool Select(Context context, RouteOption option)
    {
        var audio = GetAudioManager(context);
        if (audio is null)
            return false;

        EnsureCommunicationMode(audio);

        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(31) && option.Device is not null)
        {
            try
            {
                return audio.SetCommunicationDevice(option.Device);
            }
            catch
            {
                return false;
            }
        }

        return SelectLegacy(audio, option.Kind);
    }

    public static void ClearOverride(Context context)
    {
        var audio = GetAudioManager(context);
        if (audio is null)
            return;

        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            try { audio.ClearCommunicationDevice(); }
            catch { /* ignore */ }
        }
    }

    private static IReadOnlyList<RouteOption> ListRoutesModern(AudioManager audio)
    {
        var list = new List<RouteOption>();
        IList<AudioDeviceInfo>? devices = null;
        try
        {
            devices = audio.AvailableCommunicationDevices;
        }
        catch
        {
            return ListRoutesLegacy(audio);
        }

        if (devices is null || devices.Count == 0)
            return ListRoutesLegacy(audio);

        foreach (var device in devices)
        {
            if (device is null || !device.IsSink)
                continue;

            var kind = KindFor(device);
            var key = $"{(int)device.Type}:{device.Id}";
            var label = LabelFor(device);
            if (list.Any(r => r.Key == key))
                continue;
            list.Add(new RouteOption(key, label, kind, device));
        }

        // Prefer a stable order: earpiece, wired, bt, speaker, rest.
        return list
            .OrderBy(r => r.Kind switch
            {
                RouteKind.Earpiece => 0,
                RouteKind.Wired => 1,
                RouteKind.Usb => 2,
                RouteKind.Bluetooth => 3,
                RouteKind.Speaker => 4,
                _ => 5
            })
            .ThenBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<RouteOption> ListRoutesLegacy(AudioManager audio)
    {
        var list = new List<RouteOption>
        {
            new("earpiece", "Трубка", RouteKind.Earpiece, null),
            new("speaker", "Громкая связь", RouteKind.Speaker, null)
        };

#pragma warning disable CS0618
        if (audio.IsBluetoothScoAvailableOffCall || audio.BluetoothScoOn)
            list.Add(new("bluetooth", "Bluetooth", RouteKind.Bluetooth, null));
        if (audio.WiredHeadsetOn)
            list.Add(new("wired", "Гарнитура", RouteKind.Wired, null));
#pragma warning restore CS0618

        return list;
    }

    private static bool SelectLegacy(AudioManager audio, RouteKind kind)
    {
        try
        {
#pragma warning disable CS0618
            switch (kind)
            {
                case RouteKind.Speaker:
                    if (audio.BluetoothScoOn)
                    {
                        audio.StopBluetoothSco();
                        audio.BluetoothScoOn = false;
                    }
                    audio.SpeakerphoneOn = true;
                    return true;

                case RouteKind.Bluetooth:
                    audio.SpeakerphoneOn = false;
                    audio.StartBluetoothSco();
                    audio.BluetoothScoOn = true;
                    return true;

                case RouteKind.Wired:
                case RouteKind.Earpiece:
                default:
                    if (audio.BluetoothScoOn)
                    {
                        audio.StopBluetoothSco();
                        audio.BluetoothScoOn = false;
                    }
                    audio.SpeakerphoneOn = false;
                    return true;
            }
#pragma warning restore CS0618
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureCommunicationMode(AudioManager audio)
    {
        try
        {
            if (audio.Mode != Mode.InCommunication)
                audio.Mode = Mode.InCommunication;
        }
        catch
        {
            // Best-effort.
        }
    }

    private static AudioManager? GetAudioManager(Context context)
        => context.GetSystemService(Context.AudioService) as AudioManager;

    private static RouteKind KindFor(AudioDeviceInfo device)
    {
        var type = device.Type;
        if (type == AudioDeviceType.BuiltinEarpiece)
            return RouteKind.Earpiece;
        if (type == AudioDeviceType.BuiltinSpeaker
            || type == AudioDeviceType.BuiltinSpeakerSafe)
            return RouteKind.Speaker;
        if (type == AudioDeviceType.WiredHeadset
            || type == AudioDeviceType.WiredHeadphones
            || type == AudioDeviceType.UsbHeadset)
            return type == AudioDeviceType.UsbHeadset ? RouteKind.Usb : RouteKind.Wired;
        if (type == AudioDeviceType.BluetoothSco
            || type == AudioDeviceType.BluetoothA2dp
            || type == AudioDeviceType.BleHeadset
            || type == AudioDeviceType.BleSpeaker
            || type == AudioDeviceType.HearingAid)
            return RouteKind.Bluetooth;
        return RouteKind.Other;
    }

    private static string LabelFor(AudioDeviceInfo device)
    {
        // Built-in routes: always Russian (ProductName is often English "Speaker").
        var kind = KindFor(device);
        if (kind is RouteKind.Earpiece or RouteKind.Speaker)
            return kind == RouteKind.Speaker ? "Громкая связь" : "Трубка";

        var product = device.ProductName?.ToString()?.Trim();
        if (!string.IsNullOrWhiteSpace(product)
            && !string.Equals(product, "Speaker", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(product, "Earpiece", StringComparison.OrdinalIgnoreCase))
            return product!;

        return device.Type switch
        {
            AudioDeviceType.WiredHeadset => "Гарнитура",
            AudioDeviceType.WiredHeadphones => "Наушники",
            AudioDeviceType.BluetoothSco => "Bluetooth",
            AudioDeviceType.BluetoothA2dp => "Bluetooth (A2DP)",
            AudioDeviceType.BleHeadset => "Bluetooth LE",
            AudioDeviceType.BleSpeaker => "Bluetooth LE",
            AudioDeviceType.UsbHeadset => "USB-гарнитура",
            AudioDeviceType.HearingAid => "Слуховой аппарат",
            _ => string.IsNullOrWhiteSpace(product) ? "Аудиоустройство" : product!
        };
    }
}
