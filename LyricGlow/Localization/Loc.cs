using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace LyricGlow.Localization;

public sealed record LanguageOption(AppLanguage Language, string Code, string NativeName)
{
    public override string ToString() => NativeName;
}

public sealed class Loc : INotifyPropertyChanged
{
    static readonly object Gate = new();
    static readonly Dictionary<AppLanguage, Dictionary<string, string>> Maps = new();
    static bool _loaded;

    public static Loc Instance { get; } = new();

    public static IReadOnlyList<LanguageOption> Languages { get; } = new[]
    {
        new LanguageOption(AppLanguage.Spanish, "es", "Español"),
        new LanguageOption(AppLanguage.English, "en", "English"),
        new LanguageOption(AppLanguage.PortugueseBrazil, "pt-BR", "Português (Brasil)"),
        new LanguageOption(AppLanguage.French, "fr", "Français"),
        new LanguageOption(AppLanguage.German, "de", "Deutsch"),
        new LanguageOption(AppLanguage.Italian, "it", "Italiano"),
        new LanguageOption(AppLanguage.Japanese, "ja", "日本語"),
        new LanguageOption(AppLanguage.Korean, "ko", "한국어"),
        new LanguageOption(AppLanguage.ChineseSimplified, "zh-CN", "简体中文"),
        new LanguageOption(AppLanguage.Russian, "ru", "Русский"),
    };

    public static AppLanguage Current { get; private set; } = AppLanguage.English;
    public static CultureInfo Culture { get; private set; } = new("en-US");

    public string this[string key] => Get(Current, key);

    public event PropertyChangedEventHandler? PropertyChanged;
    public static event EventHandler? LanguageChanged;

    public static void Initialize(AppLanguage language)
    {
        EnsureLoaded();
        SetLanguage(language);
    }

    public static void SetLanguage(AppLanguage language)
    {
        EnsureLoaded();
        if (!Maps.ContainsKey(language)) language = AppLanguage.English;
        Current = language;
        Culture = CultureFor(language);
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;

        if (Application.Current is { } app)
        {
            foreach (Window window in app.Windows)
            {
                try { window.Language = XmlLanguage.GetLanguage(Culture.IetfLanguageTag); }
                catch { }
            }
        }

        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(nameof(Current)));
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string T(string key) => Get(Current, key);

    public static string F(string key, params object?[] args)
    {
        var template = T(key);
        return args.Length == 0 ? template : string.Format(Culture, template, args);
    }

    public static string NativeName(AppLanguage language) =>
        Languages.FirstOrDefault(x => x.Language == language)?.NativeName ?? "English";

    public static IReadOnlyList<string> MissingKeys()
    {
        EnsureLoaded();
        var missing = new List<string>();
        if (!Maps.TryGetValue(AppLanguage.English, out var english))
        {
            missing.Add("en:<dictionary not embedded>");
            return missing;
        }
        foreach (var option in Languages)
        {
            if (!Maps.TryGetValue(option.Language, out var map))
            {
                missing.AddRange(english.Keys.Select(key => option.Code + ":" + key));
                continue;
            }
            missing.AddRange(english.Keys.Where(key => !map.ContainsKey(key) || string.IsNullOrWhiteSpace(map[key]))
                .Select(key => option.Code + ":" + key));
        }
        return missing;
    }

    static string Get(AppLanguage language, string key)
    {
        EnsureLoaded();
        if (Maps.TryGetValue(language, out var map) &&
            map.TryGetValue(key, out var value) && value.Length > 0)
            return value;
        if (Maps.TryGetValue(AppLanguage.English, out var english) &&
            english.TryGetValue(key, out var fallback) && fallback.Length > 0)
            return fallback;
        return key;
    }

    static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            var assembly = typeof(Loc).Assembly;
            var names = assembly.GetManifestResourceNames();
            foreach (var option in Languages)
            {
                var suffix = $"Strings-{option.Code}.json";
                var name = names.FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (name is null) continue;
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                try
                {
                    var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                    if (map is not null) Maps[option.Language] = map;
                }
                catch
                {
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

[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Key)) return "";
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
