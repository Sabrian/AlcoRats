using AutoMapper;
using AlcoRats.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AlcoRats;

/// <summary>
/// Расширения для копирования объектов через AutoMapper.
/// </summary>
static class ObjectExtensions
{
    /// <summary>
    /// Копирует свойства из source в существующий объект dest.
    /// </summary>
    /// <typeparam name="T">Тип объекта.</typeparam>
    /// <param name="dest">Целевой объект.</param>
    /// <param name="source">Источник значений.</param>
    public static void CopyFrom<T>(this T dest, T source)
    {
        MapperConfig.Mapper.Map(source, dest);
    }

    /// <summary>
    /// Создаёт новый объект того же типа с копией данных из source.
    /// </summary>
    /// <typeparam name="T">Тип объекта.</typeparam>
    /// <param name="source">Исходный объект.</param>
    /// <returns>Новый экземпляр с скопированными полями.</returns>
    public static T Clone<T>(this T source)
    {
        return MapperConfig.Mapper.Map<T>(source);
    }
}
