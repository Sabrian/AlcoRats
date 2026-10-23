using AlcoRats.Settings;
using AlcoRats.ViewModels;
using AutoMapper;

namespace AlcoRats.Mapping;

/// <summary>
/// Профиль AutoMapper приложения: копирование настроек и вложенных объектов.
/// </summary>
public class MappingProfile : Profile
{
    /// <summary>
    /// Регистрирует отображения для глубокого копирования настроек.
    /// </summary>
    public MappingProfile()
    {
        CreateMap<AppSettings, AppSettings>();
    }
}
