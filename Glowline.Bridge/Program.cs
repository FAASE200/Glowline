using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Glowline.Bridge;

/// <summary>
/// One-time bridge for people still on 0.9.30, the last version published
/// under the app's old name. That updater only knows the old installer file
/// name and, after running it, only restarts the old executable, so it can't
/// move to Glowline by itself.
///
/// The release workflows attach this program under the old installer file
/// name. When the 0.9.30 updater runs it (with /quiet /norestart for per-user
/// installs, or with no arguments for per-machine ones), it:
///   1. finds the newest published Glowline release that has an installer,
///   2. downloads the installer and checks it against GitHub's SHA-256 digest,
///   3. runs it the same way it was run (quiet or interactive); the Glowline
///      installer upgrades the old installation in place,
///   4. starts Glowline, which then migrates settings, designs and the
///      start-with-Windows option on its first run.
///
/// If anything fails before the installer runs, nothing has changed: the old
/// app is still installed, the 0.9.30 updater restarts it and tries again on
/// its next check.
/// </summary>
static partial class Program
{
    const string Owner = "FAASE200";
    const string Repo = "Glowline";
    const string ReleasesApi = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases?per_page=30";
    const string ReleasesPage = "https://github.com/" + Owner + "/" + Repo + "/releases";
    const long MaxApiBytes = 4L * 1024 * 1024;
    const long MaxSetupBytes = 1L * 1024 * 1024 * 1024;

