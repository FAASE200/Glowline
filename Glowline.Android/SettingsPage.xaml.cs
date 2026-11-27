using System;
using System.Collections.Generic;
using System.Linq;
using Glowline.Android.Services;
using Glowline.Localization;
using Glowline.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Glowline.Android;

public partial class SettingsPage : ContentPage
{
    sealed class LanguageItem
    {
        public required AppLanguage Language { get; init; }
        public required string Text { get; init; }
        public override string ToString() => Text;
    }

    bool _loading;
    AppState State => AppState.Instance;

    public SettingsPage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        LanguagePicker.ItemsSource = new List<LanguageItem>(MobileLoc.Languages.Select(x => new LanguageItem
        {
            Language = x.Language,
            Text = x.NativeName,
        }));
        PresetPicker.ItemsSource = new[]
        {
            MobileLoc.T("Designer.Value.DesignLayout.Lateral"),
            MobileLoc.T("Designer.Value.DesignLayout.Fusion"),
            MobileLoc.T("Designer.Value.DesignLayout.Minimal"),
            MobileLoc.T("Android.PresetCinema"),
            MobileLoc.T("Designer.Value.DesignLayout.Karaoke"),
        };
        ThemePicker.ItemsSource = new[]
        {
            MobileLoc.T("ThemeLight"),
            MobileLoc.T("ThemeDark"),
        };
        WordAnimationPicker.ItemsSource = new[]
        {
            MobileLoc.T("Designer.Value.LyricAnimation.Fade"),
            MobileLoc.T("Designer.Value.LyricAnimation.Slide"),
            MobileLoc.T("Designer.Value.LyricAnimation.Zoom"),
            MobileLoc.T("Designer.Value.LyricAnimation.Bounce"),
            MobileLoc.T("Designer.Value.LyricAnimation.Rise"),
            MobileLoc.T("Designer.Value.LyricAnimation.Blur"),
            MobileLoc.T("Designer.Value.LyricAnimation.Elastic"),
            MobileLoc.T("Designer.Value.LyricAnimation.Pulse"),
            MobileLoc.T("Designer.Value.LyricAnimation.Wave"),
        };
        SourcePicker.ItemsSource = new[] { "Lrcmux", "LyricsPlus", "LRCLIB" };
        VerseTransitionPicker.ItemsSource = new[]
        {
            MobileLoc.T("Designer.Value.VerseTransition.Fade"),
            MobileLoc.T("Designer.Value.VerseTransition.Slide"),
            MobileLoc.T("Designer.Value.VerseTransition.Roll"),
            MobileLoc.T("Designer.Value.VerseTransition.Scale"),
            MobileLoc.T("Designer.Value.VerseTransition.None"),
        };
        CoverMotionPicker.ItemsSource = new[]
        {
            MobileLoc.T("Designer.Value.CoverAnimation.None"),
            MobileLoc.T("Designer.Value.CoverAnimation.Float"),
            MobileLoc.T("Designer.Value.CoverAnimation.Breathe"),
            MobileLoc.T("Designer.Value.CoverAnimation.Tilt"),
            MobileLoc.T("Designer.Value.CoverAnimation.Parallax"),
            MobileLoc.T("Designer.Value.CoverAnimation.Beat"),
        };
        EasingPicker.ItemsSource = new[]
        {
            MobileLoc.T("Designer.Value.EasingKind.EaseOut"),
            MobileLoc.T("Designer.Value.EasingKind.EaseInOut"),
            MobileLoc.T("Designer.Value.EasingKind.Linear"),
            MobileLoc.T("Designer.Value.EasingKind.Spring"),
            MobileLoc.T("Designer.Value.EasingKind.Bounce"),
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        State.ApplyInterfaceTheme();
        ApplyPalette();
        MobileLoc.LanguageChanged += RefreshText;
        _loading = true;
        TitleLabel.Text = MobileLoc.T("SettingsTitle");
        CloseButton.Text = "✕";
        LanguagePicker.SelectedIndex = Math.Max(0, ((IReadOnlyList<LanguageItem>)LanguagePicker.ItemsSource)
            .ToList().FindIndex(x => x.Language == State.Settings.Language));
        PresetPicker.SelectedIndex = Math.Clamp(State.Settings.MobilePresetIndex, 0, 4);
        ThemePicker.SelectedIndex = State.Settings.InterfaceTheme == InterfaceTheme.Light ? 0 : 1;
        SourcePicker.SelectedIndex = State.Settings.LyricsSource switch
        {
            LyricSource.Lrcmux => 0,
            LyricSource.LyricsPlus => 1,
            _ => 2,
        };
        LyricsSizeSlider.Value = Math.Clamp(State.Settings.FontScale, 0.7, 1.6) * 100;
        SyncMotion();
        AnimatedCoversSwitch.IsToggled = State.Settings.AnimatedCoversEnabled;
        AutoUpdateSwitch.IsToggled = State.Settings.AutoUpdateEnabled;
        GlobalOffsetSlider.Value = State.Settings.GlobalOffsetMs / 1000.0;
        TrackOffsetSlider.Value = State.Track is { } track && State.Settings.PerTrackOffsetMs.TryGetValue(track.Key, out var value)
            ? value / 1000.0 : 0;
        _loading = false;
        RefreshText();
    }

