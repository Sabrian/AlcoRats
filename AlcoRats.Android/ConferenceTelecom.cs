#nullable enable
using System;
using AlcoRats.Services;
using Android.Content;
using Android.Telecom;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlcoRats.Android;

/// <summary>
/// Telecom PhoneAccount registration and leftover Connection teardown.
/// We do <b>not</b> PlaceCall while in a Jitsi room: an active self-managed Connection
/// makes OEMs map headset short-press to hangup (Bluetooth/SCO renegotiation) and
/// steals the event from MediaSession. Keep-alive is CallStyle FGS + MediaSession;
/// audio routes use AudioManager.
/// </summary>
public static class ConferenceTelecom
{
    public const string PhoneAccountId = "alcorats_conference";
    private static readonly object Gate = new();
    private static bool _accountRegistered;

    /// <summary>True while we intentionally tear down Telecom (leave / inactive sync).</summary>
    public static bool IsEndingCall { get; private set; }

    public static ComponentName ConnectionServiceComponent(Context context)
        => new(context, Java.Lang.Class.FromType(typeof(ConferenceConnectionService)));

    public static PhoneAccountHandle CreateHandle(Context context)
        => new(ConnectionServiceComponent(context), PhoneAccountId);

    public static void EnsureRegistered(Context context, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        if (!global::System.OperatingSystem.IsAndroidVersionAtLeast(26))
            return;

        lock (Gate)
        {
            if (_accountRegistered)
                return;

            try
            {
                var telecom = context.GetSystemService(Context.TelecomService) as TelecomManager;
                if (telecom is null)
                    return;

                var handle = CreateHandle(context);
                var account = new PhoneAccount.Builder(handle, "AlcoRats")?
                    .SetCapabilities((int)PhoneAccountCapability.SelfManaged)?
                    .SetShortDescription("Конференция AlcoRats")?
                    .Build();

                if (account != null)
                {
                    telecom.RegisterPhoneAccount(account);
                    _accountRegistered = true;
                    logger.LogInformation("Telecom PhoneAccount registered ({Id})", PhoneAccountId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Telecom PhoneAccount registration failed");
            }
        }
    }

    /// <summary>Drops any leftover Connection so headset buttons reach MediaSession.</summary>
    public static void SyncWithConference(Context context, ConferenceSessionState state, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        if (!global::System.OperatingSystem.IsAndroidVersionAtLeast(26))
            return;

        EnsureRegistered(context, logger);

        lock (Gate)
        {
            try
            {
                EndCallIfNeeded(logger);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Telecom sync failed (active={Active})", state.IsActive);
            }
        }
    }

    /// <summary>No-op gate clear (kept for ConnectionService disconnect path).</summary>
    public static void ClearCallGate()
    {
    }

    /// <summary>Called if a Connection was somehow requested and Telecom rejected it.</summary>
    public static void NotifyPlaceCallFailed(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        logger.LogWarning("Telecom PlaceCall failed (unexpected — we no longer place calls)");
    }

    private static void EndCallIfNeeded(ILogger logger)
    {
        IsEndingCall = true;
        try
        {
            var connection = ConferenceConnection.Current;
            if (connection is null)
                return;

            try
            {
                connection.SetDisconnected(new DisconnectCause(Causes.Local));
                connection.Destroy();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Telecom connection disconnect");
            }

            ConferenceConnection.Current = null;
        }
        finally
        {
            IsEndingCall = false;
        }
    }
}