    static readonly string WorkDir = Path.Combine(Path.GetTempPath(), "GlowlineBridge");
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "GlowlineBridge.log");

    static int Main(string[] args)
    {
        var quiet = args.Any(a => a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
                                  a.Equals("/q", StringComparison.OrdinalIgnoreCase) ||
                                  a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
                                  a.Equals("/passive", StringComparison.OrdinalIgnoreCase));
        var dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
        Log($"start {typeof(Program).Assembly.GetName().Version} args=[{string.Join(' ', args)}]");

        try
        {
            using var http = CreateClient();
            var installer = FindNewestInstallerAsync(http).GetAwaiter().GetResult()
                ?? throw new InvalidDataException("No published Glowline release has a Windows installer.");
            Log($"newest: {installer.Tag} {installer.Name} {installer.Size} bytes {installer.Digest}");

            if (dryRun)
            {
                Console.WriteLine($"{installer.Tag} {installer.Name} {installer.Size} {installer.Digest} {installer.Url}");
                return 0;
            }

            var setup = DownloadAsync(http, installer).GetAwaiter().GetResult();
            var code = RunSetup(setup, quiet);
            Log($"setup exit code {code}");
            // 3010 and 1641 are "success, a reboot is needed / was started".
            if (code is not (0 or 3010 or 1641))
                throw new InvalidOperationException($"The installer ended with code {code}.");

            TryDelete(setup);
            StartGlowline();
            return 0;
        }
        catch (Exception ex)
        {
            Log("error: " + ex);
            if (!quiet) ShowFailure();
            return 1;
        }
    }

    sealed record Installer(string Tag, Version Version, string Name, long Size, string Digest, string Url);

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Glowline-Bridge",
            typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0"));
        return http;
    }

    /// <summary>
    /// The newest non-draft release (pre-releases included, since the first
    /// Glowline version was published as one) that has GlowlineSetup-&lt;version&gt;.exe
    /// with a SHA-256 digest.
    /// </summary>
    static async Task<Installer?> FindNewestInstallerAsync(HttpClient http)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxApiBytes)
            throw new InvalidDataException("The GitHub response is too large.");
        var json = await response.Content.ReadAsByteArrayAsync();
        if (json.Length > MaxApiBytes) throw new InvalidDataException("The GitHub response is too large.");

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unexpected response from GitHub.");

        Installer? best = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            var tag = GetString(release, "tag_name");
            var version = ParseVersion(tag);
            if (version is null || (best is not null && version <= best.Version)) continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

            var expected = $"GlowlineSetup-{version.ToString(3)}.exe";
            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(GetString(asset, "name"), expected, StringComparison.OrdinalIgnoreCase)) continue;
                var digest = GetString(asset, "digest");
                var url = GetString(asset, "browser_download_url");
                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                if (!IsSha256(digest) || size <= 0 || size > MaxSetupBytes ||
                    !url.StartsWith("https://github.com/" + Owner + "/" + Repo + "/releases/download/", StringComparison.Ordinal))
                {
                    Log($"skipping {tag}: asset without a usable digest, size or URL");
                    break;
                }
                best = new Installer(tag, version, expected, size, digest, url);
                break;
            }
        }
        return best;
    }

    static async Task<string> DownloadAsync(HttpClient http, Installer installer)
    {
        Directory.CreateDirectory(WorkDir);
        var path = Path.Combine(WorkDir, installer.Name);
        var partial = path + ".part";
        TryDelete(partial);

        using (var response = await http.GetAsync(installer.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var file = File.Create(partial);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                total += read;
                if (total > installer.Size) throw new InvalidDataException("The download is larger than published.");
                sha.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read));
            }
            if (total != installer.Size) throw new InvalidDataException("The downloaded size does not match the published one.");
            var hash = Convert.ToHexString(sha.GetHashAndReset());
            if (!string.Equals(hash, installer.Digest[7..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download does not match the SHA-256 hash from GitHub.");
        }

        File.Move(partial, path, overwrite: true);
        Log($"downloaded and verified {path}");
        return path;
    }

    static int RunSetup(string setup, bool quiet)
    {
        var psi = new ProcessStartInfo(setup) { UseShellExecute = false, WorkingDirectory = WorkDir };
        if (quiet)
        {
            psi.ArgumentList.Add("/quiet");
            psi.ArgumentList.Add("/norestart");
        }
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("The installer did not start.");
        process.WaitForExit();
        return process.ExitCode;
    }

    static void StartGlowline()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Glowline", "Glowline.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Glowline", "Glowline.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Glowline", "Glowline.exe"),
        ];
        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe is null)
        {
            Log("Glowline.exe not found after installing");
            return;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
        Log("started " + exe);
    }

    static void ShowFailure()
    {
        const uint MB_YESNO = 0x4, MB_ICONWARNING = 0x30, IDYES = 6;
        var answer = MessageBoxW(IntPtr.Zero, FailureMessage(), "Glowline", MB_YESNO | MB_ICONWARNING);
        if (answer == IDYES)
        {
            try { Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { Log("open page: " + ex.Message); }
        }
    }

    /// <summary>Same ten languages as the app, picked from the Windows display language.</summary>
    static string FailureMessage() => (GetUserDefaultUILanguage() & 0x3FF) switch
    {
        0x0A => "No se pudo instalar Glowline automáticamente.\n\n¿Quieres abrir la página de descarga para instalarlo a mano?",
        0x16 => "Não foi possível instalar o Glowline automaticamente.\n\nQuer abrir a página de download para instalá-lo manualmente?",
        0x0C => "Glowline n'a pas pu être installé automatiquement.\n\nVoulez-vous ouvrir la page de téléchargement pour l'installer manuellement ?",
        0x07 => "Glowline konnte nicht automatisch installiert werden.\n\nMöchtest du die Download-Seite öffnen, um es manuell zu installieren?",
        0x10 => "Non è stato possibile installare Glowline automaticamente.\n\nVuoi aprire la pagina di download per installarlo manualmente?",
        0x11 => "Glowline を自動でインストールできませんでした。\n\nダウンロードページを開いて手動でインストールしますか？",
        0x12 => "Glowline을 자동으로 설치하지 못했습니다.\n\n다운로드 페이지를 열어 직접 설치하시겠어요?",
        0x04 => "无法自动安装 Glowline。\n\n要打开下载页面手动安装吗？",
        0x19 => "Не удалось автоматически установить Glowline.\n\nОткрыть страницу загрузки, чтобы установить его вручную?",
        _ => "Glowline could not be installed automatically.\n\nDo you want to open the download page to install it manually?",
    };

    static Version? ParseVersion(string tag)
    {
        var text = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        return Version.TryParse(text, out var v) && v.Build >= 0 ? new Version(v.Major, v.Minor, v.Build) : null;
    }

    static bool IsSha256(string digest) =>
        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
        digest.Length == 7 + 64 &&
        digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static void Log(string line)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); }
        catch { }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();
}
