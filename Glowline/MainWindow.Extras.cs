using System;
using System.Linq;
using System.Windows;
using Glowline.Localization;
using Glowline.Services;

namespace Glowline;

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
        DiscordLyricsCheck.IsChecked = s.DiscordShowLyrics;
        DiscordLyricsCheck.IsEnabled = DiscordPresence.IsConfigured;
        TranslationCheck.IsChecked = s.TranslationEnabled;
        FillChoices(ShareStyleCombo, s.ShareStyle, "ShareStyleCover", "ShareStyleGradient", "ShareStyleLight");
        FillChoices(ShareFormatCombo, s.ShareFormat, "ShareFormatPortrait", "ShareFormatStory", "ShareFormatSquare");
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

    static void FillChoices(System.Windows.Controls.ComboBox combo, int selected, params string[] keys)
    {
        combo.Items.Clear();
        foreach (var key in keys) combo.Items.Add(Loc.T(key));
        combo.SelectedIndex = Math.Clamp(selected, 0, keys.Length - 1);
    }

    void Translation_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Settings.TranslationEnabled = TranslationCheck.IsChecked == true;
        _settings.ScheduleSave();
        _lyrics.RefreshTranslation();
    }

    void DiscordLyrics_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Settings.DiscordShowLyrics = DiscordLyricsCheck.IsChecked == true;
        _settings.ScheduleSave();
        App.NowPlaying?.Apply(_settings.Settings);
    }

    void ShareStyle_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || ShareStyleCombo.SelectedIndex < 0) return;
        _settings.Settings.ShareStyle = ShareStyleCombo.SelectedIndex;
        _settings.ScheduleSave();
    }

    void ShareFormat_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || ShareFormatCombo.SelectedIndex < 0) return;
        _settings.Settings.ShareFormat = ShareFormatCombo.SelectedIndex;
        _settings.ScheduleSave();
    }

    // ------------------------------------------------------------------
    // Design library
    // ------------------------------------------------------------------

    readonly System.Collections.Generic.List<Glowline.Models.DesignProfile> _libraryDesigns = new();

    // The built-in default name is stored in Spanish; show it in the app language.
    static string DesignDisplayName(string name) =>
        name == "Mi diseño" ? Loc.T("DefaultDesignName") : name;

    async void RefreshDesignLibrary()
    {
        try
        {
            var list = await new DesignLibraryService().ListAsync();
            _libraryDesigns.Clear();
            _libraryDesigns.AddRange(list);
            var keep = DesignLibraryCombo.SelectedIndex;
            DesignLibraryCombo.Items.Clear();
            foreach (var design in _libraryDesigns) DesignLibraryCombo.Items.Add(DesignDisplayName(design.Name));
            if (_libraryDesigns.Count > 0)
                DesignLibraryCombo.SelectedIndex = Math.Clamp(keep, 0, _libraryDesigns.Count - 1);
            DesignLibraryEmpty.Visibility = _libraryDesigns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Write("design library list: " + ex.Message);
        }
    }

    async void SaveDesignToLibrary_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = App.Designs.CloneActive();
            await new DesignLibraryService().SaveAsync(profile);
            ShowNotice(Loc.T("DesignSavedNotice"));
            RefreshDesignLibrary();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("Designer.Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void ApplyLibraryDesign_Click(object sender, RoutedEventArgs e)
    {
        var index = DesignLibraryCombo.SelectedIndex;
        if (index < 0 || index >= _libraryDesigns.Count) return;
        App.Designs.Apply(Glowline.Services.DesignService.Clone(_libraryDesigns[index]));
        PersistActiveDesign();
        RefreshDesignerUi();
        RefreshArtworkUi();
    }

    async void DeleteLibraryDesign_Click(object sender, RoutedEventArgs e)
    {
        var index = DesignLibraryCombo.SelectedIndex;
        if (index < 0 || index >= _libraryDesigns.Count) return;
        if (MessageBox.Show(this, Loc.T("DesignDeleteConfirm"), _libraryDesigns[index].Name,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await new DesignLibraryService().DeleteAsync(_libraryDesigns[index].Id);
            RefreshDesignLibrary();
        }
        catch (Exception ex)
        {
            Log.Write("design library delete: " + ex.Message);
        }
    }

    /// <summary>Applies the .glow file dropped on the window and keeps it in the library.</summary>
    async void OnDesignFileDropped(string path)
    {
        try
        {
            var package = await GlowPackageService.ImportAsync(path);
            App.Designs.Apply(package.Profile);
            PersistActiveDesign();
            RefreshDesignerUi();
            RefreshArtworkUi();
            await new DesignLibraryService().SaveAsync(package.Profile, package.PreviewPng);
            RefreshDesignLibrary();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("Designer.Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void InitDesignDrop()
    {
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = HasGlowFile(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files &&
                files[0].EndsWith(".glow", StringComparison.OrdinalIgnoreCase))
                OnDesignFileDropped(files[0]);
        };
    }

    static bool HasGlowFile(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files &&
        files[0].EndsWith(".glow", StringComparison.OrdinalIgnoreCase);

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
