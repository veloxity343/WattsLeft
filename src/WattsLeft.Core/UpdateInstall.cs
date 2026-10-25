using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace WattsLeft;

/// <summary>
/// Updates WattsLeft in place, once the user has said yes: downloads the new release's zip, checks it against
/// the checksum published with it, unpacks it beside the data folder, then hands over to the new version, which
/// copies itself into this one's folder once this one has closed, and starts.
/// </summary>
/// <remarks>
/// Everything comes from this repository's release on GitHub, over HTTPS, from addresses built from the version
/// number, never taken from a reply. GitHub serves release files from its own download hosts, so redirects are
/// followed only to those. A zip that doesn't match its checksum, is bigger than any release would be, or doesn't
/// hold WattsLeft is refused, and nothing is replaced. Replacing is undone if it fails partway.
/// </remarks>
internal static class UpdateInstall
{
    private const string Downloads = "https://github.com/veloxity343/WattsLeft/releases/download/";
    /// <summary>A release is about 16 MB; anything far bigger isn't one.</summary>
    private const long MaxZipBytes = 100L * 1024 * 1024;
    private const int MaxChecksumBytes = 1024;
    /// <summary>
    /// The longest a whole update may take to download, and the longest a download may go without receiving
    /// anything. HttpClient's own timeout only covers the reply's headers, not reading its body, so both are
    /// cancellation deadlines passed to every request and read: a connection that stalls fails, rather than
    /// waiting for ever and blocking another try.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5), StallLimit = TimeSpan.FromSeconds(30);
    private const string ExeName = "WattsLeft.exe";

    /// <summary>Where updates are unpacked before they're installed: inside the data folder, never the app's.</summary>
    private static string UpdatesFolder => Path.Combine(Log.Folder, "update");

    /// <summary>The folder the running app is in, which an update replaces.</summary>
    public static string AppFolder => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Whether this app's folder can be written to, which updating it needs (not so in Program Files).</summary>
    public static bool CanReplaceApp()
    {
        try
        {
            string probe = Path.Combine(AppFolder, $".update-check-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads <paramref name="version"/>, checks it and unpacks it, reporting progress from 0 to 1.
    /// </summary>
    /// <returns>The folder holding the new version, ready for <see cref="HandOver"/>.</returns>
    public static async Task<string> PrepareAsync(Version version, IProgress<double> progress)
    {
        string v = version.ToString(3);
        string zipName = $"WattsLeft-{v}-win-x64.zip";
        var zipAddress = new Uri($"{Downloads}v{v}/{zipName}");
        var checksumAddress = new Uri($"{zipAddress}.sha256");

        string folder = Path.Combine(UpdatesFolder, v);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        string zipPath = Path.Combine(folder, zipName);

        using var http = new HttpClient { Timeout = Deadline };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WattsLeft/{UpdateCheck.Current.ToString(3)}");
        // The whole download's deadline, and a stall limit that every read pushes back.
        using var deadline = new CancellationTokenSource(Deadline);
        using var stall = new CancellationTokenSource(StallLimit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, stall.Token);
        var token = linked.Token;

        // The checksum first: a release without one can't be checked, so isn't installed.
        string expected;
        using (var reply = await http.GetAsync(checksumAddress, HttpCompletionOption.ResponseHeadersRead, token))
        {
            CheckReply(reply);
            if (reply.Content.Headers.ContentLength > MaxChecksumBytes)
                throw new InvalidDataException("The checksum file is too big");
            // Read to the limit and no further, whether or not the reply said how long it is.
            await using var body = await reply.Content.ReadAsStreamAsync(token);
            var bytes = new byte[MaxChecksumBytes + 1];
            int filled = 0, got;
            while (filled < bytes.Length && (got = await body.ReadAsync(bytes.AsMemory(filled), token)) > 0)
            {
                filled += got;
                stall.CancelAfter(StallLimit);
            }
            if (filled > MaxChecksumBytes)
                throw new InvalidDataException("The checksum file is too big");
            string text = System.Text.Encoding.ASCII.GetString(bytes, 0, filled);
            expected = text.Length >= 64 && text[..64].All(Uri.IsHexDigit)
                ? text[..64].ToLowerInvariant()
                : throw new InvalidDataException("The checksum file doesn't hold a SHA-256");
        }

        string actual;
        stall.CancelAfter(StallLimit);
        using (var reply = await http.GetAsync(zipAddress, HttpCompletionOption.ResponseHeadersRead, token))
        {
            CheckReply(reply);
            long? length = reply.Content.Headers.ContentLength;
            if (length > MaxZipBytes)
                throw new InvalidDataException($"The download is too big ({length} bytes)");
            await using var download = await reply.Content.ReadAsStreamAsync(token);
            await using var file = File.Create(zipPath);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await download.ReadAsync(buffer, token)) > 0)
            {
                stall.CancelAfter(StallLimit);
                total += read;
                if (total > MaxZipBytes)
                    throw new InvalidDataException("The download is too big");
                sha.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), token);
                if (length > 0)
                    progress.Report((double)total / length.Value);
            }
            actual = Convert.ToHexStringLower(sha.GetHashAndReset());
        }
        if (actual != expected)
            throw new InvalidDataException("The download doesn't match its checksum");

