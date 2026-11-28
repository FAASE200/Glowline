using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Glowline.Models;

namespace Glowline.Services;

/// <summary>
/// Creates a card to share a line of the song (cover, cover colors, the
/// line and the title) in a vertical 4:5 format, which looks best on
/// Instagram and TikTok.
/// </summary>
public static class ShareCardService
{
    public const int Width = 1080;
    public const int Height = 1350;

    public static string OutputDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Glowline");

    /// <summary>Look of the card.</summary>
    public enum CardStyle { Cover, Gradient, Light }

    /// <summary>Shape of the card.</summary>
    public enum CardFormat { Portrait, Story, Square }

    public static (int Width, int Height) SizeOf(CardFormat format) => format switch
    {
        CardFormat.Story => (1080, 1920),
        CardFormat.Square => (1080, 1080),
        _ => (Width, Height),
    };

    /// <summary>
    /// Draws the card, saves it as a PNG in Pictures\Glowline and copies it
    /// to the clipboard. Returns the file path.
    /// </summary>
    public static string CreateAndSave(LrcLine line, TrackInfo track, byte[]? artBytes, Color accent,
        CardStyle style = CardStyle.Cover, CardFormat format = CardFormat.Portrait)
    {
        var bitmap = Render(line, track, artBytes, accent, style, format);
        Directory.CreateDirectory(OutputDirectory);
        var path = UniquePath(OutputDirectory, SafeFileName($"{track.Artist} - {track.Title}"));
        using (var stream = File.Create(path))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
        }

        try
        {
            Clipboard.SetImage(bitmap);
        }
        catch (Exception ex)
        {
            // Another app may have the clipboard open: the PNG is already saved.
            Log.Write("share clipboard: " + ex.Message);
        }

