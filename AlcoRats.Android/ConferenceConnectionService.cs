#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Telecom;
using Java.Util.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TelecomPresentation = Android.Telecom.Presentation;

namespace AlcoRats.Android;

/// <summary>
/// Self-managed <see cref="ConnectionService"/> for ongoing Jitsi conferences.
/// The system keeps the process warmer while a Connection is Active.
/// </summary>
[Service(
    Name = "com.alcorats.app.ConferenceConnectionService",
    Permission = "android.permission.BIND_TELECOM_CONNECTION_SERVICE",
    Exported = true)]
[IntentFilter(["android.telecom.ConnectionService"])]
public sealed class ConferenceConnectionService : ConnectionService
{
    private static ILogger Logger
        => NullLogger.Instance;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "<Pending>")]
    public override Connection OnCreateOutgoingConnection(
        PhoneAccountHandle? connectionManagerPhoneAccount,
        ConnectionRequest? request)
    {
        var connection = new ConferenceConnection();
        ConferenceConnection.Current = connection;

        // .NET Android bindings: properties instead of Java setXxx setters.
        connection.ConnectionProperties = ConnectionProperties.SelfManaged;
        connection.AudioModeIsVoip = true;
        // Prefer mute over hangup for headset short-press when the OEM supports it.
        try
        {
            connection.ConnectionCapabilities = ConnectionCapability.Mute;
        }
        catch
        {
            // Older bindings may not expose Mute.
        }

        if (request?.Address is not null)
            connection.SetAddress(request.Address, TelecomPresentation.Allowed);
        connection.SetCallerDisplayName("AlcoRats", TelecomPresentation.Allowed);
        // Dialing → Active (skip Initializing — on API 34+ that can stick as "connecting").
        connection.SetDialing();
        connection.SetActive();

        Logger.LogDebug("Telecom Connection created (outgoing self-managed)");
        return connection;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "<Pending>")]
    public override Connection OnCreateIncomingConnection(
        PhoneAccountHandle? connectionManagerPhoneAccount,
        ConnectionRequest? request)
    {
        // We only place outgoing self-managed calls.
        var connection = new ConferenceConnection();
        connection.ConnectionProperties = ConnectionProperties.SelfManaged;
        connection.SetDisconnected(new DisconnectCause(Causes.Rejected));
        connection.Destroy();
        return connection;
    }

    public override void OnCreateOutgoingConnectionFailed(
        PhoneAccountHandle? connectionManagerPhoneAccount,
        ConnectionRequest? request)
    {
        ConferenceTelecom.NotifyPlaceCallFailed(Logger);
        Logger.LogWarning("Telecom outgoing connection failed");
    }

    public override void OnCreateIncomingConnectionFailed(
        PhoneAccountHandle? connectionManagerPhoneAccount,
        ConnectionRequest? request)
        => Logger.LogWarning("Telecom incoming connection failed");
}

/// <summary>Active self-managed connection while the user is in a Jitsi room.</summary>
public sealed class ConferenceConnection : Connection
{
    public static ConferenceConnection? Current { get; set; }

    private readonly object _endpointsGate = new();
    private List<CallEndpoint> _availableEndpoints = [];
    private bool? _lastTelecomMuted;
    private int _disconnectHandled;

