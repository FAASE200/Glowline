using System;
using System.Linq;
using System.Windows;
using LyricGlow.Localization;
using LyricGlow.Services;

namespace LyricGlow;

// Settings for the "Extras" section (0.9.40) and romanization.
public partial class MainWindow
{
    /// <summary>Shows the current state of the extras in the UI.</summary>
    public void RefreshExtrasUi()
    {
        var s = _settings.Settings;
        var loading = _loading;
        _loading = true;
        RomanizationCheck.IsChecked = s.RomanizationEnabled;
        MiniLyricsCheck.IsChecked = s.MiniLyricsEnabled;
        HotkeysCheck.IsChecked = s.GlobalHotkeysEnabled;
        ObsCheck.IsChecked = s.ObsOverlayEnabled;
        DiscordCheck.IsChecked = s.DiscordPresenceEnabled && DiscordPresence.IsConfigured;
        DiscordCheck.IsEnabled = DiscordPresence.IsConfigured;
        _loading = loading;

        DiscordHint.Text = DiscordPresence.IsConfigured
            ? Loc.T("DiscordPresenceHint")
            : Loc.T("DiscordPresenceUnavailable");

        var hotkeys = App.Hotkeys;
        HotkeysList.Text = hotkeys is null
            ? ""
            : string.Join("\n", hotkeys.Hotkeys.Select(h =>
                $"{h.Gesture}  —  {Loc.T(h.LabelKey)}" +
                (s.GlobalHotkeysEnabled && !hotkeys.IsRegistered(h) ? "  " + Loc.T("HotkeyTaken") : "")));

        var overlay = App.NowPlaying?.Overlay;
        if (s.ObsOverlayEnabled && overlay is { IsRunning: true })
        {
            ObsUrlText.Text = overlay.Url;
            ObsCopyButton.IsEnabled = true;
        }
        else if (s.ObsOverlayEnabled && overlay?.LastError is { } error)
        {
            ObsUrlText.Text = Loc.F("ObsOverlayError", s.ObsOverlayPort);
            ObsCopyButton.IsEnabled = false;
            Log.Write("overlay ui: " + error);
        }
        else
        {
            ObsUrlText.Text = Loc.T("ObsOverlayOff");
            ObsCopyButton.IsEnabled = false;
        }
    }

    void Romanization_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Settings.RomanizationEnabled = RomanizationCheck.IsChecked == true;
        _settings.ScheduleSave();
        _lyrics.RefreshDisplayLines();
    }

    void MiniLyrics_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Settings.MiniLyricsEnabled = MiniLyricsCheck.IsChecked == true;
        _settings.ScheduleSave();
        MiniLyricsWindow.Sync();
    }

    void Hotkeys_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Settings.GlobalHotkeysEnabled = HotkeysCheck.IsChecked == true;
        _settings.ScheduleSave();
        App.Hotkeys?.Apply(_settings.Settings.GlobalHotkeysEnabled);
        RefreshExtrasUi();
    }

    void ShareFolder_Click(object sender, RoutedEventArgs e) => FullscreenWindow.OpenShareFolder();

    void Obs_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Settings.ObsOverlayEnabled = ObsCheck.IsChecked == true;
        _settings.ScheduleSave();
        App.NowPlaying?.Apply(_settings.Settings);
        RefreshExtrasUi();
    }

    void ObsCopy_Click(object sender, RoutedEventArgs e)
    {
        var overlay = App.NowPlaying?.Overlay;
        if (overlay is not { IsRunning: true }) return;
        try
        {
            Clipboard.SetText(overlay.Url);
            ShowNotice(Loc.T("ObsLinkCopied"));
        }
        catch (Exception ex)
        {
            Log.Write("obs copy: " + ex.Message);
        }
    }

    void Discord_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Settings.DiscordPresenceEnabled = DiscordCheck.IsChecked == true;
        _settings.ScheduleSave();
        App.NowPlaying?.Apply(_settings.Settings);
    }
}
