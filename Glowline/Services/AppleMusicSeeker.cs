using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace Glowline.Services;

static class AppleMusicSeeker
{
    public static Task<bool> SeekAsync(double positionSeconds) =>
        Task.Run(() => SeekCore(positionSeconds));

    public static Task<(bool Found, double Position)> ProbeAsync() =>
        Task.Run(() =>
        {
            try
            {
                if (FindSlider() is { } range)
                    return (true, range.Current.Value);
            }
            catch (Exception ex)
            {
                Log.Write("Apple Music probe: " + ex.Message);
            }
            return (false, 0);
        });

    static RangeValuePattern? FindSlider()
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!process.ProcessName.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase))
                    continue;

                var window = AutomationElement.RootElement.FindFirst(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
                if (window is null) continue;

                var slider = window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "LCDScrubber"));
                if (slider is null) continue;

                if (slider.TryGetCurrentPattern(RangeValuePattern.Pattern, out var patternObject) &&
                    patternObject is RangeValuePattern range && !range.Current.IsReadOnly)
                    return range;
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    static bool SeekCore(double positionSeconds)
    {
        try
        {
            var range = FindSlider();
            if (range is null)
            {
                Log.Write("Apple Music seek: no se encontró la barra de progreso");
                return false;
            }

            var target = Math.Clamp(positionSeconds, range.Current.Minimum, range.Current.Maximum);
            range.SetValue(target);
            Log.Write($"Apple Music seek (UI Automation): {target:F1}s");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Apple Music seek: " + ex.Message);
            return false;
        }
    }
}
