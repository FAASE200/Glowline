namespace Glowline.Services;

/// <summary>
/// Turns the Windows media session's app id (for example
/// "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App" or "chrome.exe") into the name
/// people know the player by ("Apple Music", "Google Chrome").
/// </summary>
public static class PlayerNames
{
    // Order matters: the first match wins ("itunes" must come before "apple").
    static readonly (string Key, string Name)[] Known =
    {
        ("itunes", "iTunes"),
        ("applemusic", "Apple Music"),
        ("apple", "Apple Music"),
        ("spotify", "Spotify"),
        ("tidal", "TIDAL"),
        ("deezer", "Deezer"),
        ("amazonmusic", "Amazon Music"),
        ("soundcloud", "SoundCloud"),
        ("msedge", "Microsoft Edge"),
        ("brave", "Brave"),
        ("vivaldi", "Vivaldi"),
        ("opera", "Opera"),
        ("chrome", "Google Chrome"),
        ("firefox", "Firefox"),
        ("zunemusic", "Media Player"),
        ("wmplayer", "Windows Media Player"),
        ("vlc", "VLC"),
        ("foobar", "foobar2000"),
        ("musicbee", "MusicBee"),
        ("aimp", "AIMP"),
    };

    /// <summary>The player's display name, or an empty string if the id is empty.</summary>
    public static string Friendly(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var lower = id.ToLowerInvariant();
        foreach (var (key, name) in Known)
        {
            if (lower.Contains(key)) return name;
        }
        return Tidy(id);
    }

    /// <summary>Best effort for players that aren't in the list: "Foo.Bar_abc!App" → "Bar", "x.exe" → "x".</summary>
    static string Tidy(string id)
    {
        var text = id.Trim();
        var bang = text.IndexOf('!');
        if (bang > 0) text = text[..bang];
        var underscore = text.IndexOf('_');
        if (underscore > 0) text = text[..underscore];
        if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) text = text[..^4];
        var dot = text.LastIndexOf('.');
        if (dot >= 0 && dot < text.Length - 1) text = text[(dot + 1)..];
        return text.Trim();
    }
}
