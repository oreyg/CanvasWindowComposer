using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Windows.Win32.UI.Input;

namespace CanvasDesktop;

/// <summary>
/// Dedicated polling thread for raw mouse input.
/// </summary>
internal sealed class RawMouseInput : IDisposable
{
    private const int KeyStateDownBit = 0x8000;

    // Initial buffer size — grown on demand if a wake brings more events than fit.
    // Without growth, GetRawInputBuffer returns ERROR_INSUFFICIENT_BUFFER and we
    // can't drain. The OS then coalesces queued WM_INPUT messages, summing per-
    // report lLastX into a single coarse delta — which makes our per-event curve
    // amplification land in a higher band than Windows applied to the cursor,
    // producing a perceived 2x pan after any drag that left the UI thread busy.
    private const int RawInputBufferInitialBytes = 16 * 1024;
    private const int ERROR_INSUFFICIENT_BUFFER = 0x7A;
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint WAIT_FAILED = 0xFFFFFFFF;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly IAppConfig _config;
    private readonly MouseCurveScaler? _curve;

    private volatile HashSet<IntPtr> _extraPanSurfaces = new();
    public void SetExtraPanSurfaces(IEnumerable<IntPtr> handles)
    {
        _extraPanSurfaces = new HashSet<IntPtr>(handles);
    }
    public void ClearExtraPanSurfaces()
    {
        _extraPanSurfaces = new HashSet<IntPtr>();
    }

    public bool Enabled { get; set; } = true;

    /// <summary>Ring buffer of parsed mouse events. Drained by the UI thread.</summary>
    public MouseEventRingBuffer Events { get; } = new(256);

    private readonly SynchronizationContext _uiContext;
    private readonly SendOrPostCallback _uiPost;
    private readonly Action _onFrame;
    private int _uiPostPending;

    // Drag state — polling thread only
    private bool _dragging;
    private bool _altDrag;
    // Timestamp of the previous raw motion event in this drag, used to estimate
    // how many native HID polls a coalesced event represents.
    private long _lastMotionTicks;
    // Cap on chunks per event so a long idle-then-burst doesn't flood the curve
    // with hundreds of sub-events that all land in band 0 anyway.
    private const int MaxCurveChunks = 50;
    // Default poll interval if a device reports dwSampleRate=0 (HID drivers
    // sometimes do this). 1ms matches the typical 1000Hz USB mouse.
    private const double DefaultPollIntervalMs = 1.0;
    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;
    // Per-device cached poll interval (ms-per-HID-report) read from
    // RID_DEVICE_INFO_MOUSE.dwSampleRate. Only ever read/written on the
    // polling thread.
    private readonly Dictionary<IntPtr, double> _pollIntervalByDevice = new();

    private Thread? _thread;
    private NativeWindow? _sink;
    private readonly ManualResetEvent _shutdown = new(false);
    private readonly ManualResetEventSlim _started = new(false);

    /// <param name="config">App config (DisableAltPan, DisableMouseCurve).</param>
    /// <param name="onFrame">Invoked on the UI thread once per drain burst.</param>
    /// <remarks>
    /// Must be constructed on the UI thread — captures
    /// <see cref="SynchronizationContext.Current"/> at ctor time so the polling
    /// thread can marshal the per-frame callback back.
    /// </remarks>
    public RawMouseInput(IAppConfig config, Action onFrame)
    {
        _config = config;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("RawMouseInput must be constructed on the UI thread.");
        _onFrame = onFrame;
        _uiPost = _ =>
        {
            Interlocked.Exchange(ref _uiPostPending, 0);
            _onFrame();
        };
        _curve = config.DisableMouseCurve ? null : new MouseCurveScaler();
    }

    public void Install()
    {
        EnsureNotWow64();

        _thread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "RawMouseInput",
            // High priority keeps the polling thread scheduled even when the UI
            // thread is busy (inertia + thumbnail updates), reducing the chance
            // that the OS coalesces queued WM_INPUT into coarse summed events.
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException("Raw input thread failed to start.");
    }

    public void Dispose()
    {
        _shutdown.Set();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _shutdown.Dispose();
        _started.Dispose();
    }

