using System;
using AutoMapper;

namespace AlcoRats.Mapping;

/// <summary>
/// Глобальный доступ к настроенному экземпляру AutoMapper после старта приложения.
/// </summary>
public static class MapperConfig
{
    private static IMapper? _mapper;

    /// <summary>
    /// Настроенный маппер; до вызова <see cref="Initialize"/> выбрасывает исключение.
    /// </summary>
    public static IMapper Mapper
    {
        get
        {
            return _mapper ?? throw new InvalidOperationException("AutoMapper is not configured. Call MapperConfig.Initialize() at startup.");
        }
    }

    /// <summary>
    /// Сохраняет экземпляр маппера, полученный из DI.
    /// </summary>
    /// <param name="mapper">Экземпляр <see cref="IMapper"/>.</param>
    public static void Initialize(IMapper mapper)
    {
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
    }
}
