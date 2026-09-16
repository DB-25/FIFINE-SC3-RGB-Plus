using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SC3RGBController.Services;

public sealed class HidDeviceClient : ISc3CustomButtonTransport, ISc3LightingTransport, IDisposable
{
    public const ushort ExpectedVid = 0x3142;
    public const ushort ExpectedPid = 0x0C33;
    public const ushort ExpectedUsagePage = 0xFF00;
    public const ushort ExpectedUsage = 0x55AA;
    public const int ExpectedInterface = 4;
    public const int ExpectedInputLength = 257;
    public const int EventInterface = 3;
    public const ushort EventUsagePage = 0x00FF;
    public const ushort EventUsage = 0x0001;
    public const int EventInputLength = 2;
    public const int ExpectedOutputLength = 257;
    public const int ExpectedFeatureLength = 9;
    public static bool RgbWritesEnabled => true;
    public static string OutputTransport => "bounded HID control transfer";

    private const uint GenericReadWrite = 0xC0000000;
    private const uint GenericRead = 0x80000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const int ControlTimeoutMs = 250;
    private const int ErrorIoPending = 997;
    private const int ErrorNotFound = 1168;
    private const int ErrorOperationAborted = 995;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint Infinite = 0xFFFFFFFF;
    private static readonly uint IoctlHidSetOutputReport = CtlCode(0x0000000B, 101, 1, 0);
    private static readonly uint IoctlHidGetInputReport = CtlCode(0x0000000B, 104, 2, 0);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _eventReadGate = new(1, 1);
    private SafeFileHandle? _writeHandle;
    private SafeFileHandle? _eventHandle;
    private DeviceIdentity? _validatedIdentity;
    private DeviceIdentity? _eventIdentity;
    private bool _customEventSessionPrimed;
    private ushort _eventCounter;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _writeHandle is { IsInvalid: false, IsClosed: false } &&
                       _validatedIdentity?.MatchesExpected == true;
            }
        }
    }

    public string DeviceSummary => "FIFINE SC3 · VID 3142 · PID 0C33 · IF4 · FF00:55AA";

    public bool Probe(out string detail)
    {
        lock (_gate)
        {
            DeviceIdentity? identity = FindExactDevice();
            if (identity is null)
            {
                CloseLocked();
                detail = "FIFINE SC3 vendor HID not found";
                return false;
            }

            if (_validatedIdentity is not null &&
                !string.Equals(_validatedIdentity.Path, identity.Path, StringComparison.OrdinalIgnoreCase))
            {
                CloseLocked();
            }

            detail = $"Connected · {identity.Product} · Output {identity.OutputReportLength} bytes";
            return true;
        }
    }

    public bool TryOpen(out string detail)
    {
        lock (_gate)
        {
            if (_writeHandle is { IsInvalid: false, IsClosed: false } &&
                _validatedIdentity?.MatchesExpected == true)
            {
                detail = "Connected";
                return true;
            }

            CloseLocked();
            DeviceIdentity? identity = FindExactDevice();
            if (identity is null)
            {
                detail = "FIFINE SC3 vendor HID not found";
                return false;
            }

            SafeFileHandle handle = Native.CreateFile(
                identity.Path,
                GenericReadWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOverlapped,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                detail = $"HID open failed · Windows error {Marshal.GetLastWin32Error()}";
                handle.Dispose();
                return false;
            }

            _validatedIdentity = identity;
            _writeHandle = handle;
            detail = "Connected";
            return true;
        }
    }

    public bool TryWriteColor(byte red, byte green, byte blue, out string detail)
    {
        lock (_gate)
        {
            using IDisposable telemetry = HidDiagnostics.Measure("FC/04", writes: 1);
            if (_writeHandle is not { IsInvalid: false, IsClosed: false } ||
                _validatedIdentity?.MatchesExpected != true)
            {
                detail = "SC3 is not connected";
                return false;
            }

            byte[] report = BuildCustomRgbReport(red, green, blue);
            ValidateCustomRgbReport(report, red, green, blue);

            bool ok = TryControlOutputLocked(report, out int error, out bool timedOut);
            if (!ok)
            {
                detail = $"RGB Output Report failed · Windows error {error}" +
                    (timedOut ? " · timed out" : string.Empty);
                CloseLocked();
                return false;
            }

            detail = $"Applied #{red:X2}{green:X2}{blue:X2}";
            return true;
        }
    }

    public bool TryDisableCustomRgb(out string detail)
    {
        lock (_gate)
        {
            using IDisposable telemetry = HidDiagnostics.Measure("RGB disable", writes: 1);
            if (_writeHandle is not { IsInvalid: false, IsClosed: false } ||
                _validatedIdentity?.MatchesExpected != true)
            {
                detail = "SC3 is not connected";
                return false;
            }

            byte[] report = BuildDisableCustomRgbReport();
            bool ok = TryControlOutputLocked(report, out int error, out bool timedOut);
            if (!ok)
            {
                detail = $"Restore Output Report failed · Windows error {error}" +
                    (timedOut ? " · timed out" : string.Empty);
                CloseLocked();
                return false;
            }

            detail = $"Returned to stock RGB mode · {OutputTransport}";
            return true;
        }
    }

    public bool TryQuerySc3(out Sc3QueryReply reply, out string detail)
    {
        lock (_gate)
        {
            using IDisposable telemetry = HidDiagnostics.Measure("FC/02", writes: 1, reads: 1);
            reply = new(Sc3FirmwareFlavor.Unknown, false, false, 0, 0, 0, 0);
            if (_writeHandle is not { IsInvalid: false, IsClosed: false } || _validatedIdentity?.MatchesExpected != true)
            {
                detail = "SC3 is not connected";
                return false;
            }
            byte[] query = BuildFirmwareQueryReport();
            if (!TryControlOutputLocked(query, out int setError, out bool setTimedOut))
            {
                detail = $"FC/02 query failed · Windows error {setError}" +
                    (setTimedOut ? " · timed out" : string.Empty);
                CloseLocked();
                return false;
            }
            byte[] response = new byte[_validatedIdentity.InputReportLength];
            if (!TryControlInputLocked(response, out int getError, out bool getTimedOut))
            {
                detail = $"FC/02 reply failed · Windows error {getError}" +
                    (getTimedOut ? " · timed out" : string.Empty);
                CloseLocked();
                return false;
            }
            if (!Sc3QueryReply.TryParse(response, out reply))
            {
                detail = "FC/02 reply was invalid";
                return false;
            }
            detail = "FC/02 reply valid";
            return true;
        }
    }

    public bool TryBeginCustomButtonEventSession(out Sc3QueryReply reply, out string detail)
    {
        // Candidate 4 keeps control traffic on MI_04 and exposes button events
        // through the existing MI_03 interrupt endpoint. Verify the firmware
        // once when a session begins; the active shortcut loop never polls FC/02.
        if (!TryQuerySc3(out reply, out detail))
            return false;
        if (!reply.SupportsFinalCustomShortcuts)
        {
            detail = "CBTN v4 event firmware is not available";
            return false;
        }

        lock (_gate)
        {
            using IDisposable telemetry = HidDiagnostics.Measure("CBTN interrupt session");
            _customEventSessionPrimed = false;
            if (_writeHandle is not { IsInvalid: false, IsClosed: false } ||
                _validatedIdentity?.MatchesExpected != true)
            {
                detail = "SC3 is not connected";
                return false;
            }

            DeviceIdentity? eventIdentity = FindExactEventDevice();
            if (eventIdentity is null)
            {
                CloseEventLocked();
                detail = "CBTN v4 MI_03 interrupt endpoint is not available";
                return false;
            }

            if (_eventHandle is not { IsInvalid: false, IsClosed: false } ||
                _eventIdentity is null ||
                !string.Equals(_eventIdentity.Path, eventIdentity.Path, StringComparison.OrdinalIgnoreCase))
            {
                CloseEventLocked();
                SafeFileHandle eventHandle = Native.CreateFile(
                    eventIdentity.Path,
                    GenericRead,
                    FileShareRead | FileShareWrite,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagOverlapped,
                    IntPtr.Zero);
                if (eventHandle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    eventHandle.Dispose();
                    detail = $"CBTN v4 MI_03 open failed · Windows error {error}";
                    return false;
                }
                _eventHandle = eventHandle;
                _eventIdentity = eventIdentity;
            }

            _eventCounter = 0;
            _customEventSessionPrimed = true;
            detail = "CBTN v4 MI_03 interrupt event session ready";
            return true;
        }
    }

    public async Task<CustomButtonDrainResult> DrainCustomButtonEventsAsync(CancellationToken token)
    {
        const int idleWindowMs = 50;
        const int maximumBufferedReports = 64;
        await _eventReadGate.WaitAsync(token);
        try
        {
            SafeFileHandle? handle;
            int inputLength;
            lock (_gate)
            {
                if (!_customEventSessionPrimed ||
                    _eventHandle is not { IsInvalid: false, IsClosed: false } ||
                    _eventIdentity?.MatchesExpectedEvent != true)
                    return new(false, 0, "CBTN interrupt event session is not ready");
                handle = _eventHandle;
                inputLength = _eventIdentity.InputReportLength;
            }

            int drained = 0;
            while (drained < maximumBufferedReports)
            {
                token.ThrowIfCancellationRequested();
                byte[] response = new byte[inputLength];
                using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(idleWindowMs);
                (bool success, int error, bool cancelled) =
                    await ReadInterruptReportAsync(handle, response, idle.Token);
                if (cancelled)
                {
                    if (token.IsCancellationRequested)
                        return new(false, drained, "CBTN interrupt drain cancelled", true);
                    return new(true, drained, $"CBTN interrupt queue drained after {idleWindowMs} ms idle");
                }
                if (!success)
                    return new(false, drained, $"CBTN interrupt drain failed · Windows error {error}");
                drained++;
            }

            return new(false, drained, "CBTN interrupt queue exceeded the safe drain limit");
        }
        catch (OperationCanceledException)
        {
            return new(false, 0, "CBTN interrupt drain cancelled", true);
        }
        finally
        {
            _eventReadGate.Release();
        }
    }

    public async Task<CustomButtonReadResult> ReadCustomButtonEventAsync(CancellationToken token)
    {
        await _eventReadGate.WaitAsync(token);
        try
        {
            using IDisposable telemetry = HidDiagnostics.Measure("CBTN interrupt read", reads: 1);
            SafeFileHandle? handle;
            int inputLength;
            lock (_gate)
            {
                if (!_customEventSessionPrimed ||
                    _eventHandle is not { IsInvalid: false, IsClosed: false } ||
                    _eventIdentity?.MatchesExpectedEvent != true)
                    return new(false, EmptyReply(), "CBTN interrupt event session is not ready");
                handle = _eventHandle;
                inputLength = _eventIdentity.InputReportLength;
            }

            byte[] response = new byte[inputLength];
            (bool success, int error, bool cancelled) = await ReadInterruptReportAsync(handle, response, token);
            if (cancelled)
                return new(false, EmptyReply(), "CBTN interrupt read cancelled", true);
            if (!success)
                return new(false, EmptyReply(), $"CBTN interrupt read failed · Windows error {error}");
            if (response.Length != EventInputLength || response[0] != 0)
                return new(false, EmptyReply(), "CBTN interrupt event was invalid");

            ushort counter;
            lock (_gate)
            {
                counter = ++_eventCounter;
            }
            Sc3QueryReply reply = new(
                Sc3FirmwareFlavor.Mod15, true, true, 4, response[1], counter, 1);

            return new(true, reply, "CBTN interrupt event valid");
        }
        catch (OperationCanceledException)
        {
            return new(false, EmptyReply(), "CBTN interrupt read cancelled", true);
        }
        finally
        {
            _eventReadGate.Release();
        }
    }

    private static Sc3QueryReply EmptyReply() =>
        new(Sc3FirmwareFlavor.Unknown, false, false, 0, 0, 0, 0);

    public bool TrySetCustomButtonMode(bool enabled, out string detail)
    {
        lock (_gate)
        {
            using IDisposable telemetry = HidDiagnostics.Measure(enabled ? "CBTN on" : "CBTN off", writes: 1);
            if (_writeHandle is not { IsInvalid: false, IsClosed: false } || _validatedIdentity?.MatchesExpected != true)
            {
                detail = "SC3 is not connected";
                return false;
            }
            byte[] report = BuildCustomButtonModeReport(enabled);
            if (!TryControlOutputLocked(report, out int error, out bool timedOut))
            {
                detail = $"Custom Button mode command failed · Windows error {error}" +
                    (timedOut ? " · timed out" : string.Empty);
                CloseLocked();
                return false;
            }
            detail = enabled ? "Shortcut mode enabled" : "Shortcut mode off sent";
            return true;
        }
    }
    public void Close()
    {
        lock (_gate)
        {
            CloseLocked();
        }
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    private void CloseLocked()
    {
        CloseEventLocked();
        if (_writeHandle is { IsInvalid: false, IsClosed: false })
            Native.CancelIoEx(_writeHandle, IntPtr.Zero);
        _writeHandle?.Dispose();
        _writeHandle = null;
        _validatedIdentity = null;
        _customEventSessionPrimed = false;
    }

    private void CloseEventLocked()
    {
        _customEventSessionPrimed = false;
        if (_eventHandle is { IsInvalid: false, IsClosed: false })
            Native.CancelIoEx(_eventHandle, IntPtr.Zero);
        _eventHandle?.Dispose();
        _eventHandle = null;
        _eventIdentity = null;
        _eventCounter = 0;
    }

    private static async Task<(bool Success, int Error, bool Cancelled)> ReadInterruptReportAsync(
        SafeFileHandle handle, byte[] report, CancellationToken token)
    {
        bool addedRef = false;
        GCHandle reportPin = default;
        IntPtr overlapped = IntPtr.Zero;
        using EventWaitHandle completed = new(false, EventResetMode.ManualReset);
        try
        {
            handle.DangerousAddRef(ref addedRef);
            reportPin = GCHandle.Alloc(report, GCHandleType.Pinned);
            Native.Overlapped state = new() { EventHandle = completed.SafeWaitHandle.DangerousGetHandle() };
            overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Native.Overlapped>());
            Marshal.StructureToPtr(state, overlapped, false);

            bool issued = Native.ReadFile(handle, reportPin.AddrOfPinnedObject(), report.Length, IntPtr.Zero, overlapped);
            int issueError = issued ? 0 : Marshal.GetLastWin32Error();
            if (!issued && issueError != ErrorIoPending)
                return (false, issueError, false);

            bool signalled = issued || await WaitHandleAsync(completed, token);
            bool cancelled = !signalled;
            if (cancelled)
            {
                bool cancellationRequested = Native.CancelIoEx(handle, overlapped);
                int cancellationError = cancellationRequested ? 0 : Marshal.GetLastWin32Error();

                // Keep the buffer and OVERLAPPED alive until this exact read has
                // left the kernel. No replacement read is allowed before then.
                await WaitHandleAsync(completed, CancellationToken.None);
                if (!cancellationRequested && cancellationError != ErrorNotFound)
                    return (false, cancellationError, true);
            }

            bool ok = Native.GetOverlappedResult(handle, overlapped, out int transferred, false);
            int error = ok ? 0 : Marshal.GetLastWin32Error();
            if (cancelled && error == ErrorOperationAborted)
                return (false, error, true);
            return (ok && transferred == report.Length, error, false);
        }
        finally
        {
            if (reportPin.IsAllocated) reportPin.Free();
            if (overlapped != IntPtr.Zero) Marshal.FreeHGlobal(overlapped);
            if (addedRef) handle.DangerousRelease();
        }
    }

    private static async Task<bool> WaitHandleAsync(WaitHandle waitHandle, CancellationToken token)
    {
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle? registered = null;
        CancellationTokenRegistration cancellation = default;
        try
        {
            registered = ThreadPool.RegisterWaitForSingleObject(
                waitHandle,
                static (state, _) => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                completion,
                Timeout.Infinite,
                true);
            if (token.CanBeCanceled)
                cancellation = token.Register(static state =>
                    ((TaskCompletionSource<bool>)state!).TrySetResult(false), completion);
            return await completion.Task;
        }
        finally
        {
            cancellation.Dispose();
            registered?.Unregister(null);
        }
    }

    private bool TryControlOutputLocked(byte[] report, out int error, out bool timedOut) =>
        TryControlLocked(IoctlHidSetOutputReport, report, null, out error, out timedOut);

    private bool TryControlInputLocked(byte[] report, out int error, out bool timedOut) =>
        TryControlLocked(IoctlHidGetInputReport, null, report, out error, out timedOut);

    private bool TryControlLocked(
        uint code, byte[]? input, byte[]? output, out int error, out bool timedOut)
    {
        error = 0;
        timedOut = false;
        SafeFileHandle? handle = _writeHandle;
        if (handle is not { IsInvalid: false, IsClosed: false })
        {
            error = 6; // ERROR_INVALID_HANDLE
            return false;
        }

        using EventWaitHandle completed = new(false, EventResetMode.ManualReset);
        Native.Overlapped overlappedData = new()
        {
            EventHandle = completed.SafeWaitHandle.DangerousGetHandle()
        };
        IntPtr overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Native.Overlapped>());
        GCHandle inputPin = default;
        GCHandle outputPin = default;
        try
        {
            Marshal.StructureToPtr(overlappedData, overlapped, false);
            IntPtr inputPointer = IntPtr.Zero;
            IntPtr outputPointer = IntPtr.Zero;
            if (input is not null)
            {
                inputPin = GCHandle.Alloc(input, GCHandleType.Pinned);
                inputPointer = inputPin.AddrOfPinnedObject();
            }
            if (output is not null)
            {
                outputPin = GCHandle.Alloc(output, GCHandleType.Pinned);
                outputPointer = outputPin.AddrOfPinnedObject();
            }

            bool issued = Native.DeviceIoControl(handle, code,
                inputPointer, input?.Length ?? 0,
                outputPointer, output?.Length ?? 0,
                IntPtr.Zero, overlapped);
            int issueError = issued ? 0 : Marshal.GetLastWin32Error();
            if (!issued && issueError != ErrorIoPending)
            {
                error = issueError;
                return false;
            }

            uint wait = issued ? WaitObject0 :
                Native.WaitForSingleObject(completed.SafeWaitHandle.DangerousGetHandle(), (uint)ControlTimeoutMs);
            if (wait == WaitTimeout)
            {
                timedOut = true;
                bool cancellationRequested = Native.CancelIoEx(handle, overlapped);
                int cancellationError = cancellationRequested ? 0 : Marshal.GetLastWin32Error();
                if (!cancellationRequested && cancellationError != ErrorNotFound)
                    error = cancellationError;

                // Preserve the OVERLAPPED structure and pinned buffers until the
                // original request really exits. A slow driver cancellation must
                // never become an abandoned native operation.
                wait = Native.WaitForSingleObject(completed.SafeWaitHandle.DangerousGetHandle(), 2000);
                if (wait != WaitObject0)
                    Native.WaitForSingleObject(completed.SafeWaitHandle.DangerousGetHandle(), Infinite);
            }

            bool ok = Native.GetOverlappedResult(handle, overlapped, out _, false);
            if (!ok)
            {
                error = Marshal.GetLastWin32Error();
                if (timedOut && error == ErrorOperationAborted) return false;
            }
            return ok;
        }
        finally
        {
            if (inputPin.IsAllocated) inputPin.Free();
            if (outputPin.IsAllocated) outputPin.Free();
            Marshal.FreeHGlobal(overlapped);
        }
    }

    private static uint CtlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;

    public static byte[] BuildCustomRgbReport(byte red, byte green, byte blue)
    {
        // Windows HID API buffer: byte 0 Report ID + 256-byte HID payload.
        // SC3 V22 RGB Mod 1.2 command. Physical LED channel order is R,B,G,
        // so keep the UI/API standard RGB and swap G/B only at transport time:
        // A5 5A FC 04 01 R B G 16 + zero padding.
        byte[] report = new byte[ExpectedOutputLength];
        byte[] payload = [0xA5, 0x5A, 0xFC, 0x04, 0x01, red, blue, green, 0x16];
        Buffer.BlockCopy(payload, 0, report, 1, payload.Length);
        return report;
    }

    public static byte[] BuildDisableCustomRgbReport()
    {
        byte[] report = new byte[ExpectedOutputLength];
        byte[] payload = [0xA5, 0x5A, 0xFC, 0x01, 0x00, 0x16];
        Buffer.BlockCopy(payload, 0, report, 1, payload.Length);
        return report;
    }

    public static byte[] BuildFirmwareQueryReport() => BuildWhitelistedFc01Report(0x02);
    public static byte[] BuildCustomButtonModeReport(bool enabled) => BuildWhitelistedFc01Report(enabled ? (byte)0x03 : (byte)0x04);

    private static byte[] BuildWhitelistedFc01Report(byte subcommand)
    {
        if (subcommand is not (0x02 or 0x03 or 0x04)) throw new InvalidOperationException("FC/01 whitelist rejected subcommand");
        byte[] report = new byte[ExpectedOutputLength];
        byte[] payload = [0xA5, 0x5A, 0xFC, 0x01, subcommand, 0x16];
        Buffer.BlockCopy(payload, 0, report, 1, payload.Length);
        return report;
    }
    public static void ValidateCustomRgbReport(byte[] report, byte red, byte green, byte blue)
    {
        if (report.Length != ExpectedOutputLength || report[0] != 0)
        {
            throw new InvalidOperationException("Invalid HID report length or Report ID");
        }

        byte[] payload = [0xA5, 0x5A, 0xFC, 0x04, 0x01, red, blue, green, 0x16];
        for (int index = 0; index < payload.Length; index++)
        {
            if (report[index + 1] != payload[index])
            {
                throw new InvalidOperationException("Custom RGB payload validation failed");
            }
        }

        if (report.Skip(1 + payload.Length).Any(value => value != 0))
        {
            throw new InvalidOperationException("Custom RGB report padding validation failed");
        }
    }

    public static bool MatchesExpectedIdentity(
        string path,
        ushort vid,
        ushort pid,
        string manufacturer,
        string product,
        string serial,
        ushort usagePage,
        ushort usage,
        ushort inputReportLength,
        ushort outputReportLength,
        ushort featureReportLength)
    {
        return vid == ExpectedVid &&
               pid == ExpectedPid &&
               path.Contains("vid_3142&pid_0c33&mi_04", StringComparison.OrdinalIgnoreCase) &&
               manufacturer.Equals("MV-SILICON", StringComparison.OrdinalIgnoreCase) &&
               product.Equals("fifine SC3", StringComparison.OrdinalIgnoreCase) &&
               serial.Equals("20190808", StringComparison.OrdinalIgnoreCase) &&
               usagePage == ExpectedUsagePage &&
               usage == ExpectedUsage &&
               inputReportLength == ExpectedInputLength &&
               outputReportLength == ExpectedOutputLength &&
               featureReportLength == ExpectedFeatureLength;
    }

    public static bool MatchesExpectedEventIdentity(
        string path,
        ushort vid,
        ushort pid,
        string manufacturer,
        string product,
        string serial,
        ushort usagePage,
        ushort usage,
        ushort inputReportLength,
        ushort outputReportLength,
        ushort featureReportLength)
    {
        return vid == ExpectedVid &&
               pid == ExpectedPid &&
               path.Contains("vid_3142&pid_0c33&mi_03", StringComparison.OrdinalIgnoreCase) &&
               manufacturer.Equals("MV-SILICON", StringComparison.OrdinalIgnoreCase) &&
               product.Equals("fifine SC3", StringComparison.OrdinalIgnoreCase) &&
               serial.Equals("20190808", StringComparison.OrdinalIgnoreCase) &&
               usagePage == EventUsagePage &&
               usage == EventUsage &&
               inputReportLength == EventInputLength &&
               outputReportLength == 0 &&
               featureReportLength == 0;
    }

    private static DeviceIdentity? FindExactDevice()
    {
        Native.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr deviceInfo = Native.SetupDiGetClassDevs(
            ref hidGuid,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceInfo == new IntPtr(-1))
        {
            return null;
        }

        List<DeviceIdentity> matches = [];
        try
        {
            for (uint index = 0; ; index++)
            {
                SpDeviceInterfaceData interfaceData = new()
                {
                    CbSize = Marshal.SizeOf<SpDeviceInterfaceData>()
                };
                if (!Native.SetupDiEnumDeviceInterfaces(
                        deviceInfo, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) // ERROR_NO_MORE_ITEMS
                    {
                        break;
                    }

                    continue;
                }

                Native.SetupDiGetDeviceInterfaceDetail(
                    deviceInfo, ref interfaceData, IntPtr.Zero, 0, out int required, IntPtr.Zero);
                if (required <= 0)
                {
                    continue;
                }

                IntPtr detail = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(
                            deviceInfo, ref interfaceData, detail, required, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    string? path = Marshal.PtrToStringUni(detail + 4);
                    if (string.IsNullOrWhiteSpace(path) ||
                        !path.Contains("vid_3142&pid_0c33&mi_04", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    DeviceIdentity? identity = InspectDevice(path);
                    if (identity?.MatchesExpected == true)
                    {
                        matches.Add(identity);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(deviceInfo);
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private static DeviceIdentity? FindExactEventDevice()
    {
        Native.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr deviceInfo = Native.SetupDiGetClassDevs(
            ref hidGuid,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceInfo == new IntPtr(-1))
            return null;

        List<DeviceIdentity> matches = [];
        try
        {
            for (uint index = 0; ; index++)
            {
                SpDeviceInterfaceData interfaceData = new()
                {
                    CbSize = Marshal.SizeOf<SpDeviceInterfaceData>()
                };
                if (!Native.SetupDiEnumDeviceInterfaces(
                        deviceInfo, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259)
                        break;
                    continue;
                }

                Native.SetupDiGetDeviceInterfaceDetail(
                    deviceInfo, ref interfaceData, IntPtr.Zero, 0, out int required, IntPtr.Zero);
                if (required <= 0)
                    continue;

                IntPtr detail = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(
                            deviceInfo, ref interfaceData, detail, required, out _, IntPtr.Zero))
                        continue;

                    string? path = Marshal.PtrToStringUni(detail + 4);
                    if (string.IsNullOrWhiteSpace(path) ||
                        !path.Contains("vid_3142&pid_0c33&mi_03", StringComparison.OrdinalIgnoreCase))
                        continue;

                    DeviceIdentity? identity = InspectDevice(path);
                    if (identity?.MatchesExpectedEvent == true)
                        matches.Add(identity);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(deviceInfo);
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private static DeviceIdentity? InspectDevice(string path)
    {
        using SafeFileHandle handle = Native.CreateFile(
            path,
            0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        HiddAttributes attributes = new() { Size = Marshal.SizeOf<HiddAttributes>() };
        if (!Native.HidD_GetAttributes(handle, ref attributes) ||
            !Native.HidD_GetPreparsedData(handle, out IntPtr preparsed))
        {
            return null;
        }

        HidpCaps caps;
        try
        {
            if (Native.HidP_GetCaps(preparsed, out caps) < 0)
            {
                return null;
            }
        }
        finally
        {
            Native.HidD_FreePreparsedData(preparsed);
        }

        string manufacturer = GetHidString(Native.HidD_GetManufacturerString, handle);
        string product = GetHidString(Native.HidD_GetProductString, handle);
        string serial = GetHidString(Native.HidD_GetSerialNumberString, handle);
        return new DeviceIdentity(
            path,
            attributes.VendorId,
            attributes.ProductId,
            manufacturer,
            product,
            serial,
            caps.UsagePage,
            caps.Usage,
            caps.InputReportByteLength,
            caps.OutputReportByteLength,
            caps.FeatureReportByteLength);
    }

    private delegate bool HidStringGetter(SafeFileHandle handle, StringBuilder buffer, int length);

    private static string GetHidString(HidStringGetter getter, SafeFileHandle handle)
    {
        StringBuilder buffer = new(256);
        return getter(handle, buffer, buffer.Capacity * 2) ? buffer.ToString() : string.Empty;
    }

    private sealed record DeviceIdentity(
        string Path,
        ushort Vid,
        ushort Pid,
        string Manufacturer,
        string Product,
        string Serial,
        ushort UsagePage,
        ushort Usage,
        ushort InputReportLength,
        ushort OutputReportLength,
        ushort FeatureReportLength)
    {
        public bool MatchesExpected => MatchesExpectedIdentity(
            Path,
            Vid,
            Pid,
            Manufacturer,
            Product,
            Serial,
            UsagePage,
            Usage,
            InputReportLength,
            OutputReportLength,
            FeatureReportLength);

        public bool MatchesExpectedEvent => MatchesExpectedEventIdentity(
            Path,
            Vid,
            Pid,
            Manufacturer,
            Product,
            Serial,
            UsagePage,
            Usage,
            InputReportLength,
            OutputReportLength,
            FeatureReportLength);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Overlapped
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint Offset;
            public uint OffsetHigh;
            public IntPtr EventHandle;
        }

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HiddAttributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_SetOutputReport(
            SafeFileHandle handle,
            byte[] reportBuffer,
            int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetInputReport(SafeFileHandle handle, byte[] reportBuffer, int reportBufferLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle handle,
            uint controlCode,
            IntPtr inputBuffer,
            int inputBufferSize,
            IntPtr outputBuffer,
            int outputBufferSize,
            IntPtr bytesReturned,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, int bytesToRead,
            IntPtr bytesRead, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped,
            out int bytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetManufacturerString(
            SafeFileHandle handle, StringBuilder buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetProductString(
            SafeFileHandle handle, StringBuilder buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetSerialNumberString(
            SafeFileHandle handle, StringBuilder buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HidD_FreePreparsedData(IntPtr data);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr data, out HidpCaps caps);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            ref Guid interfaceClassGuid,
            uint memberIndex,
            ref SpDeviceInterfaceData interfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet,
            ref SpDeviceInterfaceData interfaceData,
            IntPtr detailData,
            int detailDataSize,
            out int requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
    }
}
