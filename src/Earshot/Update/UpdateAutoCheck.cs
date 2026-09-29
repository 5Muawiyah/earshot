using System.Globalization;
using Earshot.Contracts;

namespace Earshot.Update;

// When the last automatic check was made, so a restart does not check again the same day.
internal interface IUpdateCheckStamp
{
    DateTimeOffset? Read();

    void Write(DateTimeOffset when);
}

// The stamp in a small text file under the data folder: one round-trip timestamp. It holds nothing that steers where
// a check or a download goes, only when the last one was made. A file that cannot be read or is not a timestamp
// counts as "never checked", and says so in the log.
internal sealed class FileUpdateCheckStamp(string path, ILog log) : IUpdateCheckStamp
{
    public DateTimeOffset? Read()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string text = File.ReadAllText(path).Trim();
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset when))
            {
                return when;
            }

            log.Warn("Update: " + path + " does not hold a time, so the last check is taken as never.");
            return null;
        }
        catch (IOException ex)
        {
            log.Warn("Update: " + path + " could not be read (" + ex.GetType().Name + "): " + ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            log.Warn("Update: " + path + " could not be read (" + ex.GetType().Name + "): " + ex.Message);
            return null;
        }
    }

    public void Write(DateTimeOffset when)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, when.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (IOException ex)
        {
            log.Warn("Update: " + path + " could not be written (" + ex.GetType().Name + "): " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            log.Warn("Update: " + path + " could not be written (" + ex.GetType().Name + "): " + ex.Message);
        }
    }
}

// "Check automatically": once the setting is on, a check at most once a day, the first a little after startup so it
// does not compete with it. It only ever checks; it never downloads. A check is recorded when it starts, so one that
// fails is not retried until the next day.
internal sealed class UpdateAutoCheck
{
    // A day between checks, and how often the loop looks at the clock and the setting. Behaviour, not measurements.
    internal static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time;
    private readonly Func<bool> _enabled;
    private readonly IUpdateCheckStamp _stamp;
    private readonly Func<CancellationToken, Task> _check;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _poll;

    public UpdateAutoCheck(
        TimeProvider time, Func<bool> enabled, IUpdateCheckStamp stamp, Func<CancellationToken, Task> check, TimeSpan startupDelay, TimeSpan poll)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(check);
        _time = time;
        _enabled = enabled;
        _stamp = stamp;
        _check = check;
        _startupDelay = startupDelay;
        _poll = poll;
    }

    // Due when nothing was recorded, when a day has passed, or when the record is more than a day in the future (the
    // clock was set back, so it cannot be trusted to say when the last check was).
    internal static bool IsDue(DateTimeOffset? last, DateTimeOffset now) =>
        last is null || now - last.Value >= Interval || last.Value - now > Interval;

    // Runs until cancelled.
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_startupDelay, _time, ct).ConfigureAwait(false);
            while (true)
            {
                if (_enabled() && IsDue(_stamp.Read(), _time.GetUtcNow()))
                {
                    _stamp.Write(_time.GetUtcNow());
                    await _check(ct).ConfigureAwait(false);
                }

                await Task.Delay(_poll, _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The program is closing.
        }
    }
}