        // Unpacking refuses any entry that would land outside the folder.
        string unpacked = Path.Combine(folder, "app");
        ZipFile.ExtractToDirectory(zipPath, unpacked);
        File.Delete(zipPath);
        string exe = Path.Combine(unpacked, ExeName);
        if (!File.Exists(exe))
            throw new InvalidDataException($"The download has no {ExeName}");
        if (UpdateCheck.ParseVersion(FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+', '-')[0]) != version)
            throw new InvalidDataException($"The download isn't version {v}");
        progress.Report(1);
        return unpacked;
    }

    /// <summary>Only GitHub, over HTTPS, may answer: release files come from its own download hosts.</summary>
    private static void CheckReply(HttpResponseMessage reply)
    {
        var at = reply.RequestMessage?.RequestUri;
        if (at is null || at.Scheme != Uri.UriSchemeHttps ||
            !(at.Host == "github.com" || at.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"The download was sent somewhere unexpected: {at?.Host}");
        reply.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Starts the new version to install itself over this app's folder once this process has exited. The caller
    /// then exits.
    /// </summary>
    public static void HandOver(string unpacked) =>
        Process.Start(new ProcessStartInfo(Path.Combine(unpacked, ExeName))
        {
            UseShellExecute = false,
            WorkingDirectory = unpacked,
            ArgumentList = { "--finish-update", AppFolder, Environment.ProcessId.ToString() },
        });

    /// <summary>
    /// Run by the new version, from where it was unpacked: waits for the old one to close, copies itself over
    /// the old folder, and starts from there. If copying fails, the old files are put back and the old version
    /// starts instead.
    /// </summary>
    public static void Finish(string appFolder, int oldProcessId)
    {
        try
        {
            using var old = Process.GetProcessById(oldProcessId);
            old.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }

        string from = AppFolder;
        var replaced = new List<(string Target, string Backup)>();
        var added = new List<string>();
        try
        {
            foreach (var file in Directory.GetFiles(from))
            {
                string target = Path.Combine(appFolder, Path.GetFileName(file));
                if (File.Exists(target))
                {
                    string backup = target + ".old";
                    File.Delete(backup);
                    // A file only just let go of can still be locked for a moment.
                    Retry(() => File.Move(target, backup));
                    replaced.Add((target, backup));
                }
                else
                {
                    added.Add(target);
                }
                File.Copy(file, target);
            }
            foreach (var (_, backup) in replaced)
                TryDelete(backup);
            Log.Write($"Updated to {UpdateCheck.Current.ToString(3)}", Log.HistoryFile);
        }
        catch (Exception ex)
        {
            Log.Write($"Installing the update failed, so the old version was put back: {ex.GetType().Name}: {ex.Message}");
            foreach (var target in added)
                TryDelete(target);
            foreach (var (target, backup) in replaced)
            {
                TryDelete(target);
                try
                {
                    File.Move(backup, target);
                }
                catch (Exception restoreError)
                {
                    Log.Write($"Couldn't put back {target}: {restoreError.Message}");
                }
            }
        }
        Process.Start(new ProcessStartInfo(Path.Combine(appFolder, ExeName)) { UseShellExecute = false, WorkingDirectory = appFolder });
    }

    /// <summary>Removes unpacked updates once the app runs from its own folder again.</summary>
    public static void CleanUp()
    {
        if (!Directory.Exists(UpdatesFolder) || AppFolder.StartsWith(UpdatesFolder, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            Directory.Delete(UpdatesFolder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The new version may still be closing; the next start tries again.
        }
    }

    private static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
