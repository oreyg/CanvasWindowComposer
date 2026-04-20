using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Windows.Win32.UI.Input;

namespace CanvasDesktop;

/// <summary>
/// Dedicated polling thread for raw mouse input. Replaces the old WH_MOUSE_LL
/// hook so no UI/close work runs on a hook thread (LowLevelHooksTimeout ~300ms;
/// ReprojectSync can exceed that). Paces on DwmFlush (monitor vsync): on each
/// frame, drains all accumulated raw events via a single GetRawInputBuffer
/// call, parses them, pushes parsed events to a ring buffer, and signals the
/// UI thread to consume the buffer.
/// </summary>
internal sealed class RawMouseInput : IDisposable
{
    private const int KeyStateDownBit = 0x8000;
    private const int RawInputBufferBytes = 16 * 1024; // ~350 events worth at x64 layout
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly IAppConfig _config;

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

    // Drag state — polling thread only
    private bool _dragging;
    private bool _altDrag;

    private Thread? _thread;
    private NativeWindow? _sink;
    private volatile bool _alive = true;
    private readonly ManualResetEventSlim _started = new(false);

    public bool IsDragging
    {
        get { return _dragging; }
    }

    /// <param name="config">App config (used for DisableAltPan).</param>
    /// <param name="onFrame">Invoked on the UI thread once per vsync when the buffer has new events.</param>
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
        _uiPost = _ => _onFrame();
    }

    public void Install()
    {
        _thread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "RawMouseInput"
        };
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException("Raw input thread failed to start.");
    }

    public void Dispose()
    {
        _alive = false;
        _thread?.Join(TimeSpan.FromSeconds(1));
        _started.Dispose();
    }

    // ==================== POLLING THREAD ====================

    private unsafe void ThreadProc()
    {
        // Sink HWND is required for RegisterRawInputDevices. We don't route any
        // messages through its WndProc — GetRawInputBuffer drains WM_INPUT directly.
        _sink = new NativeWindow();
        _sink.CreateHandle(new CreateParams { Parent = HWND_MESSAGE });

        RAWINPUTDEVICE rid = new()
        {
            usUsagePage = 0x01, // Generic desktop
            usUsage = 0x02,     // Mouse
            dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK,
            hwndTarget = (HWND)_sink.Handle
        };
        BOOL ok = PInvoke.RegisterRawInputDevices(&rid, 1, (uint)sizeof(RAWINPUTDEVICE));
        _started.Set();
        if (!ok)
        {
            _sink.DestroyHandle();
            return;
        }

        byte* buffer = stackalloc byte[RawInputBufferBytes];
        uint headerSize = (uint)sizeof(RAWINPUTHEADER);

        while (_alive)
        {
            PInvoke.DwmFlush(); // block until next compose pass (~vsync)
            if (!_alive) break;

            bool hadInput = false;
            while (true)
            {
                uint size = RawInputBufferBytes;
                uint count = PInvoke.GetRawInputBuffer((RAWINPUT*)buffer, ref size, headerSize);
                if (count == 0 || count == unchecked((uint)-1))
                    break;

                RAWINPUT* raw = (RAWINPUT*)buffer;
                for (uint i = 0; i < count; i++)
                {
                    if (raw->header.dwType == (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEMOUSE)
                    {
                        OnRawMouse(ref raw->data.mouse);
                        hadInput = true;
                    }
                    raw = NextBlock(raw);
                }
            }

            if (hadInput)
                _uiContext.Post(_uiPost, null);
        }

        _sink.DestroyHandle();
    }

    /// <summary>NEXTRAWINPUTBLOCK: advance by header.dwSize, aligned to IntPtr.Size.</summary>
    private static unsafe RAWINPUT* NextBlock(RAWINPUT* ptr)
    {
        nuint next = (nuint)((byte*)ptr + ptr->header.dwSize);
        nuint alignMask = (nuint)IntPtr.Size - 1;
        return (RAWINPUT*)((next + alignMask) & ~alignMask);
    }

    private void OnRawMouse(ref RAWMOUSE mouse)
    {
        if (!Enabled) return;

        ushort btn = mouse.Anonymous.Anonymous.usButtonFlags;

        if ((btn & PInvoke.RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0)
            OnMiddleDown();
        if ((btn & PInvoke.RI_MOUSE_MIDDLE_BUTTON_UP) != 0)
            OnMiddleUp();
        if ((btn & (PInvoke.RI_MOUSE_LEFT_BUTTON_DOWN | PInvoke.RI_MOUSE_RIGHT_BUTTON_DOWN)) != 0)
            Events.TryEnqueue(new MouseEvent(MouseEventType.ButtonDown));
        if ((btn & PInvoke.RI_MOUSE_WHEEL) != 0)
            OnWheel();

        if (_dragging && (mouse.lLastX != 0 || mouse.lLastY != 0))
            Events.TryEnqueue(new MouseEvent(MouseEventType.Pan, mouse.lLastX, mouse.lLastY));
    }

    private void OnMiddleDown()
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
            Events.TryEnqueue(new MouseEvent(MouseEventType.DragStarted));
        }
        else
        {
            Events.TryEnqueue(new MouseEvent(MouseEventType.ButtonDown));
        }
    }

    private void OnMiddleUp()
    {
        if (!_dragging) return;
        _dragging = false;
        Events.TryEnqueue(new MouseEvent(MouseEventType.DragEnded));
    }

    private void OnWheel()
    {
        bool alt = IsAltDown() && !IsCtrlDown() && !IsShiftDown();
        if (alt && IsDesktopOrTaskbarAt(GetCursor()))
            Events.TryEnqueue(new MouseEvent(MouseEventType.Zoom));
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
}
