using System;
using Android.App;
using Android.Service.Notification;

namespace LyricGlow.Android.Platforms;

[Service(Name = "LyricGlow.Android.Platforms.MediaNotificationListener", Label = "Glowline media sessions", Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE", Exported = false)]
[IntentFilter(new[] { "android.service.notification.NotificationListenerService" })]
public sealed class MediaNotificationListener : NotificationListenerService
{
    public static event Action? EnabledChanged;

    public override void OnListenerConnected()
    {
        base.OnListenerConnected();
        EnabledChanged?.Invoke();
    }

    public override void OnListenerDisconnected()
    {
        base.OnListenerDisconnected();
        EnabledChanged?.Invoke();
    }
}
