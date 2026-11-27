using System;
using System.Reflection;

namespace Glowline.Services;

/// <summary>
/// Public identity of the app sent to external services.
/// LRCLIB and other providers ask for a User-Agent with a name, version and
/// contact URL, so Glowline can be told apart from anonymous traffic.
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
        // The app (WPF or Android) carries the real version; Core may have a
        // different one, so the entry assembly is preferred.
        var version = Assembly.GetEntryAssembly()?.GetName().Version
                      ?? typeof(AppInfo).Assembly.GetName().Version
                      ?? new Version(0, 0, 0);
        return new Version(version.Major, version.Minor, version.Build < 0 ? 0 : version.Build);
    }
}