        Log.Write("share: " + path);
        return path;
    }

    public static BitmapSource Render(LrcLine line, TrackInfo track, byte[]? artBytes, Color accent,
        CardStyle style = CardStyle.Cover, CardFormat format = CardFormat.Portrait)
    {
        var (width, height) = SizeOf(format);
        var square = format == CardFormat.Square;
        var story = format == CardFormat.Story;
        var light = style == CardStyle.Light;
        var cover = Decode(artBytes);

        var ink = light ? Color.FromRgb(0x14, 0x14, 0x18) : Colors.White;
        var inkSoft = light ? Color.FromArgb(0xB8, 0x14, 0x14, 0x18) : Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF);
        var inkFaint = light ? Color.FromArgb(0x90, 0x14, 0x14, 0x18) : Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF);

        var root = new Grid { Width = width, Height = height, ClipToBounds = true };

        // Background by style.
        if (style == CardStyle.Cover && cover is not null)
        {
            // The enlarged, blurred cover.
            root.Children.Add(new Image
            {
                Source = cover,
                Stretch = Stretch.UniformToFill,
                Margin = new Thickness(-160),
                Effect = new BlurEffect { Radius = 90, RenderingBias = RenderingBias.Quality },
            });
            root.Children.Add(new Border
            {
                Background = new LinearGradientBrush(
                    Color.FromArgb(0x66, 0, 0, 0), Color.FromArgb(0xD0, 0, 0, 0), 90),
            });
        }
        else if (light)
        {
            root.Background = new LinearGradientBrush(
                Lighten(accent, 0.93), Lighten(accent, 0.8), 60);
        }
        else
        {
            root.Background = new LinearGradientBrush(Darken(accent, 0.25), Darken(accent, 0.8), 60);
            root.Children.Add(new Border
            {
                Background = new RadialGradientBrush(
                    Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), Colors.Transparent)
                {
                    Center = new Point(0.15, 0.1),
                    GradientOrigin = new Point(0.15, 0.1),
                    RadiusX = 0.9,
                    RadiusY = 0.9,
                },
            });
        }

        var content = new Grid
        {
            Margin = square ? new Thickness(88, 84, 88, 80) : new Thickness(96, story ? 190 : 110, 96, story ? 170 : 96),
        };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(content);

        // Header: cover + title and artist.
        var header = new DockPanel { LastChildFill = true };
        if (cover is not null)
        {
            var size = square ? 140 : 168;
            var art = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(14),
                Background = new ImageBrush(cover) { Stretch = Stretch.UniformToFill },
                Margin = new Thickness(0, 0, 32, 0),
                Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Opacity = light ? 0.25 : 0.45 },
            };
            DockPanel.SetDock(art, Dock.Left);
            header.Children.Add(art);
        }
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Text(track.Title, square ? 38 : 44, FontWeights.Bold, ink));
        titles.Children.Add(Text(track.Artist, square ? 28 : 32, FontWeights.Normal, inkSoft, 8));
        header.Children.Add(titles);
        content.Children.Add(header);

        // The line (with its pronunciation and translation, when there are).
        var quote = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var lineText = string.IsNullOrWhiteSpace(line.Text) ? line.Background : line.Text;
        var extra = (line.Romanized.Length > 0 ? line.Romanized.Length : 0) +
                    (line.Translation.Length > 0 ? line.Translation.Length : 0);
        var fontSize = lineText.Length switch
        {
            <= 28 => 92,
            <= 60 => 76,
            <= 110 => 62,
            _ => 50,
        };
        if (square) fontSize = (int)(fontSize * 0.82);
        if (extra > 0) fontSize = (int)(fontSize * 0.88);
        quote.Children.Add(new Border
        {
            Width = 72,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(light ? Darken(accent, 0.1) : Lighten(accent, 0.25)),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 36),
        });
        quote.Children.Add(Text(lineText, fontSize, FontWeights.Bold, ink, lineHeight: fontSize * 1.18));
        if (line.Romanized.Length > 0)
            quote.Children.Add(Text(line.Romanized, fontSize * 0.42, FontWeights.Normal, inkFaint, 22, italic: true));
        if (line.Translation.Length > 0)
            quote.Children.Add(Text(line.Translation, fontSize * 0.46, FontWeights.Normal, inkSoft, 24));
        Grid.SetRow(quote, 1);
        content.Children.Add(quote);

        // Signature.
        var footer = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < 5; i++)
        {
            var bar = new Border
            {
                Width = 6,
                Height = i == 2 ? 30 : i % 2 == 1 ? 22 : 12,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(i == 2 ? Color.FromRgb(0xFF, 0x3B, 0x30) : ink),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            footer.Children.Add(bar);
        }
        footer.Children.Add(Text("Glowline", 28, FontWeights.SemiBold,
            light ? Color.FromArgb(0xE0, 0x14, 0x14, 0x18) : Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF), 0, left: 12));
        Grid.SetRow(footer, 2);
        content.Children.Add(footer);

        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(root);
        target.Freeze();
        return target;
    }

    static TextBlock Text(string text, double size, FontWeight weight, Color color,
        double top = 0, double lineHeight = double.NaN, bool italic = false, double left = 0) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        Foreground = new SolidColorBrush(color),
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(left, top, 0, 0),
        LineHeight = lineHeight,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static BitmapSource? Decode(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1200;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Write("share art: " + ex.Message);
            return null;
        }
    }

    static Color Darken(Color color, double amount) => Color.FromRgb(
        (byte)(color.R * (1 - amount)), (byte)(color.G * (1 - amount)), (byte)(color.B * (1 - amount)));

    static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)(color.R + (255 - color.R) * amount),
        (byte)(color.G + (255 - color.G) * amount),
        (byte)(color.B + (255 - color.B) * amount));

    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.', '-');
        if (clean.Length > 80) clean = clean[..80].TrimEnd(' ', '.');
        return clean.Length == 0 ? "Glowline" : clean;
    }

    static string UniquePath(string directory, string baseName)
    {
        var path = Path.Combine(directory, baseName + ".png");
        for (var i = 2; File.Exists(path) && i < 1000; i++)
            path = Path.Combine(directory, $"{baseName} ({i}).png");
        return path;
    }
}
