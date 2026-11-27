using System;
using System.IO;
using Android.Graphics;
using Glowline.Services;
using AndroidColor = global::Android.Graphics.Color;
using AndroidPaint = global::Android.Graphics.Paint;
using AndroidRectF = global::Android.Graphics.RectF;

namespace Glowline.Android.Services;

public sealed class AndroidImageDecoder : IImageDecoder
{
    public (int Width, int Height) GetSize(byte[] bytes)
    {
        var options = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, options);
        return (options.OutWidth, options.OutHeight);
    }

    // Same idea as the desktop backdrop: a small center-cropped copy, blurred
    // twice so the screen picks a soft dominant color instead of a sharp photo.
    public static byte[]? CreateBackdrop(byte[]? bytes, bool blurred)
    {
        if (bytes is not { Length: > 0 }) return null;
        Bitmap? decoded = null;
        Bitmap? output = null;
        try
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, bounds);
            if (bounds.OutWidth < 8 || bounds.OutHeight < 8) return null;
            var sample = 1;
            while (bounds.OutWidth / sample > 640 || bounds.OutHeight / sample > 640) sample *= 2;
            decoded = BitmapFactory.DecodeByteArray(
                bytes, 0, bytes.Length, new BitmapFactory.Options { InSampleSize = sample });
            if (decoded is null) return null;

            var width = blurred ? 256 : 512;
            var height = blurred ? 144 : 288;
            output = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
            using (var canvas = new Canvas(output))
            {
                canvas.DrawColor(AndroidColor.Black);
                var scale = Math.Max(width / (float)decoded.Width, height / (float)decoded.Height);
                var drawW = decoded.Width * scale;
                var drawH = decoded.Height * scale;
                var left = (width - drawW) / 2f;
                var top = (height - drawH) / 2f;
                using var paint = new AndroidPaint
                {
                    FilterBitmap = true,
                    AntiAlias = true,
                    Dither = true,
                };
                canvas.DrawBitmap(
                    decoded,
                    null,
                    new AndroidRectF(left, top, left + drawW, top + drawH),
                    paint);
            }

            if (blurred)
            {
                var pixels = new int[width * height];
                output.GetPixels(pixels, 0, width, 0, 0, width, height);
                Blur(pixels, width, height, 24);
                using var blurredBitmap = Bitmap.CreateBitmap(
                    pixels, width, height, Bitmap.Config.Argb8888!)!;
                using var stream = new MemoryStream();
                blurredBitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);
                return stream.ToArray();
            }

            using var outputStream = new MemoryStream();
            output.Compress(Bitmap.CompressFormat.Png!, 100, outputStream);
            return outputStream.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            decoded?.Recycle();
            output?.Recycle();
        }
    }

    static void Blur(int[] pixels, int width, int height, int radius)
    {
        var buffer = new int[pixels.Length];
        for (var pass = 0; pass < 2; pass++)
        {
            BlurAxis(pixels, buffer, width, height, radius, horizontal: true);
            BlurAxis(buffer, pixels, width, height, radius, horizontal: false);
        }
    }

    static void BlurAxis(int[] source, int[] destination, int width, int height,
        int radius, bool horizontal)
    {
        var primary = horizontal ? width : height;
        var secondary = horizontal ? height : width;
        for (var line = 0; line < secondary; line++)
        {
            int r = 0, g = 0, b = 0, count = 0;
            var low = 0;
            var high = -1;
            for (var i = 0; i < primary; i++)
            {
                var targetHigh = Math.Min(primary - 1, i + radius);
                while (high < targetHigh)
                {
                    high++;
                    var pixel = source[horizontal ? line * width + high : high * width + line];
                    r += (pixel >> 16) & 0xFF;
                    g += (pixel >> 8) & 0xFF;
                    b += pixel & 0xFF;
                    count++;
                }
                var targetLow = Math.Max(0, i - radius);
                while (low < targetLow)
                {
                    var pixel = source[horizontal ? line * width + low : low * width + line];
                    r -= (pixel >> 16) & 0xFF;
                    g -= (pixel >> 8) & 0xFF;
                    b -= pixel & 0xFF;
                    count--;
                    low++;
                }
                destination[horizontal ? line * width + i : i * width + line] =
                    (255 << 24) | ((r / count) << 16) | ((g / count) << 8) | (b / count);
            }
        }
    }
}