    /// <summary>
    /// We trust the CsWin32-generated RAWINPUT layout for the process bitness.
    /// Under WOW64 (32-bit on 64-bit Windows), the kernel returns RAWINPUT in
    /// 64-bit layout — header is 8 bytes larger than what our struct expects,
    /// shifting the RAWMOUSE union and producing garbage deltas. We ship x64
    /// only, so just refuse to start under WOW64 rather than carry an offset
    /// fixup we can't realistically test.
    /// </summary>
    private static unsafe void EnsureNotWow64()
    {
        BOOL isWow64 = false;
        if (PInvoke.IsWow64Process(PInvoke.GetCurrentProcess(), &isWow64) && isWow64)
        {
            throw new PlatformNotSupportedException(
                "RawMouseInput does not support 32-bit process on 64-bit Windows (WOW64).");
        }
    }

    // ==================== POLLING THREAD ====================

    private unsafe void ThreadProc()
    {
        bool registered = CreateSinkAndRegister();
        _started.Set();
        if (!registered) return;

        // Heap-allocated, grown on ERROR_INSUFFICIENT_BUFFER. SDL does the same.
        // A stack buffer can't grow, so a momentary backlog (UI thread busy with
        // inertia + thumbnail updates) would leave events undrained -> coalesced.
        int bufferBytes = RawInputBufferInitialBytes;
        IntPtr buffer = Marshal.AllocHGlobal(bufferBytes);
        HANDLE shutdown = (HANDLE)_shutdown.SafeWaitHandle.DangerousGetHandle();

        try
        {
            while (WaitForInput(shutdown))
            {
                if (DrainInput(ref buffer, ref bufferBytes)
                    && Interlocked.Exchange(ref _uiPostPending, 1) == 0)
                {
                    _uiContext.Post(_uiPost, null);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            _sink!.DestroyHandle();
        }
    }

    /// <summary>
    /// Create the message-only sink HWND and register for raw mouse input.
    /// The sink exists only to host the device registration — we never dispatch
    /// from its WndProc; <see cref="PInvoke.GetRawInputBuffer"/> drains the
    /// WM_INPUT messages directly.
    /// </summary>
    private unsafe bool CreateSinkAndRegister()
    {
        _sink = new NativeWindow();
        _sink.CreateHandle(new CreateParams { Parent = HWND_MESSAGE });

        RAWINPUTDEVICE rid = new()
        {
            usUsagePage = 0x01, // Generic desktop
            usUsage = 0x02,     // Mouse
            dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK,
            hwndTarget = (HWND)_sink.Handle
        };
        if (PInvoke.RegisterRawInputDevices(&rid, 1, (uint)sizeof(RAWINPUTDEVICE)))
            return true;

        _sink.DestroyHandle();
        return false;
    }

    /// <summary>
    /// Block until raw input arrives or shutdown is signaled. Returns true if
    /// the wake was triggered by raw input (caller should drain), false on
    /// shutdown / wait failure (caller should exit the loop).
    /// </summary>
    private unsafe bool WaitForInput(HANDLE shutdown)
    {
        HANDLE* handles = stackalloc HANDLE[1];
        handles[0] = shutdown;
        uint result = (uint)PInvoke.MsgWaitForMultipleObjects(
            1, handles, false, INFINITE, QUEUE_STATUS_FLAGS.QS_RAWINPUT);
        return result != WAIT_OBJECT_0 && result != WAIT_FAILED;
    }

    /// <summary>
    /// Drain everything in the raw input queue into <see cref="OnRawMouse"/>.
    /// Grows the buffer on <c>ERROR_INSUFFICIENT_BUFFER</c> instead of
    /// dropping events — see the field-level comment for why that matters.
    /// Returns true if at least one mouse event was processed.
    /// </summary>
    /// <remarks>
    /// Also pulls WM_INPUT messages off the thread queue via PeekMessage.
    /// GetRawInputBuffer reads raw-input payloads from a kernel buffer and
    /// does NOT dequeue the WM_INPUT messages themselves, so QS_RAWINPUT
    /// stays set and <see cref="MsgWaitForMultipleObjects"/> returns
    /// immediately on the next call — the thread spins. PeekMessage(PM_REMOVE)
    /// resets the queue state so the next wait actually blocks.
    /// </remarks>
    private unsafe bool DrainInput(ref IntPtr buffer, ref int bufferBytes)
    {
        uint headerSize = (uint)sizeof(RAWINPUTHEADER);
        bool hadInput = false;
        while (true)
        {
            uint size = (uint)bufferBytes;
            uint count = PInvoke.GetRawInputBuffer((RAWINPUT*)buffer, ref size, headerSize);
            if (count == 0) break;
            if (count == unchecked((uint)-1))
            {
                if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER) break;
                // size now holds the required size for at least one event;
                // double it so we don't grow on every burst.
                int needed = Math.Max((int)size * 2, bufferBytes * 2);
                buffer = Marshal.ReAllocHGlobal(buffer, (IntPtr)needed);
                bufferBytes = needed;
                continue;
            }

            long ts = Stopwatch.GetTimestamp();
            RAWINPUT* raw = (RAWINPUT*)buffer;
            for (uint i = 0; i < count; i++)
            {
                if (raw->header.dwType == (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEMOUSE)
                {
                    OnRawMouse(ref raw->data.mouse, raw->header.hDevice, ts);
                    hadInput = true;
                }
                raw = NextBlock(raw);
            }
        }

        MSG msg;
        while (PInvoke.PeekMessage(&msg, HWND.Null, PInvoke.WM_INPUT, PInvoke.WM_INPUT,
            PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
        {
            // discard; payload was drained via GetRawInputBuffer above.
        }

        return hadInput;
    }

    /// <summary>NEXTRAWINPUTBLOCK: advance by header.dwSize, aligned to IntPtr.Size.</summary>
    private static unsafe RAWINPUT* NextBlock(RAWINPUT* ptr)
    {
        nuint next = (nuint)((byte*)ptr + ptr->header.dwSize);
        nuint alignMask = (nuint)IntPtr.Size - 1;
        return (RAWINPUT*)((next + alignMask) & ~alignMask);
    }

    private void OnRawMouse(ref RAWMOUSE mouse, IntPtr hDevice, long ts)
    {
        if (!Enabled) return;

        ushort btn = mouse.Anonymous.Anonymous.usButtonFlags;

        if ((btn & PInvoke.RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0)
            OnMiddleDown(ts);
        if ((btn & PInvoke.RI_MOUSE_MIDDLE_BUTTON_UP) != 0)
            OnMiddleUp(ts);
        if ((btn & (PInvoke.RI_MOUSE_LEFT_BUTTON_DOWN | PInvoke.RI_MOUSE_RIGHT_BUTTON_DOWN)) != 0)
            Events.TryEnqueue(new MouseEvent(MouseEventType.ButtonDown, timestamp: ts));
        if ((btn & PInvoke.RI_MOUSE_WHEEL) != 0)
            OnWheel(ts);

        if (_dragging && (mouse.lLastX != 0 || mouse.lLastY != 0))
        {
            int dx = mouse.lLastX;
            int dy = mouse.lLastY;
            if (_curve != null)
            {
                // Estimate native HID polls represented: gap_ms / poll_interval.
                // Per-device interval is queried lazily from the HID driver
                // (RID_DEVICE_INFO_MOUSE.dwSampleRate). A bigger ratio means
                // the OS coalesced multiple per-tick reports into one event
                // with a summed lLastX; passing the chunk count lets the curve
                // amplify per-tick the way Windows did to the cursor.
                double gapMs = (ts - _lastMotionTicks) / TicksPerMs;
                double pollMs = GetPollIntervalMs(hDevice);
                int chunks = Math.Clamp((int)Math.Round(gapMs / pollMs), 1, MaxCurveChunks);
                _curve.Apply(dx, dy, chunks, out dx, out dy);
            }
            _lastMotionTicks = ts;
            if (dx != 0 || dy != 0)
                Events.TryEnqueue(new MouseEvent(MouseEventType.Pan, dx, dy, ts));
        }
    }

    private void OnMiddleDown(long ts)
    {
        Point pt = GetCursor();
        bool alt = !_config.DisableAltPan
                && IsAltDown()
                && !IsCtrlDown()
                && !IsShiftDown();
        if (alt || IsDesktopOrTaskbarAt(pt))
        {
            _dragging = true;
            _altDrag = alt;
            // Seed gap so the first motion event computes chunks=1, not a huge
            // value derived from the time since the previous drag.
            _lastMotionTicks = ts;
            Events.TryEnqueue(new MouseEvent(MouseEventType.DragStarted, timestamp: ts));
        }
        else
        {
            Events.TryEnqueue(new MouseEvent(MouseEventType.ButtonDown, timestamp: ts));
        }
    }

    private void OnMiddleUp(long ts)
    {
        if (!_dragging) return;
        _dragging = false;
        // Curve scaler accumulates last-band + sub-pixel state across events.
        // Carrying that into the next drag gives it a different scale than the
        // first drag started with — pan diverges from cursor on drag 2+.
        _curve?.ResetGestureState();
        Events.TryEnqueue(new MouseEvent(MouseEventType.DragEnded, timestamp: ts));
    }

    private void OnWheel(long ts)
    {
        bool alt = IsAltDown() && !IsCtrlDown() && !IsShiftDown();
        if (alt && IsDesktopOrTaskbarAt(GetCursor()))
            Events.TryEnqueue(new MouseEvent(MouseEventType.Zoom, timestamp: ts));
    }

    // ==================== HELPERS ====================

    private static Point GetCursor()
    {
        PInvoke.GetCursorPos(out var pt);
        return new Point(pt.X, pt.Y);
    }

    private static bool IsAltDown()
    {
        return (PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_MENU) & KeyStateDownBit) != 0;
    }

    private static bool IsCtrlDown()
    {
        return (PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_CONTROL) & KeyStateDownBit) != 0;
    }

    private static bool IsShiftDown()
    {
        return (PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_SHIFT) & KeyStateDownBit) != 0;
    }

    private unsafe bool IsDesktopOrTaskbarAt(Point pt)
    {
        var pp = new System.Drawing.Point(pt.X, pt.Y);
        HWND hwnd = PInvoke.WindowFromPoint(pp);
        if (hwnd == HWND.Null)
            return true;

        HWND root = PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
        if (root == HWND.Null)
            root = hwnd;

        if (_extraPanSurfaces.Contains(root))
            return true;

        HWND desktop = PInvoke.GetDesktopWindow();
        HWND shell = PInvoke.GetShellWindow();
        if (root == desktop || root == shell)
            return true;

        Span<char> classBuf = stackalloc char[256];
        int classLen;
        fixed (char* p = classBuf)
        {
            classLen = PInvoke.GetClassName(root, new PWSTR(p), classBuf.Length);
        }
        if (classLen == 0)
            return false;
        string cls = new string(classBuf[..classLen]);
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    // ==================== HID DEVICE INFO ====================
    //
    // CsWin32 doesn't surface GetRawInputDeviceInfo / RID_DEVICE_INFO_MOUSE in
    // the metadata it generates from, so declare them by hand. We only need
    // dwSampleRate to convert "ms gap between events" into "native HID polls
    // since last event" for the curve chunking.

    private const uint RIDI_DEVICEINFO = 0x2000000b;

    [StructLayout(LayoutKind.Sequential)]
    private struct RidDeviceInfoMouse
    {
        public uint dwId;
        public uint dwNumberOfButtons;
        public uint dwSampleRate;
        public BOOL fHasHorizontalWheel;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RidDeviceInfo
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint dwType;
        // Union starts at offset 8; only the mouse variant matters for us.
        [FieldOffset(8)] public RidDeviceInfoMouse mouse;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern unsafe uint GetRawInputDeviceInfo(IntPtr hDevice, uint command, void* data, ref uint size);

    /// <summary>
    /// Returns the polling interval in milliseconds for the given HID device,
    /// queried (and cached) from <c>RID_DEVICE_INFO_MOUSE.dwSampleRate</c>.
    /// Falls back to <see cref="DefaultPollIntervalMs"/> if the driver reports
    /// no rate (some HID stacks return 0).
    /// </summary>
    private double GetPollIntervalMs(IntPtr hDevice)
    {
        if (_pollIntervalByDevice.TryGetValue(hDevice, out double cached))
            return cached;

        double intervalMs = DefaultPollIntervalMs;
        unsafe
        {
            RidDeviceInfo info = default;
            info.cbSize = (uint)sizeof(RidDeviceInfo);
            uint size = info.cbSize;
            uint result = GetRawInputDeviceInfo(hDevice, RIDI_DEVICEINFO, &info, ref size);
            if (result != unchecked((uint)-1) && result != 0
                && info.dwType == (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEMOUSE
                && info.mouse.dwSampleRate > 0)
            {
                intervalMs = 1000.0 / info.mouse.dwSampleRate;
            }
        }

        _pollIntervalByDevice[hDevice] = intervalMs;
        return intervalMs;
    }
}
