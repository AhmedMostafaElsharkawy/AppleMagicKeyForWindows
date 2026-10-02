using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MagicKeyBattery;

// Magic Keyboard のキー配置を Logitech MX Keys (Windows 配列) に合わせる低レベルキーボードフック
//
//   下段: control | option | command | space | command | option
//     →  Ctrl    | Win    | Alt     | space | AltGr   | Ctrl
//   F1〜F12: メディアキー優先 (Ctrl/Alt/Shift/Win を押している間は通常の F キー)
//   F16〜F19: 電卓 / Print Screen / メニュー / ロック
//
// fn キーは Windows に一切届かない (実機で確認済み) ため、fn の代わりに修飾キーとトレイの切り替えを使う。
internal static class KeyboardRemapper
{
    // --- 設定 ---
    public static bool RemapEnabled { get; set; } = true;       // MX Keys 配列 (修飾キー・F キー)
    public static bool SwitchingEnabled { get; set; } = true;   // F13/F14/F15 で入力先を切り替え
    public static bool MediaKeys { get; set; } = true;
    public static bool SwapIsoKeys { get; set; } = false;

    // Magic Keyboard が接続されている間だけ有効 (他のキーボードに影響させないため)
    public static volatile bool KeyboardConnected;

    public static bool IsRunning => _hook != IntPtr.Zero;

    // --- Win32 ---
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    private const int WH_KEYBOARD_LL = 13;
    private const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x01, KEYEVENTF_KEYUP = 0x02, KEYEVENTF_SCANCODE = 0x08;
    private static readonly IntPtr Signature = new IntPtr(0x4D4B4231); // "MKB1": 自分で送ったキーの目印

    private const ushort VK_TAB = 0x09, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const ushort VK_SNAPSHOT = 0x2C, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_APPS = 0x5D;
    private const ushort VK_F13 = 0x7C, VK_F14 = 0x7D, VK_F15 = 0x7E;
    private const ushort VK_F1 = 0x70, VK_F12 = 0x7B, VK_F16 = 0x7F, VK_F17 = 0x80, VK_F18 = 0x81, VK_F19 = 0x82;
    private const ushort VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private const ushort VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    private const ushort VK_MEDIA_NEXT = 0xB0, VK_MEDIA_PREV = 0xB1, VK_MEDIA_PLAY_PAUSE = 0xB3, VK_LAUNCH_APP2 = 0xB7;

    // 各キーの動作 (設定画面「キー配置」で変更できる。初期値は MX Keys 配列)
    private static Dictionary<uint, KeyAction> _map = KeyMapping.Defaults();

    public static void SetMapping(IReadOnlyDictionary<uint, KeyAction> map)
    {
        lock (_sync)
        {
            ReleaseHeldKeys();
            _map = new Dictionary<uint, KeyAction>(map);
        }
    }

    private static IntPtr _hook = IntPtr.Zero;
    private static LowLevelKeyboardProc? _proc; // GC に回収されないよう保持

    // フック専用スレッド:
    //   低レベルフックはフックを入れたスレッドのメッセージループで呼ばれる。UI スレッドが一瞬でも止まると
    //   (ログイン直後の高負荷、ダイアログ、WinRT 呼び出しなど) Windows はフックを黙って外してしまうため、
    //   何もしない専用スレッドで動かす。
    private static Thread? _hookThread;
    private static uint _hookThreadId;

    // フックが外されていないかの監視 (最後に呼ばれた時刻 vs 最後の入力時刻)
    private static long _lastHookCallbackTick;
    private const int WatchdogIntervalMs = 5000;

    // 押下中のキー (解放を確実に対応させるため)。フックのスレッドと UI スレッドの両方から触るのでロックする
    private static readonly object _sync = new();
    private static readonly Dictionary<uint, (KeyOut Key, IKeyOutput Output)> _heldRemapped = new();
    private static readonly HashSet<uint> _heldMedia = new();
    private static readonly HashSet<uint> _heldSwallowed = new();

    // キーボードが「未接続」のままキーが届いた時に電池/接続状態の再確認を頼む (Program が設定)
    public static Action? RefreshRequested;
    private static long _lastRefreshRequestTick;

    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    private const uint WM_QUIT = 0x0012, WM_TIMER = 0x0113;

