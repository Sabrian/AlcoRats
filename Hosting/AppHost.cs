using System;
using System.IO;
using AlcoRats.Services.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace AlcoRats.Hosting;

/// <summary>
/// Application composition root (Microsoft.Extensions.DependencyInjection + Serilog).
/// </summary>
public static class AppHost
{
    private static IServiceProvider? _services;
    private static bool _runtimeStarted;

    public static IServiceProvider Services
        => _services ?? throw new InvalidOperationException("AppHost is not initialized. Call Initialize() first.");

    public static bool IsInitialized => _services is not null;

    public static IServiceProvider Initialize(Action<IServiceCollection>? configurePlatform = null)
    {
        if (_services is not null)
            return _services;

        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlcoRats",
            "logs");
        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(logDirectory, "alcorats-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: false);
        });

        services.AddAlcoRatsCore();
        configurePlatform?.Invoke(services);

        // Avoid ValidateOnBuild: it eagerly constructs singletons (TTS/Piper) during Avalonia startup.
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = true
        });

        GlobalExceptionHandler.Install(_services.GetRequiredService<ILogger<GlobalExceptionHandler>>());

        var logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("AppHost");
        logger.LogInformation("AlcoRats host initialized. Log directory: {LogDirectory}", logDirectory);

        return _services;
    }

    /// <summary>
    /// Starts background services after Avalonia UI is ready.
    /// </summary>
    public static void StartRuntime()
    {
        if (_services is null || _runtimeStarted)
            return;

        _runtimeStarted = true;
        GlobalExceptionHandler.AttachUiDispatcher();

        var logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("AppHost");
        try
        {
            var announcer = _services.GetRequiredService<ParticipantAnnouncer>();
            announcer.Start();
            logger.LogInformation(
                "Runtime started (TTS: {Engine})",
                _services.GetRequiredService<SpeechQueueService>().ActiveEngineName ?? "pending");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start runtime services");
        }
    }

    public static void Shutdown()
    {
        if (_services is null)
            return;

        try
        {
            _services.GetService<ParticipantAnnouncer>()?.Stop();
            _services.GetService<SpeechQueueService>()?.Shutdown();
            Log.Information("AlcoRats host shutting down");
            Log.CloseAndFlush();
        }
        finally
        {
            if (_services is IDisposable disposable)
                disposable.Dispose();
            _services = null;
            _runtimeStarted = false;
        }
    }
}
