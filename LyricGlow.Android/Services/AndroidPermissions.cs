using System;
using Android.Content;
using LyricGlow.Android.Platforms;

namespace LyricGlow.Android.Services;

public static class AndroidPermissions
{
    public static bool NotificationAccessEnabled(Context context)
    {
        var enabled = global::Android.Provider.Settings.Secure.GetString(context.ContentResolver, "enabled_notification_listeners");
        if (string.IsNullOrWhiteSpace(enabled)) return false;
        var component = typeof(MediaNotificationListener).FullName ?? "";
        return enabled.Contains(context.PackageName + "/" + component, StringComparison.OrdinalIgnoreCase)
            || enabled.Contains(component, StringComparison.OrdinalIgnoreCase);
    }

    public static Intent NotificationAccessIntent(Context context) =>
        new Intent(global::Android.Provider.Settings.ActionNotificationListenerSettings);
}
