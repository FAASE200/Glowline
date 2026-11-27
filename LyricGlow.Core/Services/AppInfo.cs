using System;
using System.Reflection;

namespace LyricGlow.Services;

/// <summary>
/// Identidad pública de la app que se envía a los servicios externos.
/// LRCLIB y otros proveedores piden un User-Agent con nombre, versión y
/// una URL de contacto; así se distingue Glowline de tráfico anónimo.
/// </summary>
public static class AppInfo
{
    public const string Name = "Glowline";
    public const string HomepageUrl = "https://github.com/FAASE200/Glowline";

    public static Version Version { get; } = ResolveVersion();

    public static string VersionText => Version.ToString(3);

    public static string UserAgent { get; } = $"{Name}/{VersionText} (+{HomepageUrl})";

    static Version ResolveVersion()
    {
        // La app (WPF o Android) es la que lleva la versión real; Core puede
        // tener otra, así que se prefiere el ensamblado de entrada.
        var version = Assembly.GetEntryAssembly()?.GetName().Version
                      ?? typeof(AppInfo).Assembly.GetName().Version
                      ?? new Version(0, 0, 0);
        return new Version(version.Major, version.Minor, version.Build < 0 ? 0 : version.Build);
    }
}
