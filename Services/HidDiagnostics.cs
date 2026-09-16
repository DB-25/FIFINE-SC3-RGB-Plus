using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace SC3RGBController.Services;

/// <summary>
/// Opt-in, low-overhead telemetry for physical stability investigation. It is
/// inactive unless SC3_HID_DIAGNOSTICS=1 is supplied to the process.
/// </summary>
internal static class HidDiagnostics
{
    private static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("SC3_HID_DIAGNOSTICS"), "1", StringComparison.Ordinal);
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<string> SlowOperations = new();
    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(), "FIFINE-SC3-RGB+-hid-diagnostics.log");
    private static Timer? _timer;
    private static long _writes;
    private static long _reads;
    private static long _fc02;
    private static long _fc04;
    private static string _activeOperation = "idle";
    private static DateTimeOffset _activeSince;

    public static IDisposable Measure(string operation, int writes = 0, int reads = 0)
    {
        if (!Enabled) return Noop.Instance;
        EnsureTimer();
        Interlocked.Add(ref _writes, writes);
        Interlocked.Add(ref _reads, reads);
        if (operation == "FC/02") Interlocked.Increment(ref _fc02);
        if (operation == "FC/04") Interlocked.Increment(ref _fc04);
        lock (Gate)
        {
            _activeOperation = operation;
            _activeSince = DateTimeOffset.UtcNow;
        }
        return new Measurement(operation, Stopwatch.StartNew());
    }

    private static void EnsureTimer()
    {
        if (_timer is not null) return;
        lock (Gate)
        {
            _timer ??= new Timer(_ => WriteSnapshot(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
    }

    private static void WriteSnapshot()
    {
        try
        {
            string active;
            DateTimeOffset activeSince;
            lock (Gate) { active = _activeOperation; activeSince = _activeSince; }
            long writes = Interlocked.Exchange(ref _writes, 0);
            long reads = Interlocked.Exchange(ref _reads, 0);
            long fc02 = Interlocked.Exchange(ref _fc02, 0);
            long fc04 = Interlocked.Exchange(ref _fc04, 0);
            TimeSpan activeFor = active == "idle" ? TimeSpan.Zero : DateTimeOffset.UtcNow - activeSince;
            string slow = string.Join(" | ", SlowOperations.Take(8));
            File.AppendAllText(LogPath,
                $"{DateTimeOffset.UtcNow:O} rates fc02={fc02}/s fc04={fc04}/s writes={writes}/s reads={reads}/s active={active} activeMs={activeFor.TotalMilliseconds:F0}{(slow.Length == 0 ? string.Empty : " slow=" + slow)}{Environment.NewLine}");
        }
        catch { /* Diagnostics must never affect device traffic. */ }
    }

    private sealed class Measurement(string operation, Stopwatch stopwatch) : IDisposable
    {
        public void Dispose()
        {
            stopwatch.Stop();
            lock (Gate) _activeOperation = "idle";
            if (stopwatch.Elapsed >= TimeSpan.FromMilliseconds(100))
                SlowOperations.Enqueue($"{operation}:{stopwatch.Elapsed.TotalMilliseconds:F0}ms");
            while (SlowOperations.Count > 32 && SlowOperations.TryDequeue(out _)) { }
        }
    }

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }
}