    public static void Start()
    {
        if (_hookThread != null) return;

        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() => HookThreadMain(ready))
        {
            IsBackground = true,
            Name = "MagicKeyBattery keyboard hook",
            Priority = ThreadPriority.AboveNormal // 入力の遅延を最小に
        };
        _hookThread.Start();
        ready.Wait(3000);
    }

    public static void Stop()
    {
        ReleaseHeldKeys();
        Thread? thread = _hookThread;
        if (thread == null) return;
        PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(2000);
        _hookThread = null;
    }

    private static void HookThreadMain(ManualResetEventSlim ready)
    {
        _hookThreadId = GetCurrentThreadId();
        _proc = HookCallback;
        InstallHook();
        SetTimer(IntPtr.Zero, IntPtr.Zero, WatchdogIntervalMs, IntPtr.Zero);
        ready.Set();

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_TIMER) CheckHookAlive();
        }

        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private static void InstallHook()
    {
        // 新しいフックを先に入れてから古いものを外す (切り替えの間にキーが素通りしないように)
        IntPtr old = _hook;
        IntPtr hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc!, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            Debug.WriteLine($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
            return;
        }
        _hook = hook;
        Interlocked.Exchange(ref _lastHookCallbackTick, Environment.TickCount64);
        if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
    }

    // 最後の入力 (キー/マウス) よりフックの最終呼び出しが古い → 外された可能性があるので入れ直す
    private static void CheckHookAlive()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return;

        long lastInput = Environment.TickCount64 - (uint)(Environment.TickCount - (int)info.dwTime);
        long lastCallback = Interlocked.Read(ref _lastHookCallbackTick);
        if (lastInput - lastCallback > 1500) InstallHook();
    }

    // 無効化・切断・入力先の切り替え時に、押しっぱなしのまま残るキーが無いよう解放する
    public static void ReleaseHeldKeys()
    {
        lock (_sync)
        {
            foreach ((KeyOut k, IKeyOutput output) in _heldRemapped.Values) output.Key(k.Vk, k.Scan, k.Extended, up: true);
            _heldRemapped.Clear();
            _heldMedia.Clear();
            _heldSwallowed.Clear();
        }
    }

    // 変換後のキーの送り先 (通常はこの PC、F15 で別の PC を選んでいる間はネットワーク経由)
    private static IKeyOutput _output = LocalKeyOutput.Instance;

    public static void SetOutput(IKeyOutput output)
    {
        lock (_sync)
        {
            ReleaseHeldKeys();
            _output = output;
        }
    }

    // 受信側 (別の PC から届いたキー) の入力にも使う
    public static void InjectLocal(ushort vk, ushort scan, bool extended, bool up) => LocalKeyOutput.Instance.Key(vk, scan, extended, up);

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            Interlocked.Exchange(ref _lastHookCallbackTick, Environment.TickCount64);
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // TV モード: ドライバ (Magic Utilities など) が送り直した修飾キーも、チャンネル切り替え用に状態だけ追跡する
            if ((k.flags & LLKHF_INJECTED) != 0 && DeviceSwitcher.Mode == DeviceMode.Tv)
                DeviceSwitcher.TrackTvModifier(k.vkCode, (k.flags & LLKHF_UP) != 0);

            // 送信済み (自分や他のアプリ) の入力は変換しない
            if ((k.flags & LLKHF_INJECTED) == 0)
            {
                bool up = (k.flags & LLKHF_UP) != 0;
                bool extended = (k.flags & LLKHF_EXTENDED) != 0;
                try
                {
                    bool swallow;
                    lock (_sync) swallow = HandleKey(k.vkCode, k.scanCode, extended, up);
                    if (swallow) return (IntPtr)1;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"KeyboardRemapper: {ex}");
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // true = 元のキーを握りつぶす
    private static bool HandleKey(uint vk, uint scan, bool extended, bool up)
    {
        if (HandleKeyCore(vk, scan, extended, up)) return true;

        // 別の PC に入力中: 変換しなかったキーもそのまま (スキャンコードで) 送る → 相手の PC のキーボード配列で文字になる
        if (_output != LocalKeyOutput.Instance && KeyboardConnected)
        {
            _output.Key(0, (ushort)scan, extended, up);
            return true;
        }
        return false;
    }

    private static bool HandleKeyCore(uint vk, uint scan, bool extended, bool up)
    {
        // 解放は、押下時に変換したかどうかで決める (途中で設定が変わっても押しっぱなしにしない)
        if (up)
        {
            if (_heldRemapped.Remove(vk, out (KeyOut Key, IKeyOutput Output) held))
            {
                held.Output.Key(held.Key.Vk, held.Key.Scan, held.Key.Extended, up: true);
                return true;
            }
            if (_heldMedia.Remove(vk) || _heldSwallowed.Remove(vk)) return true;
        }

        if (!KeyboardConnected)
        {
            // 起動直後はキーボードがまだ Bluetooth で再接続中のことがある → キーが届いたら接続を確認し直す (10 秒に 1 回まで)
            long now = Environment.TickCount64;
            if (!up && now - Interlocked.Read(ref _lastRefreshRequestTick) > 10_000)
            {
                Interlocked.Exchange(ref _lastRefreshRequestTick, now);
                RefreshRequested?.Invoke();
            }
            return false;
        }

        KeyAction action = _map.TryGetValue(vk, out KeyAction mapped) ? mapped : KeyAction.Default;

        // --- 入力先の切り替え (初期値 F13 / F14 / F15 → PC / TV / スマホ or 別の PC) ---
        // TV モード中でも PC に戻れるよう、TV への転送より先に処理する
        if (SwitchingEnabled && KeyMapping.IsSwitch(action))
        {
            if (!up && _heldSwallowed.Add(vk)) // キーリピートは無視
            {
                DeviceSwitcher.RequestSwitch(action switch
                {
                    KeyAction.SwitchToPc => DeviceMode.Pc,
                    KeyAction.SwitchToTv => DeviceMode.Tv,
                    _ => DeviceSwitcher.F15Mode
                });
            }
            return true;
        }

        // --- TV モード: キーはすべて TV へ送り、PC には渡さない ---
        if (SwitchingEnabled && DeviceSwitcher.Mode == DeviceMode.Tv)
        {
            DeviceSwitcher.ForwardToTv(vk, scan, up);
            return true;
        }

        if (!RemapEnabled) return false;

        // AltGr 配列では右 Alt を押すと偽の左 Ctrl (scan 0x21D) が付いてくる
        // → 右 option を別のキーにしている時だけ捨てる (AltGr のままなら必要)
        if (vk == VK_LCONTROL && scan == 0x21D)
            return _map.TryGetValue(VK_RMENU, out KeyAction rightOption) && rightOption != KeyAction.Default;

        if (up) return false;

        // --- ドイツ語 ISO 配列: Apple は ^ と < のキーコードが PC と逆 ---
        if (SwapIsoKeys && !extended && (scan == 0x29 || scan == 0x56))
        {
            var swapped = new KeyOut(0, (ushort)(scan == 0x29 ? 0x56 : 0x29), false);
            _heldRemapped[vk] = (swapped, _output);
            _output.Key(0, swapped.Scan, false, up: false);
            return true;
        }

        if (action == KeyAction.Default) return false;

        // F1〜F12: 「メディアキー」がオフ、または修飾キーを押している間は通常の F キー (Alt+F4, Ctrl+F5 など)
        // (押している最中のキーは最後まで同じ動作を続ける)
        bool topRow = vk >= VK_F1 && vk <= VK_F12;
        bool alreadyHeld = _heldRemapped.ContainsKey(vk) || _heldMedia.Contains(vk);
        if (topRow && !alreadyHeld && (!MediaKeys || _output.AnyModifierDown())) return false;

        if (action == KeyAction.Disabled)
        {
            _heldSwallowed.Add(vk);
            return true;
        }

        // --- 押している間そのキーとして送る (修飾キー・メディア・音量など。キーリピートもそのまま) ---
        if (KeyMapping.TryGetHeldOutput(action, out KeyOut output))
        {
            // キーリピート: 音量・修飾キー以外は無視 (再生/一時停止が何度も切り替わる・電卓が何個も開くのを防ぐ)
            if (_heldRemapped.ContainsKey(vk) && !KeyMapping.RepeatsWhileHeld(action)) return true;
            _heldRemapped[vk] = (output, _output);
            _output.Key(output.Vk, output.Scan, output.Extended, up: false);
            return true;
        }

        // --- 1 回だけ実行する操作 (キーリピートでは明るさだけ繰り返す) ---
        if (_heldMedia.Contains(vk))
        {
            if (KeyMapping.IsRepeatable(action)) Perform(action);
            return true;
        }
        _heldMedia.Add(vk);
        Perform(action);
        return true;
    }

    private static void Perform(KeyAction action)
    {
        switch (action)
        {
            case KeyAction.BrightnessDown: _output.Brightness(-10); return;
            case KeyAction.BrightnessUp: _output.Brightness(+10); return;
            case KeyAction.LockPc: _output.Lock(); return; // Win+L は送信できないため API で
        }

        KeyOut[]? chord = KeyMapping.ChordFor(action);
        if (chord != null) SendChord(chord);
    }

    // 例: Win+Tab → Win↓ Tab↓ Tab↑ Win↑
    private static void SendChord(params KeyOut[] keys)
    {
        foreach (KeyOut k in keys) _output.Key(k.Vk, k.Scan, k.Extended, up: false);
        for (int i = keys.Length - 1; i >= 0; i--) _output.Key(keys[i].Vk, keys[i].Scan, keys[i].Extended, up: true);
    }

    // --- この PC への送信 ---

    internal sealed class LocalKeyOutput : IKeyOutput
    {
        public static readonly LocalKeyOutput Instance = new();

        // フックはキーを押すたびに呼ばれるため、配列は使い回す
        private static readonly int[] ModifierKeys = { VK_CONTROL, VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN };

        // vk = 0 ならスキャンコードで送る (キーボードレイアウトに文字を決めさせる)
        public void Key(ushort vk, ushort scan, bool extended, bool up)
        {
            uint flags = (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
            if (vk == 0) flags |= KEYEVENTF_SCANCODE;
            SendInput(1, new[] { MakeInput(vk, scan, flags) }, Marshal.SizeOf<INPUT>());
        }

        public void Lock() => LockWorkStation();

        public void Brightness(int delta) => MonitorBrightness.ChangeAsync(delta);

        public bool AnyModifierDown()
        {
            foreach (int key in ModifierKeys)
            {
                if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
            }
            return false;
        }
    }

    private static INPUT MakeInput(ushort vk, ushort scan, uint flags) => new INPUT
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = Signature } }
    };
}