    protected override void OnDisappearing()
    {
        MobileLoc.LanguageChanged -= RefreshText;
        State.Save();
        base.OnDisappearing();
    }

    void SyncMotion()
    {
        WordAnimationPicker.SelectedIndex = Math.Clamp((int)State.Design.Animations.WordAnimation, 0, 8);
        MotionIntensitySlider.Value = Math.Clamp(State.Design.Animations.WordIntensity, 0, 1) * 100;
        MotionIntensityText.Text = $"{MotionIntensitySlider.Value:0}%";
        VerseTransitionPicker.SelectedIndex = Math.Clamp((int)State.Design.Animations.VerseTransition, 0, 4);
        CoverMotionPicker.SelectedIndex = Math.Clamp((int)State.Design.Animations.CoverAnimation, 0, 5);
        EasingPicker.SelectedIndex = Math.Clamp((int)State.Design.Animations.Easing, 0, 4);
    }

    void ApplyPalette()
    {
        BackgroundColor = MobileTheme.PageBackground;
        foreach (var card in new[] { AppearanceCard, LyricsCard, MotionCard, UpdatesCard })
        {
            card.BackgroundColor = MobileTheme.CardBackground;
            card.BorderColor = MobileTheme.CardBorder;
        }

        CloseButton.BackgroundColor = MobileTheme.ButtonBackground;
        CloseButton.TextColor = MobileTheme.Ink;
        TitleLabel.TextColor = MobileTheme.Ink;
        foreach (var label in new[] { AppearanceTitle, LyricsTitle, MotionTitle, UpdatesTitle, AnimatedCoversLabel, AutoUpdateLabel })
            label.TextColor = MobileTheme.Ink;
        foreach (var label in new[] { LanguageLabel, PresetLabel, ThemeLabel, LyricsSizeLabel, LyricsSizeText, WordAnimationLabel, AnimatedCoversHint, SourceLabel, GlobalOffsetLabel, TrackOffsetLabel, MotionIntensityLabel, MotionIntensityText, VerseTransitionLabel, CoverMotionLabel, EasingLabel, UpdateHint })
            label.TextColor = MobileTheme.Muted;

        foreach (var picker in new Picker[] { LanguagePicker, PresetPicker, ThemePicker, WordAnimationPicker, SourcePicker, VerseTransitionPicker, CoverMotionPicker, EasingPicker })
        {
            picker.TextColor = MobileTheme.Ink;
            picker.TitleColor = MobileTheme.FieldTitle;
            picker.BackgroundColor = MobileTheme.FieldBackground;
        }

        foreach (var slider in new Slider[] { LyricsSizeSlider, MotionIntensitySlider, GlobalOffsetSlider, TrackOffsetSlider })
        {
            slider.MinimumTrackColor = MobileTheme.Accent;
            slider.MaximumTrackColor = MobileTheme.Track;
            slider.ThumbColor = Colors.White;
        }

        AnimatedCoversSwitch.OnColor = MobileTheme.Accent;
        AutoUpdateSwitch.OnColor = MobileTheme.Accent;
    }

    void RefreshText()
    {
        var loading = _loading;
        _loading = true;
        ThemePicker.ItemsSource = new[]
        {
            MobileLoc.T("ThemeLight"),
            MobileLoc.T("ThemeDark"),
        };
        ThemePicker.SelectedIndex = State.Settings.InterfaceTheme == InterfaceTheme.Light ? 0 : 1;
        _loading = loading;
        AppearanceTitle.Text = MobileLoc.T("Android.Appearance");
        LanguageLabel.Text = MobileLoc.T("Language");
        LanguagePicker.Title = MobileLoc.T("Language");
        PresetLabel.Text = MobileLoc.T("Android.Preset");
        PresetPicker.Title = MobileLoc.T("Android.Preset");
        ThemeLabel.Text = MobileLoc.T("InterfaceTheme");
        ThemePicker.Title = MobileLoc.T("InterfaceTheme");
        LyricsSizeLabel.Text = MobileLoc.T("Size");
        LyricsSizeText.Text = $"{State.Settings.FontScale * 100:0}%";
        WordAnimationLabel.Text = MobileLoc.T("WordMotion");
        WordAnimationPicker.Title = MobileLoc.T("WordMotion");
        AnimatedCoversLabel.Text = MobileLoc.T("Android.AnimatedCovers");
        AnimatedCoversHint.Text = MobileLoc.T("Android.AnimatedCoversHint");
        LyricsTitle.Text = MobileLoc.T("SettingsSectionLyrics");
        SourceLabel.Text = MobileLoc.T("Android.LyricsSource");
        SourcePicker.Title = MobileLoc.T("Android.LyricsSource");
        GlobalOffsetLabel.Text = MobileLoc.F("Android.GlobalOffset", State.Settings.GlobalOffsetMs / 1000.0);
        TrackOffsetLabel.Text = MobileLoc.F("Android.TrackOffset",
            State.Track is { } track && State.Settings.PerTrackOffsetMs.TryGetValue(track.Key, out var value)
                ? value / 1000.0 : 0);
        MotionTitle.Text = MobileLoc.T("Android.Motion");
        MotionIntensityLabel.Text = MobileLoc.T("Android.MotionIntensity");
        VerseTransitionLabel.Text = MobileLoc.T("Android.VerseTransition");
        CoverMotionLabel.Text = MobileLoc.T("Android.CoverMotion");
        EasingLabel.Text = MobileLoc.T("Android.Easing");
        VerseTransitionPicker.Title = MobileLoc.T("Android.VerseTransition");
        CoverMotionPicker.Title = MobileLoc.T("Android.CoverMotion");
        EasingPicker.Title = MobileLoc.T("Android.Easing");
        UpdatesTitle.Text = MobileLoc.T("UpdatesTitle");
        AutoUpdateLabel.Text = MobileLoc.T("AutoUpdateCheckbox");
        UpdateHint.Text = MobileLoc.T("Android.UpdateHint");
    }

