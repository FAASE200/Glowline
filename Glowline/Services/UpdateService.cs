using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Glowline.Localization;
using Glowline.Models;

namespace Glowline.Services;

public enum UpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Downloading,
    Ready,
    Failed,
}

public sealed class UpdateInfo
{
    public Version Version { get; init; } = new(0, 0);
    public long AssetId { get; init; }
    public long Size { get; init; }
    public string? Digest { get; init; }
    public string Notes { get; init; } = "";
}

public sealed class UpdateState
{
    public UpdatePhase Phase { get; init; } = UpdatePhase.Idle;
    public string MessageKey { get; init; } = "";
    public object?[] MessageArgs { get; init; } = Array.Empty<object?>();
    public string Message => MessageKey.Length > 0 ? Loc.F(MessageKey, MessageArgs) : "";
    public double Progress { get; init; }
    public UpdateInfo? Info { get; init; }
}

public sealed class UpdateService
{
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    readonly SettingsService _settings;
    readonly ReleaseClient _release;
    readonly object _gate = new();
    readonly string _updatesDir;
    UpdateState _state = new() { Phase = UpdatePhase.Idle, MessageKey = "UpdateNotChecked" };
    string? _readyPath;
    bool _applyStarted;
    bool _autoStarted;
    int _checking;

    public UpdateService(SettingsService settings)
    {
        _settings = settings;
        _release = new ReleaseClient();
        _updatesDir = Path.Combine(SettingsService.DataDir, "updates");
    }

