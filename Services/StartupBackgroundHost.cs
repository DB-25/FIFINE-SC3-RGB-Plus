using SC3FirmwareTool.Core;
using SC3RGBController.Models;

namespace SC3RGBController.Services;

/// <summary>
/// Runs at Windows login before any WPF window exists.  It verifies the same
/// supported SC3 profile used by the UI, starts saved RGB restoration, and
/// activates Custom Buttons only when Firmware 1.5 exposes CBTN v4 MI_03 interrupt events.
/// </summary>
public sealed class StartupBackgroundHost : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly HidDeviceClient _hid = new();
    private readonly FirmwareService _firmwareService = new();
    private readonly CustomButtonShortcutController _customButtons;
    private readonly BackgroundLightingRestorer _lighting;
    private CancellationTokenSource? _cts;
    private Task? _deviceTask;
    private volatile bool _lightingFirmwareReady;
    private bool _restoreStockMode;

    public StartupBackgroundHost(AppSettings settings, bool restoreStockMode = false)
    {
        _settings = settings;
        _restoreStockMode = restoreStockMode;
        _customButtons = new CustomButtonShortcutController(_hid, GetCustomButtonPath);
        _lighting = new BackgroundLightingRestorer(_hid, () => _lightingFirmwareReady,
            () => SavedLightingState.From(_settings));
    }

    public bool IsRunning => _deviceTask is { IsCompleted: false };

    public async Task DisableShortcutsAsync()
    {
        _settings.CustomShortcutsEnabled = false;
        SettingsStore.Save(_settings);
        await _customButtons.SetPreferenceAsync(false);
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _lighting.Start();
        _deviceTask = Task.Run(() => WatchDeviceAsync(_cts.Token));
    }

    private async Task WatchDeviceAsync(CancellationToken token)
    {
        bool wasReady = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool present = await Task.Run(() => _hid.Probe(out _), token);
                bool ready = present && _lightingFirmwareReady && _hid.IsConnected
                    ? true
                    : present && await DetectRgbFirmwareAsync(token);
                _lightingFirmwareReady = ready;

                if (ready && _restoreStockMode)
                {
                    // One controlled output-only command returns a session left in
                    // shortcut mode to normal SC3 behavior. Never retry in a loop.
                    _hid.TrySetCustomButtonMode(false, out _);
                    _restoreStockMode = false;
                }

                if (ready && _settings.CustomShortcutsEnabled)
                    await _customButtons.SetPreferenceAsync(true);
                else if (wasReady && !ready && _customButtons.IsRunning)
                    await _customButtons.StopAsync(sendOff: false);

                wasReady = ready;
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> DetectRgbFirmwareAsync(CancellationToken token)
    {
        DeviceStatus firmware = await Task.Run(_firmwareService.Detect, token);
        if (!firmware.ValidatedProfile || !_hid.TryOpen(out _)) return false;

        Sc3QueryReply? query = null;
        if (_hid.TryQuerySc3(out Sc3QueryReply reply, out _)) query = reply;
        Sc3FirmwareFlavor flavor = Sc3FirmwareClassificationPolicy.Resolve(
            firmware.ValidatedProfile, firmware.ModInstalled, query);

        bool rgbReady = firmware.ModInstalled || flavor is Sc3FirmwareFlavor.Mod14 or Sc3FirmwareFlavor.Mod15;
        if (flavor != Sc3FirmwareFlavor.Mod15 && _customButtons.IsRunning)
            await _customButtons.StopAsync(sendOff: false);
        return rgbReady;
    }

    private string? GetCustomButtonPath(CustomButtonId button) => button switch
    {
        CustomButtonId.A => _settings.CustomAPath,
        CustomButtonId.B => _settings.CustomBPath,
        CustomButtonId.C => _settings.CustomCPath,
        _ => _settings.CustomDPath
    };

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cancellation = _cts;
        Task? deviceTask = _deviceTask;
        _cts = null;
        _deviceTask = null;
        _lightingFirmwareReady = false;
        cancellation?.Cancel();
        if (deviceTask is not null)
        {
            try { await deviceTask; }
            catch (OperationCanceledException) { }
        }
        cancellation?.Dispose();
        await _customButtons.DisposeAsync();
        await _lighting.DisposeAsync();
        _hid.Dispose();
    }
}
