namespace WattsLeft.Battery;

/// <summary>
/// When a source that has to ask its devices (a receiver, a dongle) asks them, so they show as soon as they
/// answer without being asked all the time:
/// <list type="bullet">
/// <item>every <see cref="Interval"/>, a full poll;</item>
/// <item>straight away whenever Windows sends a device notice, which a device connecting to its receiver or
/// switching on usually brings;</item>
/// <item>every <see cref="SettlingInterval"/> for <see cref="SettlingTime"/> after <see cref="Settle"/>, for a
/// device that has just appeared: a receiver is plugged in before the device behind it has connected to it.</item>
/// </list>
/// Polls between the full ones are quick: the source asks only what's worth asking then.
/// </summary>
internal sealed class PollSchedule : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SettlingTime = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan SettlingInterval = TimeSpan.FromSeconds(2);

    private readonly Func<bool, CancellationToken, Task> _poll;
    private readonly string _name;
    private readonly CancellationTokenSource _stop = new();
    /// <summary>Released to poll now rather than at the next interval.</summary>
    private readonly SemaphoreSlim _wake = new(0);
    private readonly IDisposable _notices;
    private long _settleUntil;

    /// <param name="name">What's polled, for the error log.</param>
    /// <param name="poll">
    /// Asks the devices. Its argument is true for a full poll, when everything should be asked; false for a
    /// quick one, when only what's in use need be.
    /// </param>
    /// <param name="ready">Waited for (up to 10 seconds) before the first poll: the devices already plugged in being listed.</param>
    public PollSchedule(string name, Func<bool, CancellationToken, Task> poll, Task? ready = null)
    {
        _name = name;
        _poll = poll;
        _notices = DeviceNotices.Watch(Wake);
        _ = Task.Run(() => RunAsync(ready, _stop.Token));
    }

    /// <summary>The token a poll should stop at, for one started outside the schedule.</summary>
    public CancellationToken Stopping => _stop.Token;

    /// <summary>Polls now.</summary>
    public void Wake()
    {
        lock (_wake)
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
    }

    /// <summary>A device has just appeared: polls now, and often for a while, until it has connected.</summary>
    public void Settle()
    {
        Interlocked.Exchange(ref _settleUntil, (DateTime.UtcNow + SettlingTime).Ticks);
        Wake();
    }

    private bool Settling => DateTime.UtcNow.Ticks < Interlocked.Read(ref _settleUntil);

    private async Task RunAsync(Task? ready, CancellationToken token)
    {
        if (ready is not null)
        {
            try
            {
                await ready.WaitAsync(TimeSpan.FromSeconds(10), token);
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        var nextFull = DateTime.MinValue;
        while (!token.IsCancellationRequested)
        {
            bool full = !Settling && DateTime.UtcNow >= nextFull;
            try
            {
                await _poll(full, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Once($"{_name} failed: {ex.GetType().Name}: {ex.Message}");
            }
            if (full)
                nextFull = DateTime.UtcNow + Interval;

            try
            {
                var wait = Settling ? SettlingInterval : nextFull - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    await _wake.WaitAsync(wait, token);
                // One poll answers any number of notices that came meanwhile.
                while (_wake.Wait(0)) { }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _notices.Dispose();
    }
}
