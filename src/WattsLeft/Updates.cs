using Avalonia.Controls;
using Avalonia.Threading;
using WattsLeft.Ui;

namespace WattsLeft;

/// <summary>
/// Checks for a newer WattsLeft once a day while the user has it on, and on demand when they turn it on. A
/// newer version gets one pop-up; the tray menu and Settings offer it until the user updates, which, once they
/// confirm, downloads, checks and installs it (<see cref="UpdateInstall"/>). Runs on the UI thread; the network
/// is awaited, so nothing waits on it.
/// </summary>
internal static class Updates
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    /// <summary>After a failed check, the next try is sooner than a day.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(3);
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMinutes(30) };
    private static Action<UpdateInfo>? _announce;

    /// <summary>A check is under way.</summary>
    public static bool Checking { get; private set; }

    /// <summary>How far an update's download has got, 0 to 1, while one is being installed; null otherwise.</summary>
    public static double? Installing { get; private set; }

    /// <summary>Why the last try at updating failed, until the next try.</summary>
    public static string? InstallError { get; private set; }

    /// <summary>A check started or finished. Raised on the UI thread.</summary>
    public static event Action? Changed;

    /// <summary>The newer version found, with its page, while checking is on and there is one.</summary>
    public static UpdateInfo? Available =>
        Settings.CheckForUpdates == true && Settings.UpdateAvailable is { } version
            ? new UpdateInfo(version, UpdateCheck.ReleasePageFor(version))
            : null;

    /// <summary>Starts the daily checks; <paramref name="announce"/> shows the pop-up for a newer version.</summary>
    public static void Start(Action<UpdateInfo> announce)
    {
        _announce = announce;
        Timer.Tick += (_, _) => CheckIfDue();
        Timer.Start();
        CheckIfDue();
    }

    public static void Stop() => Timer.Stop();

    private static void CheckIfDue()
    {
        if (Settings.CheckForUpdates != true)
            return;
        var last = Settings.LastUpdateCheck;
        if (last is null || DateTimeOffset.Now - last.Value.When >= (last.Value.Answered ? Interval : RetryInterval))
            CheckNow();
    }

    /// <summary>Checks now, if checking is on and one isn't already under way.</summary>
    public static async void CheckNow()
    {
        if (Checking || Settings.CheckForUpdates != true)
            return;
        Checking = true;
        Changed?.Invoke();
        try
        {
            var newer = await UpdateCheck.FindNewerAsync();
            Settings.RecordUpdateCheck(DateTimeOffset.Now, answered: true, newer?.Version);
            if (newer is not null && Settings.UpdateAnnounced != newer.Version)
            {
                Settings.UpdateAnnounced = newer.Version;
                _announce?.Invoke(newer);
            }
        }
        catch (Exception ex)
        {
            Log.Once($"Checking for updates failed: {ex.GetType().Name}: {ex.Message}");
            Settings.RecordUpdateCheck(DateTimeOffset.Now, answered: false, null);
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Asks whether to update to the newer version, over <paramref name="owner"/> if given, and if the user says yes,
    /// downloads, checks and installs it, then restarts as it. A folder WattsLeft can't change (Program Files, say)
    /// can't be updated in place, so the release page opens instead.
    /// </summary>
    public static async void Offer(Window? owner)
    {
        if (Available is not { } update || Installing is not null)
            return;
        string version = update.Version.ToString(3);
        if (!UpdateInstall.CanReplaceApp())
        {
            InstallError = "WattsLeft's folder can't be changed, so download the new version from GitHub.";
            Changed?.Invoke();
            OpenPage(update);
            return;
        }

        string title = $"Update to WattsLeft {version}?";
        string message = $"WattsLeft will download version {version} from GitHub (about 16 MB), check it, then close and " +
            "start again as the new version. Your settings and battery history are kept.";
        var palette = Appearance.For(Appearance.AppsUseLight());
        (string, Action) whatsNew = ("What's new", () => OpenPage(update));
        bool confirmed = owner is null
            ? await ConfirmDialog.AskOnItsOwn(title, message, "Update", "Not now", palette, whatsNew)
            : await ConfirmDialog.Ask(owner, title, message, "Update", palette, "Not now", whatsNew);
        if (!confirmed || Installing is not null)
            return;

        Installing = 0;
        InstallError = null;
        Changed?.Invoke();
        try
        {
            // Reported back on the UI thread, where this was made.
            var progress = new Progress<double>(done =>
            {
                Installing = done;
                Changed?.Invoke();
            });
            string unpacked = await Task.Run(() => UpdateInstall.PrepareAsync(update.Version, progress));
            UpdateInstall.HandOver(unpacked);
            App.Exit();
        }
        catch (Exception ex)
        {
            Log.Write($"Updating to {version} failed: {ex.GetType().Name}: {ex.Message}");
            InstallError = ex is HttpRequestException or OperationCanceledException
                ? "Couldn't download the update. Try again, or download it from GitHub."
                : "The update didn't check out, so nothing was changed. Try again, or download it from GitHub.";
            Installing = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Opens a release's page in the browser. The address is always one WattsLeft built itself.</summary>
    public static void OpenPage(UpdateInfo update) => Platform.Windows.Win32.Open(update.Url);
}
