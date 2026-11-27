using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Glowline.Android.Platforms;
using Glowline.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using AppVersion = System.Version;

namespace Glowline.Android.Services;

public sealed class AndroidUpdateService
{
    public static AndroidUpdateService Instance { get; private set; } = null!;

    readonly AndroidSettingsStore _settings;
    readonly ReleaseClient _client;
    readonly string _downloadDirectory = Path.Combine(FileSystem.CacheDirectory, "updates");
    string? _readyPath;
    bool _autoStarted;
    string _messageKey = "UpdateNotChecked";
    object?[] _messageArgs = Array.Empty<object?>();

    public event Action? Changed;
    public AppVersion CurrentVersion { get; }
    public long CurrentVersionCode => long.TryParse(AppInfo.Current.BuildString, out var code) ? code : 0;
    public string CurrentVersionText => CurrentVersion.ToString(3);
    public AppVersion? AvailableVersion { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsDownloading { get; private set; }
    public bool IsReady => !string.IsNullOrEmpty(_readyPath);
    public double Progress { get; private set; }
    public string Message => MobileLoc.F(_messageKey, _messageArgs);

    private AndroidUpdateService(AndroidSettingsStore settings)
    {
        _settings = settings;
        _client = new ReleaseClient();
        var current = AppInfo.Current.Version;
        CurrentVersion = new AppVersion(current.Major, current.Minor, current.Build < 0 ? 0 : current.Build);
    }

    public static AndroidUpdateService Create(AndroidSettingsStore settings)
    {
        Instance = new AndroidUpdateService(settings);
        return Instance;
    }

    public void StartAutoChecks()
    {
        if (_autoStarted) return;
        _autoStarted = true;
        _ = AutoLoopAsync();
    }

    async Task AutoLoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            while (true)
            {
                if (_settings.Settings.AutoUpdateEnabled) await CheckAsync();
                await Task.Delay(TimeSpan.FromHours(6));
            }
        }
        catch (Exception ex) { Log.Write("Android update auto: " + ex.Message); }
    }

    public async Task CheckAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Progress = 0;
        SetMessage("UpdateChecking");
        try
        {
            var release = await _client.GetLatestAsync();
            if (release is null)
            {
                SetMessage("UpdateNoUpdatesPublished");
                return;
            }
            var version = ReleaseClient.Format(release.Version);
            var asset = _client.FindAsset(release,
                $"Glowline-{version}-android-universal-debug.apk",
                $"Glowline-{version}-android-universal.apk");
            if (asset is null)
            {
                SetMessage("Android.UpdateMissingApk");
                return;
            }
            AvailableVersion = release.Version;
            if (release.Version <= CurrentVersion)
            {
                _readyPath = null;
                SetMessage("UpdateUpToDateFormat", CurrentVersionText);
                return;
            }
            if (!ReleaseClient.IsSha256Digest(asset.Digest))
            {
                SetMessage("UpdateHashMismatch");
                return;
            }

            IsDownloading = true;
            SetMessage("UpdateDownloadingFormat", version, 0.0);
            Directory.CreateDirectory(_downloadDirectory);
            var destination = Path.Combine(_downloadDirectory,
                $"Glowline-{version}-android-universal.apk");
            await _client.DownloadAsync(asset, destination, value =>
            {
                Progress = value;
                SetMessage("UpdateDownloadingFormat", version, asset.Size / 1048576.0 * value);
            });
            _readyPath = destination;
            IsDownloading = false;
            Progress = 1;
            SetMessage("UpdateReadyFormat", version);
        }
        catch (Exception ex)
        {
            IsDownloading = false;
            Log.Write("Android update check: " + ex.Message);
            SetMessage(ex is InvalidDataException ? "UpdateHashMismatch" : "UpdateCheckFailed");
        }
        finally
        {
            IsBusy = false;
            RaiseChanged();
        }
    }

    public async Task InstallDownloadedAsync()
    {
        if (string.IsNullOrEmpty(_readyPath) || !File.Exists(_readyPath))
            throw new InvalidOperationException(MobileLoc.T("Android.UpdateNotDownloaded"));
        var context = Platform.AppContext;
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && !context.PackageManager.CanRequestPackageInstalls())
        {
            context.StartActivity(new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources,
                global::Android.Net.Uri.Parse("package:" + context.PackageName)).AddFlags(ActivityFlags.NewTask));
            SetMessage("Android.UpdateUnknownSources");
            RaiseChanged();
            return;
        }

        ValidateArchive(context, _readyPath);
        var packageInstaller = context.PackageManager.PackageInstaller;
        var length = new FileInfo(_readyPath).Length;
        var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        parameters.SetAppPackageName(context.PackageName);
        parameters.SetSize(length);
        parameters.SetRequireUserAction((int)PackageInstallUserAction.Required);
        var sessionId = packageInstaller.CreateSession(parameters);
        using var session = packageInstaller.OpenSession(sessionId);
        await using (var input = File.OpenRead(_readyPath))
        await using (var output = session.OpenWrite("base.apk", 0, length))
        {
            await input.CopyToAsync(output);
        }
        var statusIntent = new Intent(context, typeof(UpdateInstallReceiver));
        statusIntent.SetAction(UpdateInstallReceiver.ActionStatus);
        var pending = PendingIntent.GetBroadcast(context, sessionId, statusIntent,
            PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent);
        session.Commit(pending.IntentSender);
        SetMessage("Android.UpdateInstallConfirmation");
        RaiseChanged();
    }

    void ValidateArchive(Context context, string path)
    {
        var flags = OperatingSystem.IsAndroidVersionAtLeast(28)
            ? PackageInfoFlags.SigningCertificates
            : (PackageInfoFlags)0;
        var archive = context.PackageManager.GetPackageArchiveInfo(path, flags)
            ?? throw new InvalidDataException(MobileLoc.T("Android.UpdateInvalidApk"));
        if (!string.Equals(archive.PackageName, context.PackageName, StringComparison.Ordinal))
            throw new InvalidDataException(MobileLoc.T("Android.UpdateWrongPackage"));
        if (archive.LongVersionCode <= CurrentVersionCode)
            throw new InvalidDataException(MobileLoc.T("Android.UpdateNotNewer"));
        if (OperatingSystem.IsAndroidVersionAtLeast(28) && archive.SigningInfo is { } signing)
        {
            var current = context.PackageManager.GetPackageInfo(context.PackageName, flags);
            var expected = current.SigningInfo?.GetApkContentsSigners()?.Select(SignatureHash).ToArray();
            var actual = signing.GetApkContentsSigners()?.Select(SignatureHash).ToArray();
            if (expected is null || actual is null || !expected.SequenceEqual(actual))
                throw new InvalidDataException(MobileLoc.T("Android.UpdateSignatureMismatch"));
        }
    }

    static string SignatureHash(Signature signature)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(signature.ToByteArray()));
    }

    void SetMessage(string key, params object?[] args)
    {
        _messageKey = key;
        _messageArgs = args;
    }
    void RaiseChanged() => MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
}
