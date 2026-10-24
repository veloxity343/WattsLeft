namespace WattsLeft;

/// <summary>
/// The app's logs in %LOCALAPPDATA%\WattsLeft: errors, worth keeping since a tray app has nowhere else to
/// report them, and the readable history of readings. Both are kept from growing forever.
/// </summary>
internal static class Log
{
    internal const string ErrorFile = "error.log";
    internal const string HistoryFile = "history.log";

    /// <summary>Where the app keeps its logs, history and reports.</summary>
    internal static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WattsLeft");

    private static readonly object WriteLock = new();

    /// <summary>Appends to a log in %LOCALAPPDATA%\WattsLeft, since a tray app has nowhere else to report.</summary>
    internal static void Write(string message, string fileName = ErrorFile)
    {
        try
        {
            // One writer at a time: the pollers log from their own threads, and two appends at once would lose a line.
            lock (WriteLock)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, fileName), $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static readonly TimeSpan HistoryKept = TimeSpan.FromDays(30);
    public const long ErrorLimit = 1024 * 1024;

    /// <summary>
    /// Keeps the logs from growing forever: history.log to its last 30 days (battery.csv has the long-term
    /// record), error.log to its newest megabyte. Run at startup.
    /// </summary>
    internal static void Trim()
    {
        TrimByAge(Path.Combine(Folder, HistoryFile), DateTime.Now - HistoryKept);
        TrimBySize(Path.Combine(Folder, ErrorFile), ErrorLimit);
    }

    /// <summary>Drops lines dated before <paramref name="cutoff"/>; each line starts with its timestamp.</summary>
    internal static void TrimByAge(string path, DateTime cutoff)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var lines = File.ReadAllLines(path);
            int first = Array.FindIndex(lines, line => LineTime(line) is DateTime t && t >= cutoff);
            if (first == 0)
                return;
            File.WriteAllLines(path, first < 0 ? [] : lines[first..]);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Keeps roughly the newest <paramref name="limit"/> bytes, starting at a whole entry: error entries can
    /// run over several lines (stack traces), so the cut moves forward to the next line with a timestamp.
    /// </summary>
    internal static void TrimBySize(string path, long limit)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= limit)
                return;
            string tail;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(-limit, SeekOrigin.End);
                tail = new StreamReader(stream).ReadToEnd();
            }
            var lines = tail.Split('\n');
            int first = Array.FindIndex(lines, 1, line => LineTime(line) is not null); // line 0 is likely cut mid-way
            File.WriteAllText(path, first < 0 ? "" : string.Join('\n', lines[first..]));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static DateTime? LineTime(string line)
    {
        int space = line.IndexOf(' ');
        return space > 0 && DateTime.TryParse(line[..space], System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;
    }

    private static readonly HashSet<string> LoggedOnce = [];

    /// <summary>Logs a recurring problem the first time it appears, rather than on every 30-second poll.</summary>
    internal static void Once(string message)
    {
        lock (LoggedOnce)
        {
            if (!LoggedOnce.Add(message))
                return;
        }
        Write(message);
    }
}
