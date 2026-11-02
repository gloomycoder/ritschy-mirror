using System.Runtime.InteropServices;

namespace RitschyMirror;

/// <summary>
/// Minimal Win32 window (no WinForms/WPF) — for borderless fullscreen on a
/// specific display or a small test window. Provides the HWND for the
/// DXGI swapchain and pumps the message loop.
/// </summary>
public sealed class Win32Window
{
    public IntPtr Hwnd { get; private set; }
    public bool Running { get; private set; } = true;

    // Window class WndProc: registered ONCE per process, function pointer to a STATIC
    // delegate that is rooted for the whole process lifetime as a static field → the GC can never
    // collect it. This removes two previously latent crash causes (fix v1.3.1, stream crash 01:17):
    //  (1) the delegate used to be an INSTANCE field → window<->delegate formed a
    //      self-referencing, GC-collectable cycle; if it was collected in the middle of DispatchMessage
    //      (the JIT keeps "this" alive no longer than necessary), Windows called a
    //      dead delegate → CLR FailFast "callback on a garbage collected delegate" (hard
    //      process death WITHOUT a log line — exactly the 01:17 pattern).
    //  (2) RegisterClassEx was retried for every window, but silently failed from the 2nd on
    //      (class already exists) → ALL windows shared the delegate of the FIRST; once its
    //      instance ended after a reinit, the class pointer dangled for all later windows.
    // Solution: one process-wide static WndProc that dispatches to the right window via
    // HWND→instance (unknown HWND → DefWindowProc, never a call on something dead).
    private static readonly WndProcDelegate s_wndProc = StaticWndProc;
    private static readonly object s_lock = new();
    private static readonly Dictionary<IntPtr, Win32Window> s_windows = new();
    private static bool s_classRegistered;
    private const string ClassName = "RitschyMirrorWindowClass";

    // ── Mouse lock (output_mode "fullscreen_block") ─────────────────────
    // Low-level mouse hook keeps the cursor out of the target display rectangle. Works
    // on ANY setup (including cross-adapter), without true exclusive fullscreen.
    private LowLevelMouseProc? _mouseProc;
    private IntPtr _mouseHook = IntPtr.Zero;
    private RECT _blockRect;
    private bool _inReposition;
    // Own thread ONLY for the mouse hook (see EnableCursorBlock).
    private Thread? _hookThread;
    private volatile uint _hookThreadId;

