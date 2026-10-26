using AlcoRats.Bot.Jitsi;
using AlcoRats.Bot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace AlcoRats.Bot.Hosting;

/// <summary>
/// Composition root (Microsoft.Extensions.DependencyInjection + Serilog).
/// </summary>
public static class AppHost
{
    private static IServiceProvider _services;

    public static IServiceProvider Services
        => _services ?? throw new InvalidOperationException("AppHost is not initialized. Call Initialize() first.");

    public static bool IsInitialized => _services != null;

    public static IServiceProvider Initialize(Action<IServiceCollection> configure = null)
    {
        if (_services != null)
            return _services;

        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlcoRats.Bot",
            "logs");
        Directory.CreateDirectory(logDirectory);

        var minimumLevel =
#if DEBUG
            LogEventLevel.Debug;
#else
            LogEventLevel.Information;
#endif

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(logDirectory, "alcorats-bot-.log"),
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

        services.AddSingleton(Configuration.Instance);
        services.AddSingleton<IJitsiBotService, JitsiBotService>();
        services.AddSingleton<IServiceMain, ServiceMain>();
        services.AddSingleton<ICommandProcessor, CommandProcessor>();
        services.AddSingleton<ICommandProvider, TelegramCommandProvider>();
        services.AddSingleton(services => services.GetRequiredService<ICommandProvider>() as IClientProvider);
        services.AddSingleton<IDatabase, Database>();

        configure?.Invoke(services);

        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("AppHost");
        logger.LogInformation(
            "AlcoRats.Bot host initialized. MinimumLevel={MinimumLevel}. Log directory: {LogDirectory}",
            minimumLevel,
            logDirectory);

        return _services;
    }

    public static void Shutdown()
    {
        if (_services == null)
            return;

        try
        {
            try
            {
                // Disconnect from Chromium but keep it alive when attached via debugging port,
                // so the next process can reconnect to the same session.
                var jitsi = _services.GetService<IJitsiBotService>();
                if (jitsi != null)
                    jitsi.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to stop Jitsi bot during shutdown");
            }

            Log.Information("AlcoRats.Bot host shutting down");
            Log.CloseAndFlush();
        }
        finally
        {
            if (_services is IAsyncDisposable asyncDisposable)
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            else if (_services is IDisposable disposable)
                disposable.Dispose();
            _services = null;
        }
    }
}
