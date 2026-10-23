using AlcoRats.Services;
using AlcoRats.Services.Speech;
using AlcoRats.Settings;
using AlcoRats.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AlcoRats.Hosting;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAlcoRatsCore(this IServiceCollection services)
    {
        services.AddSingleton<AppSettings>();
        services.AddSingleton<IConferenceSession, ConferenceSession>();
        services.AddSingleton<IAudioPlayer, PlatformAudioPlayer>();
        services.AddSingleton<PiperTtsEngine>();
        services.AddSingleton<SpeechQueueService>();
        services.AddSingleton<ParticipantAnnouncer>();
        services.AddTransient<MainViewModel>();
        return services;
    }
}
