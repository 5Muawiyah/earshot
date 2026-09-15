using Earshot.Contracts;

namespace Earshot.Tests;

// An ILog that keeps every entry for assertions.
internal sealed class CapturingLog : ILog
{
    private readonly List<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_entries) { return _entries.ToArray(); } }
    }

    public void Write(LogLevel level, string message, Exception? ex = null)
    {
        lock (_entries)
        {
            _entries.Add(new LogEntry(level, message, ex));
        }
    }

    public bool Has(LogLevel level, string fragment) =>
        Entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));
}

internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

// A private folder under %TEMP% for one test, removed afterwards.
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "earshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

// Sets one process environment variable for the life of the object, then puts the old value back.
internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    public EnvironmentVariableScope(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}