    public Win32Window(string title, int x, int y, int width, int height, bool borderless)
    {
        // Register the class only ONCE per process (static WndProc, see field comment above).
        lock (s_lock)
        {
            if (!s_classRegistered)
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    style = 0x0002 | 0x0001, // CS_HREDRAW | CS_VREDRAW
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                    hInstance = GetModuleHandle(null),
                    hCursor = LoadCursor(IntPtr.Zero, 32512), // IDC_ARROW
                    hbrBackground = IntPtr.Zero,
                    lpszClassName = ClassName,
                };
                RegisterClassEx(ref wc);
                s_classRegistered = true;
            }
        }

        // Borderless (WS_POPUP) for fullscreen, otherwise overlapped window.
        uint style = borderless ? 0x80000000 /*WS_POPUP*/ : 0x00CF0000 /*WS_OVERLAPPEDWINDOW*/;
        uint exStyle = borderless ? 0x00000008u /*WS_EX_TOPMOST*/ : 0u;

        Hwnd = CreateWindowEx(
            exStyle, ClassName, title, style,
            x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (Hwnd != IntPtr.Zero) { lock (s_lock) s_windows[Hwnd] = this; }

        ShowWindow(Hwnd, 5 /*SW_SHOW*/);
        UpdateWindow(Hwnd);
    }

    /// <summary>Process all pending messages without blocking.</summary>
    public void PumpMessages()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1 /*PM_REMOVE*/))
        {
            if (msg.message == 0x0012 /*WM_QUIT*/) { Running = false; break; }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    /// <summary>
    /// Explicitly destroy the window. Needed for the reinit BETWEEN render sessions: there
    /// the render thread does NOT end (it rebuilds the chain), so the old HWND would otherwise
    /// stay on the target display as a borderless topmost ghost window while the
    /// new one opens behind it. Must run on the creator thread (= render thread).
    /// </summary>
    public void Destroy()
    {
        DisableCursorBlock();
        var h = Hwnd;
        Hwnd = IntPtr.Zero;
        if (h != IntPtr.Zero)
        {
            lock (s_lock) s_windows.Remove(h);
            DestroyWindow(h); // synchronous → WM_DESTROY → PostQuitMessage(0)
        }
        // Clear the WM_QUIT posted by WM_DESTROY (and leftover messages) here on the thread,
        // otherwise it would immediately end the fresh session on the next PumpMessages.
        while (PeekMessage(out _, IntPtr.Zero, 0, 0, 1 /*PM_REMOVE*/)) { }
        Running = false;
    }

    /// <summary>
    /// Keep the cursor out of the rectangle (l,t,r,b in screen coordinates).
    ///
    /// The WH_MOUSE_LL hook runs on its OWN, lean thread (only a GetMessage loop) —
    /// NOT on the render thread. Reason: a low-level mouse hook is dispatched on the installing
    /// thread and needs a quick message pump. If it ran on the render thread,
    /// it would only be serviced ~1×/frame (Present(vsync) blocks in between) → the mouse would be
    /// throttled system-wide to render cadence + stutter. On the dedicated thread the
    /// callback returns immediately → cursor stays smooth (full polling rate), the lock holds.
    /// </summary>
    public void EnableCursorBlock(int l, int t, int r, int b)
    {
        _blockRect = new RECT { Left = l, Top = t, Right = r, Bottom = b };
        if (_hookThread != null) return;
        _mouseProc = MouseHookProc; // keep as a field (GC)

        using var ready = new ManualResetEventSlim(false);
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            _mouseHook = SetWindowsHookEx(14 /*WH_MOUSE_LL*/, _mouseProc!, GetModuleHandle(null), 0);
            ready.Set();
            // Lean loop — does nothing except promptly serve the hook callback.
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        }) { IsBackground = true, Name = "MirrorMouseHook" };
        _hookThread.Start();
        ready.Wait(2000); // until the hook is installed
    }

    public void DisableCursorBlock()
    {
        var t = _hookThread;
        if (t != null)
        {
            // WM_QUIT to the hook thread → GetMessage loop ends → unhook there.
            if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
            if (Thread.CurrentThread != t) t.Join(2000);
            _hookThread = null;
            _hookThreadId = 0;
        }
        _mouseProc = null;
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_inReposition && wParam.ToInt32() == 0x0200 /*WM_MOUSEMOVE*/)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int x = data.pt_x, y = data.pt_y;
            if (x >= _blockRect.Left && x < _blockRect.Right && y >= _blockRect.Top && y < _blockRect.Bottom)
            {
                // Push to the nearest outer edge (smallest penetration depth).
                int dl = x - _blockRect.Left, dr = _blockRect.Right - x;
                int dt = y - _blockRect.Top,  db = _blockRect.Bottom - y;
                int min = System.Math.Min(System.Math.Min(dl, dr), System.Math.Min(dt, db));
                int nx = x, ny = y;
                if (min == dl)      nx = _blockRect.Left - 1;
                else if (min == dr) nx = _blockRect.Right;
                else if (min == dt) ny = _blockRect.Top - 1;
                else                ny = _blockRect.Bottom;
                _inReposition = true;
                SetCursorPos(nx, ny);
                _inReposition = false;
                return new IntPtr(1); // swallow the movement
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // Process-wide dispatcher: finds the instance for the HWND and forwards. Unknown HWND
    // (e.g. leftover message after Destroy) → DefWindowProc, never a call on something dead.
    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        Win32Window? self;
        lock (s_lock) s_windows.TryGetValue(hWnd, out self);
        return self != null
            ? self.InstanceWndProc(hWnd, msg, wParam, lParam)
            : DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr InstanceWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0010: // WM_CLOSE
                Running = false;
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            case 0x0002: // WM_DESTROY
                Running = false;
                lock (s_lock) s_windows.Remove(hWnd);
                DisableCursorBlock();
                PostQuitMessage(0);
                return IntPtr.Zero;
            case 0x0100: // WM_KEYDOWN
                if (wParam.ToInt32() == 0x1B /*VK_ESCAPE*/) { Running = false; DestroyWindow(hWnd); }
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ── P/Invoke ─────────────────────────────────────────────────────────
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int pt_x;
        public int pt_y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int X, int Y);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int X, int Y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int nExitCode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