// 変換後のキーの送り先 (この PC / ネットワーク越しの別の PC)
internal interface IKeyOutput
{
    // vk = 0 ならスキャンコードで送る
    void Key(ushort vk, ushort scan, bool extended, bool up);
    void Lock();
    void Brightness(int delta);
    bool AnyModifierDown();
}

// 外部モニターの明るさ (DDC/CI)。遅いのでフックの外 (スレッドプール) で実行する
internal static class MonitorBrightness
{
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint min, out uint current, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint brightness);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    private static int _pendingDelta;
    private static int _running;

    // キーリピートで連続して呼ばれるため、変化量をまとめて 1 回ずつ適用
    public static void ChangeAsync(int delta)
    {
        Interlocked.Add(ref _pendingDelta, delta);
        if (Interlocked.Exchange(ref _running, 1) == 1) return;

        Task.Run(() =>
        {
            try
            {
                int d;
                while ((d = Interlocked.Exchange(ref _pendingDelta, 0)) != 0) Apply(d);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MonitorBrightness: {ex}");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    private static void Apply(int deltaPercent)
    {
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, dc, rect, data) => { handles.Add(h); return true; }, IntPtr.Zero);

        foreach (IntPtr hMonitor in handles)
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0) continue;
            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors)) continue;
            try
            {
                foreach (PHYSICAL_MONITOR m in monitors)
                {
                    // DDC/CI 非対応のモニターは失敗するだけ
                    if (!GetMonitorBrightness(m.hPhysicalMonitor, out uint min, out uint cur, out uint max) || max <= min) continue;
                    int step = (int)Math.Round((max - min) * deltaPercent / 100.0);
                    uint next = (uint)Math.Clamp((int)cur + step, (int)min, (int)max);
                    if (next != cur) SetMonitorBrightness(m.hPhysicalMonitor, next);
                }
            }
            finally
            {
                DestroyPhysicalMonitors(count, monitors);
            }
        }
    }
}
