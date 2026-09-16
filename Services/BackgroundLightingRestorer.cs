using System.Windows.Media;

namespace SC3RGBController.Services;

public interface ISc3LightingTransport
{
    bool IsConnected { get; }
    bool TryOpen(out string detail);
    bool TryWriteColor(byte red, byte green, byte blue, out string detail);
}

/// <summary>
/// Owns the background RGB sender.  It sends the saved state once on every
/// real HID connection, retains animated effects at the normal 30 Hz cadence,
/// and only retries opening a missing device at the requested backoff.
/// </summary>
public sealed class BackgroundLightingRestorer : IAsyncDisposable
{
    private readonly ISc3LightingTransport _transport;
    private readonly Func<bool> _firmwareReady;
    private readonly Func<SavedLightingState> _stateProvider;
    private readonly TimeSpan _frameInterval;
    private readonly TimeSpan _reconnectInterval;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private DateTime _effectStartedAt;
    private bool _wasConnected;

    public BackgroundLightingRestorer(
        ISc3LightingTransport transport,
        Func<bool> firmwareReady,
        Func<SavedLightingState> stateProvider,
        TimeSpan? frameInterval = null,
        TimeSpan? reconnectInterval = null)
    {
        _transport = transport;
        _firmwareReady = firmwareReady;
        _stateProvider = stateProvider;
        _frameInterval = frameInterval ?? TimeSpan.FromMilliseconds(1000.0 / 30.0);
        _reconnectInterval = reconnectInterval ?? TimeSpan.FromSeconds(1);
    }

    public bool IsRunning => _task is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _effectStartedAt = DateTime.UtcNow;
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        Color? lastSent = null;
        DateTime nextOpenAttempt = DateTime.MinValue;
        using PeriodicTimer timer = new(_frameInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (!_firmwareReady())
                {
                    lastSent = null;
                    _wasConnected = false;
                    continue;
                }

                if (!_transport.IsConnected)
                {
                    // The prior HID session ended.  A successful open below is a
                    // real reconnect and must force one complete saved-state send.
                    _wasConnected = false;
                    lastSent = null;
                    if (DateTime.UtcNow < nextOpenAttempt) continue;
                    nextOpenAttempt = DateTime.UtcNow.Add(_reconnectInterval);
                    if (!_transport.TryOpen(out _))
                    {
                        _wasConnected = false;
                        lastSent = null;
                        continue;
                    }
                }

                if (!_wasConnected)
                {
                    // A reconnect begins a new effect cycle and forces a full restore.
                    _effectStartedAt = DateTime.UtcNow;
                    lastSent = null;
                    _wasConnected = true;
                }

                Color output = _stateProvider().GetOutput(_effectStartedAt, DateTime.UtcNow);
                if (lastSent == output) continue;
                if (_transport.TryWriteColor(output.R, output.G, output.B, out _))
                {
                    lastSent = output;
                }
                else
                {
                    lastSent = null;
                    _wasConnected = false;
                    nextOpenAttempt = DateTime.UtcNow.Add(_reconnectInterval);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cancellation = _cts;
        Task? task = _task;
        _cts = null;
        _task = null;
        cancellation?.Cancel();
        if (task is not null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }
        cancellation?.Dispose();
    }
}
