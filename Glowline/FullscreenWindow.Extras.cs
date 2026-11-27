using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glowline.Localization;
using Glowline.Models;
using Glowline.Services;

namespace Glowline;

// 0.9.40 extras: per-line menu (share as image and fine timing
// adjustment).
public partial class FullscreenWindow
{
    const int LineNudgeMs = 100;

    /// <summary>True while the lyrics view is full screen and in front.</summary>
    public bool IsShowingFullscreen =>
        _isOpen && IsVisible && IsActive && !_windowed && WindowState != WindowState.Minimized;

    void ShowLineMenu(FrameworkElement target, int index)
    {
        if (index < 0 || index >= _lines.Count) return;
        var offset = _lyrics.GetLineOffsetMs(index);
        var menu = new ContextMenu { PlacementTarget = target };

        var share = new MenuItem { Header = Loc.T("LineMenuShare") };
        share.Click += (_, __) => ShareLine(index);
        menu.Items.Add(share);
        menu.Items.Add(new Separator());

        var header = new MenuItem
        {
            Header = offset == 0
                ? Loc.T("LineMenuTiming")
                : Loc.F("LineMenuTimingFormat", FormatOffset(offset)),
            IsEnabled = false,
        };
        menu.Items.Add(header);

        var earlier = new MenuItem { Header = Loc.F("LineMenuEarlierFormat", LineNudgeMs) };
        earlier.Click += (_, __) => _lyrics.AdjustLineOffset(index, -LineNudgeMs);
        menu.Items.Add(earlier);

        var later = new MenuItem { Header = Loc.F("LineMenuLaterFormat", LineNudgeMs) };
        later.Click += (_, __) => _lyrics.AdjustLineOffset(index, LineNudgeMs);
        menu.Items.Add(later);

        var reset = new MenuItem { Header = Loc.T("LineMenuReset"), IsEnabled = offset != 0 };
        reset.Click += (_, __) => _lyrics.ResetLineOffset(index);
        menu.Items.Add(reset);

        menu.IsOpen = true;
    }

    static string FormatOffset(int ms) =>
        (ms > 0 ? "+" : "−") + (Math.Abs(ms) / 1000.0).ToString("0.0#", System.Globalization.CultureInfo.CurrentCulture) + " s";

    /// <summary>
    /// Shares the given line, or the one playing if -1. Uses the lyrics and
    /// cover from the services, so it also works with the full-screen view
    /// closed (for example, from the global shortcut).
    /// </summary>
    public async void ShareLine(int index = -1)
    {
        try
        {
            var track = _lyrics.Track;
            var lines = _lyrics.Lines;
            if (index < 0 && _lyrics.State == LyricsState.Synced)
                index = _lyrics.GetLineIndex(_lyrics.GetAdjustedPosition());
            if (track is null || index < 0 || index >= lines.Count)
            {
                App.MainWindowRef?.ShowNotice(Loc.T("ShareNothing"));
                return;
            }

            var line = lines[index];
            byte[]? art = null;
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(4));
                var cover = await App.CoverArt.GetAsync(track, cts.Token);
                if (cover.HasImage) art = cover.Bytes;
            }
            catch (Exception ex)
            {
                Log.Write("share cover: " + ex.Message);
            }

            var accent = _fusionBackground == Colors.White ? Color.FromRgb(0xFF, 0x3B, 0x30) : _fusionBackground;
            var path = ShareCardService.CreateAndSave(line, track, art, accent);
            App.MainWindowRef?.ShowNotice(Loc.F("ShareSavedFormat", path));
        }
        catch (Exception ex)
        {
            Log.Write("share: " + ex);
            App.MainWindowRef?.ShowNotice(Loc.T("ShareFailed"));
        }
    }

    public static void OpenShareFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(ShareCardService.OutputDirectory);
            Process.Start(new ProcessStartInfo(ShareCardService.OutputDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("share folder: " + ex.Message);
        }
    }
}