    void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_loading || LanguagePicker.SelectedItem is not LanguageItem item) return;
        State.SetLanguage(item.Language);
        RefreshText();
    }

    void OnPresetChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        State.SetPreset(PresetPicker.SelectedIndex);
        _loading = true;
        SyncMotion();
        _loading = false;
    }

    void OnInterfaceThemeChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        State.Settings.InterfaceTheme = ThemePicker.SelectedIndex == 0
            ? InterfaceTheme.Light
            : InterfaceTheme.Dark;
        State.Save();
        State.ApplyInterfaceTheme();
        ApplyPalette();
    }

    void OnSourceChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        State.Settings.LyricsSource = SourcePicker.SelectedIndex switch
        {
            0 => LyricSource.Lrcmux,
            1 => LyricSource.LyricsPlus,
            _ => LyricSource.Lrclib,
        };
        State.Save();
        State.Lyrics.ReloadSource();
    }

    void OnLyricsSize(object? sender, EventArgs e)
    {
        if (_loading) return;
        State.Settings.FontScale = Math.Clamp(LyricsSizeSlider.Value / 100.0, 0.7, 1.6);
        State.Save();
        LyricsSizeText.Text = $"{State.Settings.FontScale * 100:0}%";
    }

    void OnWordAnimationChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        var style = (LyricAnimation)Math.Clamp(WordAnimationPicker.SelectedIndex, 0, 8);
        State.Design.Animations.WordAnimation = style;
        State.Settings.WordAnimation = (WordAnimationStyle)(int)style;
        State.Save();
    }

    void OnMotionIntensity(object? sender, EventArgs e)
    {
        if (_loading) return;
        var value = Math.Clamp(MotionIntensitySlider.Value / 100.0, 0, 1);
        State.Design.Animations.WordIntensity = value;
        State.Settings.WordMotionStrength = value;
        MotionIntensityText.Text = $"{MotionIntensitySlider.Value:0}%";
        State.Save();
    }

    void OnVerseTransitionChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        State.Design.Animations.VerseTransition = (VerseTransition)Math.Clamp(VerseTransitionPicker.SelectedIndex, 0, 4);
        State.Save();
    }

    void OnCoverMotionChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        State.Design.Animations.CoverAnimation = (CoverAnimation)Math.Clamp(CoverMotionPicker.SelectedIndex, 0, 5);
        State.Save();
    }

    void OnEasingChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        State.Design.Animations.Easing = (EasingKind)Math.Clamp(EasingPicker.SelectedIndex, 0, 4);
        State.Save();
    }

    void OnAnimatedCovers(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        State.SetAnimatedCovers(e.Value);
    }

    void OnAutoUpdate(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        State.Settings.AutoUpdateEnabled = e.Value;
        State.Save();
    }

    void OnGlobalOffset(object? sender, EventArgs e)
    {
        if (_loading) return;
        State.Settings.GlobalOffsetMs = (int)Math.Round(GlobalOffsetSlider.Value * 1000);
        State.Save();
        RefreshText();
    }

    void OnTrackOffset(object? sender, EventArgs e)
    {
        if (_loading || State.Track is not { } track) return;
        State.Settings.PerTrackOffsetMs[track.Key] = (int)Math.Round(TrackOffsetSlider.Value * 1000);
        State.Save();
        RefreshText();
    }

    async void OnClose(object? sender, EventArgs e)
    {
        State.Save();
        var modals = Navigation.ModalStack;
        if (modals.Count > 0 && ReferenceEquals(modals[modals.Count - 1], this))
            await Navigation.PopModalAsync();
        else
            await Navigation.PopAsync();
    }
}
