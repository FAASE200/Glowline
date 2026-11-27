using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LyricGlow.Localization;
using LyricGlow.Models;
using LyricGlow.Services;

namespace LyricGlow;

/// <summary>
/// Ventanita flotante, siempre encima, con la línea actual y la siguiente.
/// Pensada para usar mientras se juega, se programa o se ve otra cosa sin
/// abrir la pantalla completa. No roba el foco ni aparece en Alt+Tab.
/// </summary>
public sealed class MiniLyricsWindow : Window
{
    static MiniLyricsWindow? _instance;

    readonly TextBlock _current;
    readonly TextBlock _next;
    readonly TextBlock _title;
    readonly Border _panel;
    readonly Button _close;
    readonly DispatcherTimer _timer;
    int _lineIndex = int.MinValue;
    LyricsState _state = LyricsState.Idle;
    string _trackKey = "";
    IReadOnlyList<LrcLine>? _lines;

    /// <summary>Muestra u oculta la mini-letra según el ajuste guardado.</summary>
    public static void Sync()
    {
        var enabled = App.Settings.Settings.MiniLyricsEnabled && !App.SnapshotMode;
        if (enabled)
        {
            _instance ??= new MiniLyricsWindow();
            if (!_instance.IsVisible) _instance.Show();
        }
        else if (_instance is not null)
        {
            _instance.Close();
            _instance = null;
        }
    }

    public static void Toggle()
    {
        App.Settings.Settings.MiniLyricsEnabled = !App.Settings.Settings.MiniLyricsEnabled;
        App.Settings.ScheduleSave();
        Sync();
        App.MainWindowRef?.RefreshExtrasUi();
    }

    MiniLyricsWindow()
    {
        Title = "Glowline mini";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 560;
        // Posición inicial aproximada para que no aparezca un instante en la esquina
        // superior izquierda; al cargar se ajusta con el alto real.
        WindowStartupLocation = WindowStartupLocation.Manual;
        var area = SystemParameters.WorkArea;
        Left = App.Settings.Settings.MiniLyricsLeft ?? area.Right - Width - 24;
        Top = App.Settings.Settings.MiniLyricsTop ?? area.Bottom - 130;

        _title = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 28, 6),
        };
        _current = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
        };
        _next = new TextBlock
        {
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0),
        };

        _close = new Button
        {
            Content = "✕",
            Width = 24,
            Height = 24,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0,
            ToolTip = Loc.T("MiniLyricsHide"),
            Focusable = false,
        };
        _close.Click += (_, __) => Toggle();

        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(_current);
        stack.Children.Add(_next);

        var grid = new Grid();
        grid.Children.Add(stack);
        grid.Children.Add(_close);

        _panel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x10, 0x10, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 12, 12, 14),
            Child = grid,
            Cursor = Cursors.SizeAll,
        };
        Content = _panel;

        MouseEnter += (_, __) => FadeClose(1);
        MouseLeave += (_, __) => FadeClose(0);
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
            SavePosition();
        };
        MouseRightButtonUp += (_, e) =>
        {
            ShowMenu();
            e.Handled = true;
        };

        SourceInitialized += (_, __) => MakeToolWindow();
        Loaded += (_, __) => PlaceWindow();
        Closed += (_, __) => _timer.Stop();

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, __) => Refresh();
        _timer.Start();
        Refresh();
    }

    void Refresh()
    {
        var lyrics = App.Lyrics;
        var track = lyrics.Track;

        // Con la pantalla completa abierta, la mini-letra solo estorba.
        var hidden = App.Primary?.IsShowingFullscreen == true || track is null;
        var visibility = hidden ? Visibility.Hidden : Visibility.Visible;
        if (Visibility != visibility) Visibility = visibility;
        if (track is null) return;

        var key = track.Key;
        if (key != _trackKey || !ReferenceEquals(_lines, lyrics.Lines) || _state != lyrics.State)
        {
            _trackKey = key;
            _lines = lyrics.Lines;
            _state = lyrics.State;
            _lineIndex = int.MinValue;
            _title.Text = track.DisplayName;
        }

        if (lyrics.State != LyricsState.Synced || lyrics.Lines.Count == 0)
        {
            var message = lyrics.State switch
            {
                LyricsState.Searching => Loc.T("MiniLyricsSearching"),
                LyricsState.Instrumental => "♪",
                LyricsState.Plain => Loc.T("MiniLyricsNotSynced"),
                _ => Loc.T("MiniLyricsNoLyrics"),
            };
            SetText(message, "");
            return;
        }

        var index = lyrics.GetLineIndex(lyrics.GetAdjustedPosition());
        if (index == _lineIndex) return;
        _lineIndex = index;
        var lines = lyrics.Lines;
        var current = index >= 0 ? Display(lines[index]) : "♪";
        var next = index + 1 < lines.Count ? lines[index + 1].Text : "";
        SetText(current, next);
    }

    static string Display(LrcLine line)
    {
        var text = line.Text.Length > 0 ? line.Text : line.Background;
        return line.Romanized.Length > 0 ? text + "\n" + line.Romanized : text;
    }

    void SetText(string current, string next)
    {
        if (_current.Text == current && _next.Text == next) return;
        _current.Text = current;
        _next.Text = next;
        _next.Visibility = next.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!App.Designs.Active.ReduceMotion)
        {
            _current.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(220)));
        }
    }

    void FadeClose(double to) =>
        _close.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(150)));

    void ShowMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _panel };
        var open = new MenuItem { Header = Loc.T("TrayOpen") };
        open.Click += (_, __) => App.ShowPrimaryWindow();
        menu.Items.Add(open);
        var share = new MenuItem { Header = Loc.T("LineMenuShare") };
        share.Click += (_, __) => App.Primary?.ShareLine();
        menu.Items.Add(share);
        menu.Items.Add(new Separator());
        var hide = new MenuItem { Header = Loc.T("MiniLyricsHide") };
        hide.Click += (_, __) => Toggle();
        menu.Items.Add(hide);
        menu.IsOpen = true;
    }

    void PlaceWindow()
    {
        var settings = App.Settings.Settings;
        var area = SystemParameters.WorkArea;
        var left = settings.MiniLyricsLeft ?? area.Right - Width - 24;
        var top = settings.MiniLyricsTop ?? area.Bottom - ActualHeight - 24;
        // Si la pantalla cambió y la posición guardada quedó fuera, se vuelve a la esquina.
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        if (left < virtualLeft || left > virtualLeft + SystemParameters.VirtualScreenWidth - 80 ||
            top < virtualTop || top > virtualTop + SystemParameters.VirtualScreenHeight - 40)
        {
            left = area.Right - Width - 24;
            top = area.Bottom - ActualHeight - 24;
        }
        Left = left;
        Top = top;
    }

    void SavePosition()
    {
        App.Settings.Settings.MiniLyricsLeft = Left;
        App.Settings.Settings.MiniLyricsTop = Top;
        App.Settings.ScheduleSave();
    }

    void MakeToolWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        // Sin botón en la barra de tareas ni en Alt+Tab.
        SetWindowLong(handle, GwlExStyle, style | WsExToolWindow);
    }

    const int GwlExStyle = -20;
    const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
