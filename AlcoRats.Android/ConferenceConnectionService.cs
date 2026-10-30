#nullable enable
using Android.App;
using Android.Content;
using Android.Telecom;
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

    public override void OnDisconnect()
    {
        // Headset / system may end the self-managed Telecom call; that must NOT leave
        // Jitsi (play/pause hotkey is mic toggle via MediaSession). Explicit hangup uses
        // ConferenceMediaSessionService.ActionLeave → LeaveAsync.
        SetDisconnected(new DisconnectCause(Causes.Local));
        Destroy();
        if (ReferenceEquals(Current, this))
            Current = null;

        ConferenceTelecom.ClearCallGate();
    }

    public override void OnAbort()
        => OnDisconnect();

    public override void OnReject()
        => OnDisconnect();
}
