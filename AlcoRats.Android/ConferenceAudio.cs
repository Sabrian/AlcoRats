#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Android.Content;
using Android.Media;
using Android.OS;
using Android.Widget;

namespace AlcoRats.Android;

/// <summary>
/// Call audio output routing (earpiece / speaker / wired / Bluetooth) via
/// <see cref="AudioManager"/> communication-device APIs (API 31+) with legacy fallbacks.
/// </summary>
public static class ConferenceAudio
{
    public const string LogTag = "AlcoRats.Audio";

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

    public sealed record SelectResult(bool Ok, string Summary, string Details);

    /// <summary>
    /// Optional hook (MainView) to also steer Chromium/WebRTC via setSinkId / Jitsi APIs.
    /// Argument is a coarse kind: earpiece | speaker | wired | bluetooth | other.
    /// </summary>
    public static Action<string>? WebAudioOutputHint { get; set; }

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
        => SelectDetailed(context, option, showToast: false).Ok;

    public static SelectResult SelectDetailed(Context context, RouteOption option, bool showToast = true)
    {
        var audio = GetAudioManager(context);
        if (audio is null)
            return Fail(context, showToast, "AudioManager=null", "");

        var before = Snapshot(audio);
        EnsureCommunicationMode(audio);

        var modernOk = false;
        var legacyOk = false;
        var modernNote = "skipped";
        var legacyNote = "skipped";

        // Prefer a fresh device handle — stale AudioDeviceInfo from the dialog list often fails.
        AudioDeviceInfo? target = null;
        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            target = FindFreshDevice(audio, option);
            if (target is not null)
            {
                try
                {
                    modernOk = audio.SetCommunicationDevice(target);
                    modernNote = $"SetCommunicationDevice(type={(int)target.Type}, id={target.Id}) → {modernOk}";
                }
                catch (Exception ex)
                {
                    modernNote = "SetCommunicationDevice EX: " + ex.GetType().Name + ": " + ex.Message;
                }
            }
            else
            {
                modernNote = "no fresh device for " + option.Key;
            }
        }

        // Always mirror with legacy flags — WebRTC/WebView often follows SpeakerphoneOn more reliably.
        legacyOk = SelectLegacy(audio, option.Kind);
        legacyNote = $"legacy({option.Kind}) → {legacyOk}; speaker={SafeSpeaker(audio)}; sco={SafeSco(audio)}";

        try
        {
            WebAudioOutputHint?.Invoke(KindHint(option.Kind));
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(LogTag, "WebAudioOutputHint failed: " + ex.Message);
        }

        var after = Snapshot(audio);
        var ok = modernOk || legacyOk;
        var summary = ok
            ? $"OK → {CurrentLabel(context)}"
            : "Не переключилось";
        var details = new StringBuilder()
            .AppendLine("want: " + option.Label + " (" + option.Kind + ")")
            .AppendLine("before: " + before)
            .AppendLine(modernNote)
            .AppendLine(legacyNote)
            .AppendLine("after: " + after)
            .AppendLine("mode: " + audio.Mode)
            .ToString();

        global::Android.Util.Log.Info(LogTag, summary + " | " + details.Replace('\n', ' | '));

        if (showToast)
        {
            try
            {
                Toast.MakeText(context, summary, ToastLength.Long)?.Show();
            }
            catch
            {
                // ignore
            }
        }

        return new SelectResult(ok, summary, details);
    }

    public static string DumpDiagnostics(Context context)
    {
        var audio = GetAudioManager(context);
        if (audio is null)
            return "AudioManager=null";

        var sb = new StringBuilder();
        sb.AppendLine("mode=" + audio.Mode);
        sb.AppendLine("snapshot=" + Snapshot(audio));
        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            try
            {
                var cur = audio.CommunicationDevice;
                sb.AppendLine(cur is null
                    ? "communicationDevice=null"
                    : $"communicationDevice type={(int)cur.Type} id={cur.Id} label={LabelFor(cur)}");
                var available = audio.AvailableCommunicationDevices;
                sb.AppendLine("available=" + (available?.Count ?? 0));
                if (available is not null)
                {
                    foreach (var d in available)
                    {
                        if (d is null) continue;
                        sb.AppendLine($"  - type={(int)d.Type} id={d.Id} sink={d.IsSink} {LabelFor(d)}");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("modern dump EX: " + ex.Message);
            }
        }

        return sb.ToString();
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

    private static SelectResult Fail(Context context, bool showToast, string summary, string details)
    {
        global::Android.Util.Log.Warn(LogTag, summary + " " + details);
        if (showToast)
        {
            try { Toast.MakeText(context, summary, ToastLength.Long)?.Show(); }
            catch { /* ignore */ }
        }

        return new SelectResult(false, summary, details);
    }

    private static AudioDeviceInfo? FindFreshDevice(AudioManager audio, RouteOption option)
    {
        try
        {
            var available = audio.AvailableCommunicationDevices;
            if (available is null)
                return option.Device;

            if (option.Device is not null)
            {
                foreach (var d in available)
                {
                    if (d is not null && d.Id == option.Device.Id && d.Type == option.Device.Type)
                        return d;
                }
            }

            // Match by kind if id vanished.
            foreach (var d in available)
            {
                if (d is null || !d.IsSink)
                    continue;
                if (KindFor(d) == option.Kind)
                    return d;
            }
        }
        catch
        {
            // ignore
        }

        return option.Device;
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

    private static string Snapshot(AudioManager audio)
    {
        var comm = "?";
        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            try
            {
                var d = audio.CommunicationDevice;
                comm = d is null ? "null" : $"{LabelFor(d)}(t={(int)d.Type},id={d.Id})";
            }
            catch (Exception ex)
            {
                comm = "err:" + ex.Message;
            }
        }

        return $"comm={comm}; speaker={SafeSpeaker(audio)}; sco={SafeSco(audio)}";
    }

    private static bool SafeSpeaker(AudioManager audio)
    {
        try
        {
#pragma warning disable CS0618
            return audio.SpeakerphoneOn;
#pragma warning restore CS0618
        }
        catch { return false; }
    }

    private static bool SafeSco(AudioManager audio)
    {
        try
        {
#pragma warning disable CS0618
            return audio.BluetoothScoOn;
#pragma warning restore CS0618
        }
        catch { return false; }
    }

    private static string KindHint(RouteKind kind)
        => kind switch
        {
            RouteKind.Earpiece => "earpiece",
            RouteKind.Speaker => "speaker",
            RouteKind.Wired => "wired",
            RouteKind.Bluetooth => "bluetooth",
            RouteKind.Usb => "usb",
            _ => "other"
        };

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