    public static Version CurrentVersion { get; } =
        Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0));

    public static string CurrentVersionText => Format(CurrentVersion);
    public UpdateState State { get { lock (_gate) return _state; } }
    public event EventHandler? Changed;

    public void StartAutoChecks()
    {
        if (_autoStarted) return;
        _autoStarted = true;
        _ = Task.Run(async () =>
        {
            try
            {
                CleanupOldDownloads();
                await Task.Delay(TimeSpan.FromSeconds(5));
                while (true)
                {
                    if (_settings.Settings.AutoUpdateEnabled)
                        await CheckAsync(manual: false, CancellationToken.None);
                    await Task.Delay(CheckInterval);
                }
            }
            catch (Exception ex)
            {
                Log.Write("update auto: " + ex.Message);
            }
        });
    }

    // Every update leaves its installer (tens of MB) in the updates folder.
    // On startup, installers for versions already installed are removed,
    // along with partial downloads left over from earlier sessions.
    void CleanupOldDownloads()
    {
        try
        {
            var dir = new DirectoryInfo(_updatesDir);
            if (!dir.Exists) return;
            foreach (var file in dir.GetFiles())
            {
                var delete = file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
                if (!delete && file.Name.StartsWith("GlowlineSetup-", StringComparison.OrdinalIgnoreCase) &&
                    file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var version = ReleaseClient.ParseVersion(
                        Path.GetFileNameWithoutExtension(file.Name)["GlowlineSetup-".Length..]);
                    delete = version is not null && version <= CurrentVersion;
                }

                if (!delete) continue;
                try
                {
                    file.Delete();
                    Log.Write($"update: removed {file.Name}");
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("update cleanup: " + ex.Message);
        }
    }

    public async Task CheckAsync(bool manual, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            SetState(UpdatePhase.Checking, "UpdateChecking");
            var release = await _release.GetLatestAsync(ct);
            if (release is null)
            {
                SetState(UpdatePhase.UpToDate, "UpdateNoUpdatesPublished");
                return;
            }

            var newer = release.Version > CurrentVersion;
            var asset = _release.FindAsset(release,
                $"GlowlineSetup-{Format(release.Version)}.exe",
                "Glowline.exe");
            if (asset is null)
            {
                // An older release without a Glowline installer (such as the
                // 0.9.35 bridge for 0.9.30) is not an error: this version is newer.
                if (!newer)
                {
                    lock (_gate) _readyPath = null;
                    SetState(UpdatePhase.UpToDate, "UpdateUpToDateFormat", new object?[] { CurrentVersionText });
                    return;
                }
                SetState(UpdatePhase.Failed, "UpdateMissingAsset");
                return;
            }

            string notes;
            if (newer)
            {
                if (!ReleaseClient.IsSha256Digest(asset.Digest))
                {
                    Log.Write($"update: missing or invalid digest in {release.Tag} ({asset.Digest ?? "null"})");
                    SetState(UpdatePhase.Failed, "UpdateHashMismatch");
                    return;
                }
                notes = await ResolveNotesAsync(release, ct);
            }
            else
            {
                notes = "";
            }

            var info = new UpdateInfo
            {
                Version = release.Version,
                AssetId = asset.Id,
                Size = asset.Size,
                Digest = asset.Digest,
                Notes = notes,
            };

            if (!newer)
            {
                lock (_gate) _readyPath = null;
                SetState(UpdatePhase.UpToDate, "UpdateUpToDateFormat",
                    new object?[] { CurrentVersionText }, 0, info);
                return;
            }

            Log.Write($"update: {release.Tag} available ({info.Size / 1048576.0:0.0} MB)");
            await DownloadAsync(info, asset, ct);
        }
        catch (OperationCanceledException)
        {
            SetState(UpdatePhase.Failed, "UpdateCancelled");
        }
        catch (Exception ex)
        {
            Log.Write("update check: " + ex.Message);
            SetState(UpdatePhase.Failed, "UpdateCheckFailed");
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    async Task<string> ResolveNotesAsync(ReleaseInfo release, CancellationToken ct)
    {
        var cached = release.Notes;
        if (!string.IsNullOrWhiteSpace(cached)) return cached;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            return await _release.GetReleaseNotesAsync(release.Tag, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Write("update notes: timeout");
            return "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write("update notes: " + ex.Message);
            return "";
        }
    }

    async Task DownloadAsync(UpdateInfo info, ReleaseAsset asset, CancellationToken ct)
    {
        Directory.CreateDirectory(_updatesDir);
        var fileName = Path.GetFileName(asset.Name);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"Glowline-{Format(info.Version)}.exe";
        var final = Path.Combine(_updatesDir, fileName);
        if (File.Exists(final))
        {
            var existingHash = await HashFileAsync(final, ct);
            if (ReleaseClient.DigestMatches(info.Digest, existingHash))
            {
                lock (_gate) _readyPath = final;
                SetState(UpdatePhase.Ready, "UpdateReadyFormat",
                    new object?[] { Format(info.Version) }, 1, info, final);
                return;
            }
            File.Delete(final);
        }

        try
        {
            await _release.DownloadAsync(asset, final, progress =>
            {
                SetState(UpdatePhase.Downloading, "UpdateDownloadingFormat",
                    new object?[] { Format(info.Version), info.Size / 1048576.0 * progress }, progress, info);
            }, ct);
            lock (_gate) _readyPath = final;
            Log.Write($"update: ready {final}");
            SetState(UpdatePhase.Ready, "UpdateReadyFormat",
                new object?[] { Format(info.Version) }, 1, info, final);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { if (File.Exists(final)) File.Delete(final); } catch { }
            Log.Write("update download: " + ex.Message);
            SetState(UpdatePhase.Failed, ex is InvalidDataException ? "UpdateHashMismatch" : "UpdateDownloadFailed",
                info: info);
        }
    }

    public bool ApplyPendingOnExit(bool restart)
    {
        string? path;
        lock (_gate)
        {
            if (_applyStarted) return false;
            path = _readyPath;
        }

        if (path is null || !File.Exists(path)) return false;
        var target = Environment.ProcessPath;
        if (string.IsNullOrEmpty(target)) return false;

        if (Path.GetFileName(path).StartsWith("GlowlineSetup-", StringComparison.OrdinalIgnoreCase))
            return StartInstallerApply(path, target, restart);

        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(target) ?? "",
            };
            psi.ArgumentList.Add("--apply-update");
            psi.ArgumentList.Add(target);
            psi.ArgumentList.Add("--wait-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            if (restart) psi.ArgumentList.Add("--restart");
            Process.Start(psi);
            _applyStarted = true;
            Log.Write($"update: applier started restart={restart}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("update apply start: " + ex.Message);
            return false;
        }
    }

    bool StartInstallerApply(string setup, string target, bool restart)
    {
        var localInstall = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Glowline", "Glowline.exe");
        var localInstallDir = Path.GetDirectoryName(localInstall) ?? "";
        var targetDir = Path.GetDirectoryName(target) ?? "";
        var perUserInstall = string.Equals(targetDir, localInstallDir, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(target, localInstall, StringComparison.OrdinalIgnoreCase);
        var perMachine = IsUnderProgramFiles(target);

        if (perMachine && !restart)
        {
            Log.Write("update installer: per-machine install needs user confirmation");
            return false;
        }

        var appToStart = perUserInstall ? target : localInstall;
        var wait = $"Wait-Process -Id {Environment.ProcessId} -Timeout 30 -ErrorAction SilentlyContinue; ";
        var start = restart
            ? $"if (Test-Path -LiteralPath {PsQuote(appToStart)}) {{ Start-Process -FilePath {PsQuote(appToStart)} }}"
            : "";
        var script = perMachine
            ? wait + $"Start-Process -FilePath {PsQuote(setup)}"
            : wait +
              $"Start-Process -FilePath {PsQuote(setup)} -ArgumentList '/quiet','/norestart' -Wait; " +
              start;

        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
            Process.Start(psi);
            _applyStarted = true;
            Log.Write($"update installer: setup={setup} app={appToStart} silent={!perMachine} restart={restart}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("update installer start: " + ex.Message);
            return false;
        }
    }

    static bool IsUnderProgramFiles(string path)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return (!string.IsNullOrEmpty(programFiles) &&
                path.StartsWith(programFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(programFilesX86) &&
                path.StartsWith(programFilesX86 + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    public static void RunApplyUpdate(string target, string? waitPid, bool restart)
    {
        var self = Environment.ProcessPath;
        Log.Write($"update apply: target={target} pid={waitPid} restart={restart}");
        if (string.IsNullOrEmpty(self)) return;

        if (int.TryParse(waitPid, out var pid))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.WaitForExit(20000);
            }
            catch
            {
            }
        }

        Thread.Sleep(500);
        if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target),
                StringComparison.OrdinalIgnoreCase))
        {
            if (restart) StartTarget(target);
            return;
        }

        var temp = target + ".update-new.tmp";
        var backup = target + ".previous";
        var copied = false;
        for (var attempt = 0; attempt < 80 && !copied; attempt++)
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
                File.Copy(self, temp, overwrite: true);
                copied = true;
            }
            catch (IOException)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
            catch (Exception ex)
            {
                Log.Write("update apply copy: " + ex.Message);
                break;
            }
        }

        if (!copied)
        {
            Log.Write("update apply: could not prepare the new executable");
            TryDelete(temp);
            return;
        }

        if (!TrySwapToNewBinary(target, temp, backup))
        {
            TryDelete(temp);
            Log.Write("update apply: could not replace the executable; keeping the installed one");
            return;
        }

        Log.Write("update apply: executable replaced");
        if (restart) StartTarget(target);
    }

    static bool TrySwapToNewBinary(string target, string temp, string backup)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                File.Replace(temp, target, backup, ignoreMetadataErrors: true);
                TryDelete(backup + ".tmp");
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
            catch (Exception ex)
            {
                Log.Write("update apply replace: " + ex.Message);
                return false;
            }
        }
        return false;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static void StartTarget(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target) ?? "",
            });
        }
        catch (Exception ex)
        {
            Log.Write("update restart: " + ex.Message);
        }
    }

    static Version Normalize(Version version) =>
        new(version.Major, version.Minor, version.Build < 0 ? 0 : version.Build);

    static string Format(Version version) => version.ToString(3);

    public static bool IsSha256Digest(string? digest) => ReleaseClient.IsSha256Digest(digest);

    static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    void SetState(UpdatePhase phase, string messageKey, object?[]? messageArgs = null,
        double progress = 0, UpdateInfo? info = null, string? readyPath = null)
    {
        lock (_gate)
        {
            _state = new UpdateState
            {
                Phase = phase,
                MessageKey = messageKey,
                MessageArgs = messageArgs ?? Array.Empty<object?>(),
                Progress = progress,
                Info = info,
            };
            if (readyPath is not null) _readyPath = readyPath;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(new Action(RaiseChanged));
        else
            RaiseChanged();
    }

    void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Write("update changed: " + ex.Message);
        }
    }
}
