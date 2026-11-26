using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using LyricGlow.Localization;
using LyricGlow.Services;

namespace LyricGlow.Android.Services;

public sealed record MobileLanguage(AppLanguage Language, string Code, string NativeName);

public static class MobileLoc
{
    static readonly object Gate = new();
    static readonly Dictionary<AppLanguage, Dictionary<string, string>> Maps = new();
    static bool _loaded;

    public static AppLanguage Current { get; private set; } = AppLanguage.Spanish;
    public static CultureInfo Culture { get; private set; } = new("es-CL");
    public static event Action? LanguageChanged;

    public static IReadOnlyList<MobileLanguage> Languages { get; } = new[]
    {
        new MobileLanguage(AppLanguage.Spanish, "es", "Español"),
        new MobileLanguage(AppLanguage.English, "en", "English"),
        new MobileLanguage(AppLanguage.PortugueseBrazil, "pt-BR", "Português (Brasil)"),
        new MobileLanguage(AppLanguage.French, "fr", "Français"),
        new MobileLanguage(AppLanguage.German, "de", "Deutsch"),
        new MobileLanguage(AppLanguage.Italian, "it", "Italiano"),
        new MobileLanguage(AppLanguage.Japanese, "ja", "日本語"),
        new MobileLanguage(AppLanguage.Korean, "ko", "한국어"),
        new MobileLanguage(AppLanguage.ChineseSimplified, "zh-CN", "简体中文"),
        new MobileLanguage(AppLanguage.Russian, "ru", "Русский"),
    };

    public static void SetLanguage(AppLanguage language)
    {
        EnsureLoaded();
        if (!Maps.ContainsKey(language)) language = AppLanguage.English;
        Current = language;
        Culture = CultureFor(language);
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        LanguageChanged?.Invoke();
    }

    public static string T(string key) => Get(Current, key);

    public static string F(string key, params object?[] args) =>
        args.Length == 0 ? T(key) : string.Format(Culture, T(key), args);

    static string Get(AppLanguage language, string key)
    {
        EnsureLoaded();
        if (Maps.TryGetValue(language, out var map) && map.TryGetValue(key, out var value) && value.Length > 0)
            return value;
        if (Maps.TryGetValue(AppLanguage.English, out var english) && english.TryGetValue(key, out var fallback) && fallback.Length > 0)
            return fallback;
        return key;
    }

    static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            var names = typeof(MobileLoc).Assembly.GetManifestResourceNames();
            foreach (var option in Languages)
            {
                var suffix = $"Strings-{option.Code}.json";
                var name = names.FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (name is null) continue;
                using var stream = typeof(MobileLoc).Assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                try
                {
                    var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                    if (map is not null) Maps[option.Language] = map;
                }
                catch (Exception ex)
                {
                    Log.Write("Mobile localization: " + ex.Message);
                }
            }
            _loaded = true;
        }
    }

    static CultureInfo CultureFor(AppLanguage language) => language switch
    {
        AppLanguage.English => new CultureInfo("en-US"),
        AppLanguage.PortugueseBrazil => new CultureInfo("pt-BR"),
        AppLanguage.French => new CultureInfo("fr-FR"),
        AppLanguage.German => new CultureInfo("de-DE"),
        AppLanguage.Italian => new CultureInfo("it-IT"),
        AppLanguage.Japanese => new CultureInfo("ja-JP"),
        AppLanguage.Korean => new CultureInfo("ko-KR"),
        AppLanguage.ChineseSimplified => new CultureInfo("zh-CN"),
        AppLanguage.Russian => new CultureInfo("ru-RU"),
        _ => new CultureInfo("es-CL"),
    };
}
