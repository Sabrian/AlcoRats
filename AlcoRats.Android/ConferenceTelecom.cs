#nullable enable
using System;
using AlcoRats.Services;
using Android.Content;
using Android.OS;
using Android.Telecom;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlcoRats.Android;

/// <summary>
/// Self-managed Telecom account: registers a PhoneAccount and places/ends a call
/// while the Jitsi conference is active so OEMs treat the app as an ongoing VoIP call.
/// </summary>
public static class ConferenceTelecom
{
    public const string PhoneAccountId = "alcorats_conference";
    private static readonly object Gate = new();
    private static bool _accountRegistered;
    /// <summary>True after PlaceCall until we disconnect or the place attempt fails.</summary>
    private static bool _placeCallInFlightOrActive;

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

    /// <summary>Places a self-managed outgoing call when the conference becomes active.</summary>
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
                if (state.IsActive)
                    PlaceCallIfNeeded(context, logger);
                else
                    EndCallIfNeeded(logger);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Telecom sync failed (active={Active})", state.IsActive);
            }
        }
    }

    /// <summary>Clears the in-flight/active gate (disconnect, abort, or PlaceCall failure).</summary>
    public static void ClearCallGate()
    {
        lock (Gate)
            _placeCallInFlightOrActive = false;
    }

    /// <summary>Called from ConnectionService when Telecom rejects the outgoing request.</summary>
    public static void NotifyPlaceCallFailed(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        ClearCallGate();
        logger.LogWarning("Telecom PlaceCall failed; will retry on next sync");
    }

    private static void PlaceCallIfNeeded(Context context, ILogger logger)
    {
        // Live connection already held — just mark ourselves in sync.
        if (ConferenceConnection.Current is not null)
        {
            _placeCallInFlightOrActive = true;
            return;
        }

        // PlaceCall already issued; ConnectionService has not answered yet.
        // A second PlaceCall shows: "already another call connecting".
        if (_placeCallInFlightOrActive)
            return;

        var telecom = context.GetSystemService(Context.TelecomService) as TelecomManager;
        if (telecom is null)
            return;

        var handle = CreateHandle(context);
        var extras = new Bundle();
        extras.PutParcelable(TelecomManager.ExtraPhoneAccountHandle, handle);
        if (global::System.OperatingSystem.IsAndroidVersionAtLeast(26))
            extras.PutBoolean(TelecomManager.ExtraStartCallWithSpeakerphone, false);

        // Namespace AlcoRats.Android shadows Android — use global:: for Uri.
        var address = global::Android.Net.Uri.FromParts("sip", "alcorats", null);
        _placeCallInFlightOrActive = true;
        try
        {
            telecom.PlaceCall(address, extras);
            logger.LogInformation("Telecom self-managed call placed");
        }
        catch (Exception)
        {
            _placeCallInFlightOrActive = false;
            throw;
        }
    }

    private static void EndCallIfNeeded(ILogger logger)
    {
        var connection = ConferenceConnection.Current;
        if (connection is not null)
        {
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

        _placeCallInFlightOrActive = false;
    }
}
