using Android.App;
using Android.Content;
using Android.Content.PM;
using LyricGlow.Services;

namespace LyricGlow.Android.Platforms;

[BroadcastReceiver(Name = "LyricGlow.Android.Platforms.UpdateInstallReceiver", Label = "Glowline update installer", Exported = false)]
public sealed class UpdateInstallReceiver : BroadcastReceiver
{
    public const string ActionStatus = "LyricGlow.UpdateInstallStatus";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null || intent.Action != ActionStatus) return;
        if ((PackageInstallStatus)ResultCode == PackageInstallStatus.PendingUserAction &&
            intent.GetParcelableExtra(Intent.ExtraIntent) is Intent confirm)
        {
            confirm.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(confirm);
            return;
        }
        Log.Write($"Android update installer status: {ResultCode}");
    }
}
