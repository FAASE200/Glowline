using System;
using System.IO;
using Glowline.Android.Services;
using Glowline.Models;
using Glowline.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Graphics;

namespace Glowline.Android;

public partial class MainPage : ContentPage
{
    readonly IDispatcherTimer _timer;
    byte[]? _lastCover;
    string _lastTrackKey = "";
    bool _coverUsingA = true;

    AppState State => AppState.Instance;

    public MainPage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, __) => Refresh();
    }

    void ApplyPalette()
    {
        BackgroundColor = MobileTheme.PageBackground;
        foreach (var card in new[] { HeroCard, PermissionsCard, UpdatesCard })
        {
            card.BackgroundColor = MobileTheme.CardBackground;
            card.BorderColor = MobileTheme.CardBorder;
        }

        AppTitle.TextColor = MobileTheme.Ink;
        BrandLogoDark.IsVisible = MobileTheme.IsDark;
        BrandLogoLight.IsVisible = !MobileTheme.IsDark;
        foreach (var label in new[] { TrackTitle, PermissionsTitle, UpdatesTitle })
            label.TextColor = MobileTheme.Ink;
        foreach (var label in new[] { VersionText, TrackArtist, StatusText, PlaybackStatusText, UpdateVersionText })
            label.TextColor = MobileTheme.Muted;
        foreach (var label in new[] { TrackSource, PositionText, UpdateStatusText, FooterText })
            label.TextColor = MobileTheme.Faint;

        CoverFrame.BackgroundColor = MobileTheme.CoverBackground;
        CoverPlaceholder.TextColor = MobileTheme.CoverGlyph;
        StatusPill.BackgroundColor = MobileTheme.PillBackground;

        SettingsButton.BackgroundColor = MobileTheme.SubtleButtonBackground;
        SettingsButton.TextColor = MobileTheme.Ink;
        PreviousButton.BackgroundColor = MobileTheme.SubtleButtonBackground;
        PreviousButton.TextColor = MobileTheme.Ink;
        NextButton.BackgroundColor = MobileTheme.SubtleButtonBackground;
        NextButton.TextColor = MobileTheme.Ink;
        NotificationAccessButton.BackgroundColor = MobileTheme.ButtonBackground;
        NotificationAccessButton.TextColor = MobileTheme.Ink;
        UpdateCheckButton.BackgroundColor = MobileTheme.ButtonBackground;
        UpdateCheckButton.TextColor = MobileTheme.Ink;

        PlayButton.BackgroundColor = MobileTheme.Accent;
        PlayButton.TextColor = MobileTheme.OnAccent;
        FullscreenButton.BackgroundColor = MobileTheme.Accent;
        FullscreenButton.TextColor = MobileTheme.OnAccent;
        UpdateInstallButton.BackgroundColor = MobileTheme.Accent;
        UpdateInstallButton.TextColor = MobileTheme.OnAccent;
        UpdateProgress.ProgressColor = MobileTheme.Accent;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        State.ApplyInterfaceTheme();
        ApplyPalette();
        State.Start();
        State.Changed += OnStateChanged;
        State.CoverChanged += OnStateChanged;
        MobileLoc.LanguageChanged += OnLanguageChanged;
        AndroidUpdateService.Instance.Changed += RefreshUpdate;
        AndroidUpdateService.Instance.StartAutoChecks();
        _timer.Start();
        RefreshText();
        Refresh();
        PlayIntro();
    }

    protected override void OnDisappearing()
    {
        _timer.Stop();
        State.Changed -= OnStateChanged;
        State.CoverChanged -= OnStateChanged;
        MobileLoc.LanguageChanged -= OnLanguageChanged;
        AndroidUpdateService.Instance.Changed -= RefreshUpdate;
        base.OnDisappearing();
    }

    async void PlayIntro()
    {
        ContentStack.CancelAnimations();
        ContentStack.Opacity = 0;
        ContentStack.TranslationY = 12;
        await Task.Delay(80);
        if (!IsLoaded)
        {
            ContentStack.Opacity = 1;
            ContentStack.TranslationY = 0;
            return;
        }
        await Task.WhenAll(
            ContentStack.FadeTo(1, 260, Easing.CubicOut),
            ContentStack.TranslateTo(0, 0, 300, Easing.CubicOut));
        ContentStack.Opacity = 1;
        ContentStack.TranslationY = 0;
    }

    void OnStateChanged() => MainThread.BeginInvokeOnMainThread(Refresh);

    void OnLanguageChanged() => MainThread.BeginInvokeOnMainThread(RefreshText);

    void RefreshText()
    {
        VersionText.Text = $"v{AppInfo.Current.Version} · Android {AppInfo.Current.BuildString} · fSS";
        SettingsButton.Text = "⚙";
        FullscreenButton.Text = MobileLoc.T("Android.Fullscreen");
        PermissionsTitle.Text = MobileLoc.T("Android.PermissionsTitle");
        PermissionsHint.Text = MobileLoc.T("Android.PermissionsHint");
        UpdatesTitle.Text = MobileLoc.T("UpdatesTitle");
        UpdateCheckButton.Text = MobileLoc.T("CheckNow");
        UpdateInstallButton.Text = MobileLoc.T("Android.UpdateInstall");
        FooterText.Text = MobileLoc.T("Android.Footer");
        Refresh();
    }

    void Refresh()
    {
        if (!IsLoaded) return;
        var track = State.Track;
        var key = track?.Key ?? "";
        if (key != _lastTrackKey)
        {
            _lastTrackKey = key;
            _ = AnimateTrackChange();
        }
        TrackTitle.Text = track?.Title ?? MobileLoc.T("NoPlayback");
        TrackArtist.Text = track?.Artist ?? "";
        TrackSource.Text = State.Lyrics.Found?.Source ?? track?.SourceAppId ?? "";

        if (!ReferenceEquals(_lastCover, State.CoverBytes))
        {
            _lastCover = State.CoverBytes;
            var bytes = _lastCover;
            var source = bytes is { Length: > 0 }
                ? ImageSource.FromStream(() => new MemoryStream(bytes))
                : null;
            var incoming = _coverUsingA ? CoverB : CoverA;
            var outgoing = _coverUsingA ? CoverA : CoverB;
            incoming.Source = source;
            incoming.Opacity = 0;
            _ = Task.WhenAll(incoming.FadeTo(1, 420, Easing.CubicOut), outgoing.FadeTo(0, 420, Easing.CubicIn));
            _coverUsingA = !_coverUsingA;
            CoverPlaceholder.IsVisible = source is null;
        }

        var playing = State.IsPlaying;
        PlayButton.Text = playing ? "Ⅱ" : "▶";
        StatusDot.Fill = new SolidColorBrush(playing ? Color.FromArgb("#30D158") : Color.FromArgb("#6B7280"));
        PlaybackStatusText.Text = track is null
            ? MobileLoc.T("NoSession")
            : playing ? MobileLoc.T("Playing") : MobileLoc.T("Paused");
        PositionText.Text = Clock(State.Position);

        StatusText.Text = State.LyricsState switch
        {
            LyricsState.Idle => MobileLoc.T("StatusNoPlayback"),
            LyricsState.Searching => MobileLoc.T("StatusSearchingLyrics"),
            LyricsState.Synced => MobileLoc.T("StatusLyricsReady"),
            LyricsState.Plain => MobileLoc.T("StatusPlain"),
            LyricsState.Instrumental => MobileLoc.T("StatusInstrumental"),
            LyricsState.NotFound => MobileLoc.T("StatusLyricsNotFoundFull"),
            LyricsState.Error => MobileLoc.T("StatusLyricsErrorFull"),
            _ => "",
        };
        RefreshPermissions();
        RefreshUpdate();
    }

    async Task AnimateTrackChange()
    {
        foreach (var view in new VisualElement[] { TrackTitle, TrackArtist, TrackSource })
        {
            view.CancelAnimations();
            view.Opacity = 0.25;
            view.TranslationY = 5;
        }
        await Task.WhenAll(
            TrackTitle.FadeTo(1, 220, Easing.CubicOut),
            TrackArtist.FadeTo(1, 260, Easing.CubicOut),
            TrackSource.FadeTo(1, 300, Easing.CubicOut),
            TrackTitle.TranslateTo(0, 0, 240, Easing.CubicOut),
            TrackArtist.TranslateTo(0, 0, 260, Easing.CubicOut),
            TrackSource.TranslateTo(0, 0, 280, Easing.CubicOut));
    }

    void RefreshPermissions()
    {
        var context = Platform.AppContext;
        NotificationAccessButton.Text = MobileLoc.F("Android.NotificationAccessState",
            AndroidPermissions.NotificationAccessEnabled(context) ? "✓" : "—");
    }

    void RefreshUpdate() => RefreshUpdateUi();

    void RefreshUpdateUi()
    {
        var updates = AndroidUpdateService.Instance;
        UpdateVersionText.Text = MobileLoc.F("CurrentVersionFormat", AppInfo.Current.Version);
        UpdateStatusText.Text = updates.Message;
        UpdateProgress.IsVisible = updates.IsDownloading;
        UpdateProgress.Progress = updates.Progress;
        UpdateInstallButton.IsVisible = updates.IsReady;
        UpdateCheckButton.IsEnabled = !updates.IsBusy;
    }

    async void OnSettings(object? sender, EventArgs e) => await Navigation.PushAsync(new SettingsPage());

    async void OnFullscreen(object? sender, EventArgs e)
    {
        try
        {
            await Navigation.PushAsync(new FullscreenPage());
        }
        catch (Exception ex)
        {
            Log.Write("Android fullscreen navigation: " + ex);
            await DisplayAlert("Glowline", ex.Message, MobileLoc.T("Android.Ok"));
        }
    }

    async void OnPlayPause(object? sender, EventArgs e)
    {
        await PlayButton.ScaleTo(0.9, 60, Easing.CubicOut);
        State.TogglePlayPause();
        Refresh();
        await PlayButton.ScaleTo(1, 150, Easing.CubicOut);
    }

    void OnPrevious(object? sender, EventArgs e)
    {
        State.Previous();
        Refresh();
    }

    void OnNext(object? sender, EventArgs e)
    {
        State.Next();
        Refresh();
    }

    void OnNotificationAccess(object? sender, EventArgs e) =>
        Platform.CurrentActivity?.StartActivity(AndroidPermissions.NotificationAccessIntent(Platform.AppContext));

    async void OnCheckUpdate(object? sender, EventArgs e) => await RunUpdateAsync(() => AndroidUpdateService.Instance.CheckAsync());

    async void OnInstallUpdate(object? sender, EventArgs e) => await RunUpdateAsync(() => AndroidUpdateService.Instance.InstallDownloadedAsync());

    async Task RunUpdateAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { await DisplayAlert("Glowline", ex.Message, MobileLoc.T("Android.Ok")); }
        RefreshUpdateUi();
    }

    static string Clock(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        return $"{(int)seconds / 60}:{(int)seconds % 60:00}";
    }
}