    /// <summary>
    /// Route call audio via Telecom. Required while a self-managed Connection is active —
    /// plain AudioManager.SetCommunicationDevice is ignored/reverted by the framework.
    /// </summary>
    public bool TrySetAudioRoute(ConferenceAudio.RouteKind kind, out string note)
    {
        note = "no-op";
        try
        {
            if (global::System.OperatingSystem.IsAndroidVersionAtLeast(34))
            {
                CallEndpoint? endpoint;
                lock (_endpointsGate)
                    endpoint = FindEndpoint(kind, _availableEndpoints);

                if (endpoint is not null)
                {
                    RequestCallEndpointChange(
                        endpoint,
                        Executors.NewSingleThreadExecutor()!,
                        new EndpointOutcomeReceiver());
                    note = $"RequestCallEndpointChange type={endpoint.EndpointType} name={endpoint.EndpointName}";
                    return true;
                }

                note = "no CallEndpoint for " + kind + " (available=" + _availableEndpoints.Count + "); fallback SetAudioRoute";
            }

#pragma warning disable CS0618
            var route = kind switch
            {
                ConferenceAudio.RouteKind.Speaker => CallAudioRoute.Speaker,
                ConferenceAudio.RouteKind.Bluetooth => CallAudioRoute.Bluetooth,
                ConferenceAudio.RouteKind.Wired => CallAudioRoute.WiredHeadset,
                ConferenceAudio.RouteKind.Usb => CallAudioRoute.WiredHeadset,
                _ => CallAudioRoute.Earpiece
            };
            SetAudioRoute(route);
#pragma warning restore CS0618
            note = "SetAudioRoute " + route;
            return true;
        }
        catch (Exception ex)
        {
            note = "Telecom route EX: " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public string DescribeAudioState()
    {
        try
        {
            if (global::System.OperatingSystem.IsAndroidVersionAtLeast(34))
            {
                var cur = CurrentCallEndpoint;
                var avail = 0;
                lock (_endpointsGate)
                    avail = _availableEndpoints.Count;
                if (cur is not null)
                    return $"endpoint type={cur.EndpointType} name={cur.EndpointName}; available={avail}";
                return $"endpoint=null; available={avail}";
            }

#pragma warning disable CS0618
            var state = CallAudioState;
#pragma warning restore CS0618
            if (state is null)
                return "CallAudioState=null";
            return $"route={state.Route}; supported={state.SupportedRouteMask}";
        }
        catch (Exception ex)
        {
            return "audio-state EX: " + ex.Message;
        }
    }

    public override void OnAvailableCallEndpointsChanged(IList<CallEndpoint> availableEndpoints)
    {
        lock (_endpointsGate)
        {
            _availableEndpoints = availableEndpoints?.ToList() ?? [];
        }

        base.OnAvailableCallEndpointsChanged(availableEndpoints);
    }

    public override void OnCallEndpointChanged(CallEndpoint callEndpoint)
    {
        global::Android.Util.Log.Info(
            ConferenceAudio.LogTag,
            "OnCallEndpointChanged type=" + callEndpoint?.EndpointType + " name=" + callEndpoint?.EndpointName);
        base.OnCallEndpointChanged(callEndpoint);
    }

#pragma warning disable CS0618
    public override void OnCallAudioStateChanged(CallAudioState? state)
    {
        global::Android.Util.Log.Info(
            ConferenceAudio.LogTag,
            "OnCallAudioStateChanged route=" + state?.Route + " muted=" + state?.IsMuted);

        // Some devices flip CallAudioState.isMuted on headset play/pause instead of MEDIA_BUTTON.
        // Skip during disconnect/rearm — that churn must not double-toggle the mic.
        if (state is not null
            && _lastTelecomMuted != state.IsMuted
            && !HotkeyDiag.MuteEventsSuppressed
            && _disconnectHandled == 0)
        {
            _lastTelecomMuted = state.IsMuted;
            HotkeyDiag.ToggleMicFrom("Telecom.CallAudioState.isMuted=" + state.IsMuted);
        }
        else if (state is not null)
        {
            _lastTelecomMuted = state.IsMuted;
        }

        base.OnCallAudioStateChanged(state);
    }
#pragma warning restore CS0618

    public override void OnMuteStateChanged(bool isMuted)
    {
        _lastTelecomMuted = isMuted;
        if (!HotkeyDiag.MuteEventsSuppressed && _disconnectHandled == 0)
            HotkeyDiag.ToggleMicFrom("Telecom.OnMuteStateChanged muted=" + isMuted);
        base.OnMuteStateChanged(isMuted);
    }

    public override void OnDisconnect()
        => HandleDisconnect("OnDisconnect");

    public override void OnAbort()
        => HandleDisconnect("OnAbort");

    public override void OnReject()
        => HandleDisconnect("OnReject");

    private void HandleDisconnect(string reason)
    {
        // OEM often fires OnDisconnect + OnAbort for one headset press — handle once.
        if (Interlocked.Exchange(ref _disconnectHandled, 1) != 0)
        {
            HotkeyDiag.Note("Telecom." + reason + " (already handled)");
            return;
        }

        var intentional = ConferenceTelecom.IsEndingCall;
        HotkeyDiag.Note("Telecom." + reason + " intentional=" + intentional);

        if (intentional)
        {
            TearDownConnection();
            return;
        }

        // Should be rare: we no longer PlaceCall for conferences. If a leftover
        // Connection still gets a headset hangup, mute only and tear down — never rearm
        // (re-PlaceCall is what flips Bluetooth/SCO devices).
        HotkeyDiag.ToggleMicFrom("Telecom." + reason + "→hotkey");
        HotkeyDiag.SuppressMuteEvents();
        TearDownConnection();
    }

    private void TearDownConnection()
    {
        try
        {
            SetDisconnected(new DisconnectCause(Causes.Local));
            Destroy();
        }
        catch
        {
            // Already torn down.
        }

        if (ReferenceEquals(Current, this))
            Current = null;

        ConferenceTelecom.ClearCallGate();
    }

    private static CallEndpoint? FindEndpoint(ConferenceAudio.RouteKind kind, List<CallEndpoint> endpoints)
    {
        foreach (var ep in endpoints)
        {
            if (ep is null)
                continue;
            var type = ep.EndpointType;
            var match = kind switch
            {
                ConferenceAudio.RouteKind.Earpiece => type == CallEndpointType.Earpiece,
                ConferenceAudio.RouteKind.Speaker => type == CallEndpointType.Speaker,
                ConferenceAudio.RouteKind.Bluetooth => type == CallEndpointType.Bluetooth,
                ConferenceAudio.RouteKind.Wired => type == CallEndpointType.WiredHeadset,
                ConferenceAudio.RouteKind.Usb => type == CallEndpointType.WiredHeadset,
                _ => false
            };
            if (match)
                return ep;
        }

        // Speaker fallback: name contains "speaker".
        if (kind == ConferenceAudio.RouteKind.Speaker)
        {
            return endpoints.FirstOrDefault(ep =>
                (ep?.EndpointName?.ToString() ?? "").Contains("speaker", StringComparison.OrdinalIgnoreCase)
                || (ep?.EndpointName?.ToString() ?? "").Contains("динамик", StringComparison.OrdinalIgnoreCase)
                || (ep?.EndpointName?.ToString() ?? "").Contains("громк", StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private sealed class EndpointOutcomeReceiver : Java.Lang.Object, IOutcomeReceiver
    {
        public void OnResult(Java.Lang.Object? result)
            => global::Android.Util.Log.Info(ConferenceAudio.LogTag, "CallEndpoint change OK");

        public void OnError(Java.Lang.Throwable error)
            => global::Android.Util.Log.Warn(ConferenceAudio.LogTag, "CallEndpoint change failed: " + error?.Message);
    }
}
