using System;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windows.Devices.Enumeration;
using Microsoft.Win32;

namespace MagicKeyBattery;

static class Program
{
    private static System.Windows.Forms.Timer? _timer;
    private static System.Windows.Forms.Timer? _debounceTimer;
    private static System.Windows.Forms.Timer? _retryTimer;
    private static readonly long StartedTick = Environment.TickCount64;
    private static NotifyIcon? _trayIcon;
    private static ToolStripMenuItem? _batteryMenu;
    private static Icon? _currentIcon;
    private static Mutex? _singleInstanceMutex;
    private static HistoryForm? _historyForm;
    private static SynchronizationContext? _uiContext;

    // 接続/切断をリアルタイムで検知するウォッチャー
    private static DeviceWatcher? _hidWatcher;

    // 更新処理の多重実行防止
    private static bool _updating = false;
    private static bool _updatePending = false;

    // 内部設定値（レジストリ同期用）
    private static int _intervalMinutes = 3;
    private static int _notifyThreshold = 20;
    private static int _criticalThreshold = 10;   // 2 回目の緊急の警告
    private static bool _notifyFull = true;       // 100% になったら「ケーブルを外せます」
    private static bool _hasNotifiedCritical = false;
    private static bool _hasNotifiedFull = false;
    private static byte _previousLevel = 0;       // 満充電の検出用 (前回の残量)

    // キー配置 (「キー配置」画面で変更)
    private static Dictionary<uint, KeyAction> _keyMap = KeyMapping.Defaults();
    private static KeyMapForm? _keyMapForm;

    // 別の PC (F15) とクリップボードのテキストを共有
    private static bool _shareClipboard = true;

    // キー配置 (MX Keys 風) の設定
    private static bool _remapEnabled = false;
    private static bool _mediaKeys = true;
    private static bool _swapIsoKeys = false;
    private static ToolStripMenuItem? _mediaKeysMenu;
    private static ToolStripMenuItem? _serialMenu;
    private static string? _keyboardSerial;

    // F13/F14/F15 の入力先切り替え
    private static bool _deviceSwitching = true;
    private static string _tvIp = "";
    private static string _tvClientKey = "";
    private static string _tvCertPin = "";
    private static bool _migrateLegacySecret = false;

    // F15 = 別の PC (受信側でも MagicKeyBattery を動かす)
    private static bool _f15IsPc = false;
    private static string _remotePcHost = "";
    private static string _remotePcToken = "";   // Base64 (レジストリには DPAPI で暗号化して保存)
    private static string _remotePcCertPin = "";
    private static bool _receiverEnabled = false;
    private static DevicesForm? _devicesForm;
    private static string _language = ""; // 空 = OS のロケールから自動判定
    private static bool _hasNotifiedLowBattery = false;

    // 履歴ログ (値が変わった時だけ記録)
    private static byte _lastLoggedLevel = 0;
    private static bool _lastLoggedCharging = false;
    // exe の隣の MagicKeyBattery-data (書き込めない場所なら %LOCALAPPDATA%)
    private static string HistoryDir => AppStorage.DataDir;
    private static string HistoryFile => AppStorage.HistoryFile;
    private const long HISTORY_MAX_BYTES = 512 * 1024;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    private const string REG_RUN_KEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string APP_NAME = "MagicKeyBattery";
    // csproj の <Version> (例: 1.3.1)
    private static readonly string AppVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

    // --- Win32 API 宣言 ---
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetInputReport(IntPtr HidDeviceObject, byte[] ReportBuffer, uint ReportBufferLength);
    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(IntPtr HidDeviceObject, out IntPtr PreparsedData);
    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);
    [DllImport("hid.dll")]
    private static extern bool HidD_GetSerialNumberString(IntPtr HidDeviceObject, byte[] Buffer, uint BufferLength);
    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr PreparsedData, byte[] Capabilities);

    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    private const byte BATTERY_REPORT_ID = 0x90;

    // --- デバイス識別 (名前ではなく VID/PID で判定) ---
    private const string HID_INTERFACE_CLASS = "{4D1E55B2-F16F-11CF-88CB-001111000030}";
    private const string BT_CLASSIC_PROTOCOL = "{E0CBF06C-CD8B-4647-BB8A-263B43F0F974}";
    private const string PROP_CONTAINER_ID = "System.Devices.ContainerId";
    private const string PROP_INTERFACE_ENABLED = "System.Devices.InterfaceEnabled";

    // Bluetooth は "VID&0001004C" (Bluetooth SIG の Apple ID)、USB 接続時は "VID_05AC"
    private static readonly string[] AppleVidMarkers = { "VID&0001004C", "VID&000205AC", "VID_05AC" };
    // Magic Keyboard の PID (動作確認済みは 026C のみ)
    private static readonly string[] MagicKeyboardPids = { "0267", "026C", "029A", "029C", "029F", "0320", "0321", "0322" };

    private enum KeyboardStatus { NotConnected, Unavailable, Ok }

    private readonly record struct KeyboardState(KeyboardStatus Status, byte Level = 0, bool Charging = false);

    // --- 多言語辞書 (Localization.cs) ---
    private static Dictionary<string, Dictionary<string, string>> LocalizedText => Localization.Text;

    private static string T(string key)
    {
        var table = LocalizedText.TryGetValue(_language, out var t) ? t : LocalizedText["en"];
        return table.TryGetValue(key, out var text) ? text : key;
    }

    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // レジストリから保存された設定をロード
        LoadSettings();
        if (_migrateLegacySecret) SaveSettings(); // 平文のペアリングキーを暗号化して保存し直す

        // 多重起動防止 (管理者として起動された別インスタンスの場合はアクセス拒否になる)
        bool createdNew;
        try
        {
            _singleInstanceMutex = new Mutex(true, @"Local\MagicKeyBattery_SingleInstance", out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            createdNew = false;
        }
        if (!createdNew)
        {
            MessageBox.Show(T("already_running"), APP_NAME, MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, Rtl.MessageBoxOptions);
            return;
        }

        _trayIcon = new NotifyIcon();
        _trayIcon.Text = $"Magic Keyboard: {T("checking")}";
        _trayIcon.Icon = SystemIcons.Application;

        BuildContextMenu();

        // ContextMenuStrip の生成で WindowsFormsSynchronizationContext がインストールされる
        _uiContext = SynchronizationContext.Current;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        if (_uiContext != null) DeviceSwitcher.Init(_uiContext, T);
        DeviceSwitcher.ConfigureTv(_tvIp, _tvClientKey, _tvCertPin, SaveTvCredentials);
        ConfigureRemotePcFromSettings();
        ApplyReceiver();

        // クリップボード共有 (別の PC を操作中 / この PC が操作されている時だけ実際に送る)
        ClipboardSync.Enabled = _shareClipboard;
        ClipboardSync.LocalTextChanged += text =>
        {
            DeviceSwitcher.OnLocalClipboardChanged(text);
            RemoteReceiver.BroadcastClipboard(text);
        };
        RemoteReceiver.ClipboardReceived += text => _uiContext?.Post(_ => ClipboardSync.SetFromRemote(text), null);
        ClipboardSync.Start();

        _trayIcon.Visible = true;

        _timer = new System.Windows.Forms.Timer();
        _timer.Interval = _intervalMinutes * 60 * 1000;
        _timer.Tick += async (s, e) => await UpdateBatteryLevelAsync();
        _timer.Start();

        // 接続直後は HID インターフェースの準備に少し時間がかかるため、まとめて 2 秒後に更新
        _debounceTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _debounceTimer.Tick += async (s, e) =>
        {
            _debounceTimer?.Stop();
            await UpdateBatteryLevelAsync();
        };

        _retryTimer = new System.Windows.Forms.Timer { Interval = 30 * 1000 };
        _retryTimer.Tick += async (s, e) =>
        {
            _retryTimer?.Stop();
            await UpdateBatteryLevelAsync();
        };

        StartWatchers();

        // フックのスレッドから: キーボードからキーが届いたのに「未接続」扱いなら、すぐ接続を確認し直す
        KeyboardRemapper.RefreshRequested = () => _uiContext?.Post(async _ => await UpdateBatteryLevelAsync(), null);
        ApplyRemapSettings();

        _ = UpdateBatteryLevelAsync();

        // --history: 起動と同時に履歴グラフを開く (ショートカット用)
        if (Array.Exists(args, a => a.Equals("--history", StringComparison.OrdinalIgnoreCase))) OpenHistory();

        Application.Run();

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
    }

    private static void BuildContextMenu()
    {
        if (_trayIcon == null) return;

        // macOS 風のメニュー (ライト/ダークは Windows の設定に合わせる)
        bool dark = AppleTheme.AppsDark;
        ContextMenuStrip contextMenu = new ContextMenuStrip
        {
            Renderer = new AppleMenuRenderer(dark),
            Font = new Font("Segoe UI", 9.5f),
            ShowImageMargin = true,
            Padding = new Padding(0, 5, 0, 5),
            RightToLeft = Rtl.Enabled ? RightToLeft.Yes : RightToLeft.No // アラビア語
        };
        AppleMenuRenderer.ApplyRoundedCorners(contextMenu);
        int iconSize = contextMenu.LogicalToDeviceUnits(16);
        contextMenu.ImageScalingSize = new Size(iconSize, iconSize);
        Color iconColor = AppleTheme.MenuText(dark);

        ToolStripMenuItem Item(string text, TrayMenuGlyph? glyph, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text, glyph is TrayMenuGlyph gl ? MenuIcons.Render(gl, iconSize, iconColor) : null, onClick)
            {
                Tag = glyph,
                Padding = new Padding(0, 3, 0, 3),
                // 継承だけだとアラビア文字が欠けることがあるので各項目に明示的に設定
                RightToLeft = Rtl.Enabled ? RightToLeft.Yes : RightToLeft.No
            };
            return item;
        }

        string currentText = _batteryMenu?.Text ?? $"Magic Keyboard — {T("checking")}";
        _batteryMenu = Item(currentText, null, async (s, e) => await UpdateBatteryLevelAsync());
        _batteryMenu.Font = new Font(contextMenu.Font, FontStyle.Bold);
        contextMenu.Items.Add(_batteryMenu);

        // シリアル番号 (クリックでコピー)
        _serialMenu = Item("", TrayMenuGlyph.Key, (s, e) => {
            if (_keyboardSerial == null) return;
            try { Clipboard.SetText(_keyboardSerial); } catch { }
            _trayIcon?.ShowBalloonTip(2000, APP_NAME, T("serial_copied"), ToolTipIcon.Info);
        });
        UpdateSerialMenu();
        contextMenu.Items.Add(_serialMenu);
        contextMenu.Items.Add(new ToolStripSeparator());

        // MX Keys の fn ロックに相当: F1〜F12 のメディアキー/通常 F キーをすぐ切り替える (オンの時は ✓)
        _mediaKeysMenu = Item(T("menu_media_keys"), null, (s, e) => {
            _mediaKeys = !_mediaKeys;
            SaveSettings();
            ApplyRemapSettings();
        });
        _mediaKeysMenu.Checked = _mediaKeys;
        _mediaKeysMenu.Visible = _remapEnabled;
        contextMenu.Items.Add(_mediaKeysMenu);
        contextMenu.Items.Add(Item(T("open_keymap"), TrayMenuGlyph.Keyboard, (s, e) => OpenKeyMap()));

        contextMenu.Items.Add(Item(T("open_devices"), TrayMenuGlyph.Display, (s, e) => OpenDevices()));
        contextMenu.Items.Add(Item(T("open_history"), TrayMenuGlyph.Chart, (s, e) => OpenHistory()));
        contextMenu.Items.Add(Item(T("open_settings"), TrayMenuGlyph.Gear, (s, e) => ShowSettingsDialog()));
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(Item(T("exit_app"), TrayMenuGlyph.Power, (s, e) => ExitApplication()));
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem($"{APP_NAME} v{AppVersion}") { Enabled = false });

        // 電池のアイコンをメニューにも表示
        _batteryMenu.Image = BatteryIcon.Render(iconSize, _lastGlyph, _lastLevel, _lastCharging, dark);

        // 言語変更で再構築した場合は古いメニューを破棄
        ContextMenuStrip? oldMenu = _trayIcon.ContextMenuStrip;
        _trayIcon.ContextMenuStrip = contextMenu;
        oldMenu?.Dispose();
    }

    private static void ExitApplication()
    {
        _timer?.Stop();
        _debounceTimer?.Stop();
        _retryTimer?.Stop();
        StopWatchers();
        KeyboardRemapper.Stop();
        DeviceSwitcher.Shutdown();
        RemoteReceiver.Stop();
        ClipboardSync.Stop();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
        }
        _currentIcon?.Dispose();

        Application.Exit();
    }

    // --- デバイス識別 ---

    private static bool IsMagicKeyboardInterface(string interfaceId)
    {
        bool isApple = false;
        foreach (string vid in AppleVidMarkers)
        {
            if (interfaceId.Contains(vid, StringComparison.OrdinalIgnoreCase)) { isApple = true; break; }
        }
        if (!isApple) return false;

        foreach (string pid in MagicKeyboardPids)
        {
            if (interfaceId.Contains($"PID&{pid}", StringComparison.OrdinalIgnoreCase) ||
                interfaceId.Contains($"PID_{pid}", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // USB (Lightning / USB-C ケーブル) 経由で見えている = 充電中
    private static bool IsUsbInterface(string interfaceId) => interfaceId.Contains("VID_05AC", StringComparison.OrdinalIgnoreCase);

    private static bool IsInterfaceEnabled(DeviceInformation info)
    {
        return info.Properties.TryGetValue(PROP_INTERFACE_ENABLED, out object? value) && value is bool enabled ? enabled : info.IsEnabled;
    }

    // --- 残量更新 ---

    private static async Task UpdateBatteryLevelAsync()
    {
        // タイマー/メニュー/設定保存/ウォッチャーから同時に呼ばれても 1 本だけ実行し、要求があれば最後にもう一度実行
        if (_updating)
        {
            _updatePending = true;
            return;
        }

        _updating = true;
        try
        {
            do
            {
                _updatePending = false;
                KeyboardState state;
                try
                {
                    state = await ReadKeyboardStateAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ReadKeyboardStateAsync failed: {ex}");
                    state = new KeyboardState(KeyboardStatus.Unavailable);
                }
                UpdateUI(state);
            }
            while (_updatePending);
        }
        finally
        {
            _updating = false;
        }
    }

    private static async Task<KeyboardState> ReadKeyboardStateAsync()
    {
        // 1. ペアリング済みの Magic Keyboard の HID インターフェースを VID/PID で探す (名前変更や OS 言語に依存しない)
        string hidAqs = $"System.Devices.InterfaceClassGuid:=\"{HID_INTERFACE_CLASS}\"";
        var hidInterfaces = await DeviceInformation.FindAllAsync(hidAqs, new[] { PROP_CONTAINER_ID, PROP_INTERFACE_ENABLED }, DeviceInformationKind.DeviceInterface);

        var keyboardInterfaces = new List<DeviceInformation>();
        foreach (var iface in hidInterfaces)
        {
            if (IsMagicKeyboardInterface(iface.Id)) keyboardInterfaces.Add(iface);
        }

        if (keyboardInterfaces.Count == 0) return new KeyboardState(KeyboardStatus.NotConnected);

        bool charging = keyboardInterfaces.Exists(i => IsUsbInterface(i.Id) && IsInterfaceEnabled(i));

        // 2. 接続状態: 切断されると Windows は HID インターフェースを無効化する
        //    (AssociationEndpoint の列挙は Bluetooth 探索が走り 30〜60 秒かかるため使わない)
        bool connected = keyboardInterfaces.Exists(IsInterfaceEnabled);
        if (!connected) return new KeyboardState(KeyboardStatus.NotConnected);

        // 3. コレクション番号 (Col02 等) はドライバーによって変わるため、有効な全 HID インターフェースを試す
        foreach (var iface in keyboardInterfaces)
        {
            if (!IsInterfaceEnabled(iface)) continue;
            _keyboardSerial ??= TryReadSerial(iface.Id);

            byte? level = TryReadBatteryLevel(iface.Id);
            if (level != null) return new KeyboardState(KeyboardStatus.Ok, level.Value, charging);
        }

        // 4. 接続済みだが読み取りに失敗 (スリープ復帰・再接続直後は一時的に ERROR_INVALID_PARAMETER になることがある)
        return new KeyboardState(KeyboardStatus.Unavailable, 0, charging);
    }

    // キーボードが報告するシリアル番号 (Magic Keyboard は Bluetooth アドレスを返す)
    private static string? TryReadSerial(string devicePath)
    {
        IntPtr handle = CreateFile(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == INVALID_HANDLE_VALUE) return null;
        try
        {
            byte[] buffer = new byte[256];
            if (!HidD_GetSerialNumberString(handle, buffer, (uint)buffer.Length)) return null;
            string serial = System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0').ToUpperInvariant();
            return serial.Length == 0 ? null : serial;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // 12 桁の Bluetooth アドレスなら 80:4A:14:... の形で表示
    private static string FormatSerial(string serial) =>
        serial.Length == 12 && System.Text.RegularExpressions.Regex.IsMatch(serial, "^[0-9A-F]{12}$")
            ? string.Join(":", Enumerable.Range(0, 6).Select(i => serial.Substring(i * 2, 2)))
            : serial;

    private static byte? TryReadBatteryLevel(string devicePath)
    {
        IntPtr handle = CreateFile(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == INVALID_HANDLE_VALUE) return null;

        try
        {
            // バッファ長はコレクションの InputReportByteLength に合わせる必要がある
            int length = 8;
            if (HidD_GetPreparsedData(handle, out IntPtr preparsed))
            {
                byte[] caps = new byte[64];
                if (HidP_GetCaps(preparsed, caps) == 0x00110000) // HIDP_STATUS_SUCCESS
                {
                    int inputLength = BitConverter.ToUInt16(caps, 4);
                    if (inputLength >= 3) length = inputLength;
                }
                HidD_FreePreparsedData(preparsed);
            }

            byte[] buffer = new byte[length];
            buffer[0] = BATTERY_REPORT_ID;
            if (!HidD_GetInputReport(handle, buffer, (uint)buffer.Length)) return null;
            if (buffer[0] != BATTERY_REPORT_ID) return null;

            byte batteryLevel = buffer[2];
            return batteryLevel > 0 && batteryLevel <= 100 ? batteryLevel : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // --- 接続/切断のリアルタイム検知 ---

    private static void StartWatchers()
    {
        try
        {
            // HID インターフェースの追加/削除/有効化の変化 = Bluetooth 接続/切断・USB ケーブルの抜き差し
            // (AssociationEndpoint のウォッチャーは Bluetooth 探索を走らせ続けるため使わない)
            string hidAqs = $"System.Devices.InterfaceClassGuid:=\"{HID_INTERFACE_CLASS}\"";
            var enumerationDone = false;
            _hidWatcher = DeviceInformation.CreateWatcher(hidAqs, new[] { PROP_INTERFACE_ENABLED }, DeviceInformationKind.DeviceInterface);
            _hidWatcher.Added += (w, info) =>
            {
                if (enumerationDone && IsMagicKeyboardInterface(info.Id)) ScheduleRefresh();
            };
            _hidWatcher.Updated += (w, update) =>
            {
                if (IsMagicKeyboardInterface(update.Id)) ScheduleRefresh();
            };
            _hidWatcher.Removed += (w, update) =>
            {
                if (IsMagicKeyboardInterface(update.Id)) ScheduleRefresh();
            };
            _hidWatcher.EnumerationCompleted += (w, o) => enumerationDone = true;
            _hidWatcher.Start();
        }
        catch (Exception ex)
        {
            // ウォッチャーが使えなくても定期更新で動作は継続
            System.Diagnostics.Debug.WriteLine($"StartWatchers failed: {ex}");
        }
    }

    private static void StopWatchers()
    {
        foreach (var watcher in new[] { _hidWatcher })
        {
            try
            {
                if (watcher != null && (watcher.Status == DeviceWatcherStatus.Started || watcher.Status == DeviceWatcherStatus.EnumerationCompleted))
                    watcher.Stop();
            }
            catch { }
        }
    }

    // ウォッチャーのイベントはスレッドプールから来るため UI スレッドに戻してからデバウンス
    private static void ScheduleRefresh()
    {
        _uiContext?.Post(_ =>
        {
            if (_debounceTimer == null) return;
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }, null);
    }

    // --- UI ---

    private static void UpdateUI(KeyboardState state)
    {
        if (_trayIcon == null || _batteryMenu == null) return;

        // キー配置の変換は Magic Keyboard が接続されている間だけ (他のキーボードには影響させない)
        bool connected = state.Status != KeyboardStatus.NotConnected;
        if (KeyboardRemapper.KeyboardConnected && !connected) KeyboardRemapper.ReleaseHeldKeys();
        KeyboardRemapper.KeyboardConnected = connected;
        UpdateSerialMenu();

        if (state.Status == KeyboardStatus.NotConnected)
        {
            _trayIcon.Text = $"Magic Keyboard: {T("not_connected")}";
            _batteryMenu.Text = $"Magic Keyboard — {T("click_retry")}";
            SetDynamicIcon(0, false);
            _hasNotifiedLowBattery = false;

            // Windows 起動直後はキーボードの Bluetooth 再接続が遅れることがある → 最初の 3 分間は 30 秒ごとに確認
            if (Environment.TickCount64 - StartedTick < 3 * 60 * 1000)
            {
                _retryTimer?.Stop();
                _retryTimer?.Start();
            }
            return;
        }

        if (state.Status == KeyboardStatus.Unavailable)
        {
            string chargingSuffix = state.Charging ? $" ⚡{T("charging")}" : "";
            SetTrayText($"Magic Keyboard: {T("unavailable")}{chargingSuffix}");
            _batteryMenu.Text = $"Magic Keyboard — {T("unavailable_retry")}";
            SetDynamicIcon(0, true, unknown: true, charging: state.Charging);

            // 一時的な失敗が多いため、定期更新を待たずに 30 秒後に再試行
            _retryTimer?.Stop();
            _retryTimer?.Start();
            return;
        }

        byte level = state.Level;
        string chargingText = state.Charging ? $" ⚡{T("charging")}" : "";
        SetTrayText($"Magic Keyboard: {level}%{chargingText}");
        _batteryMenu.Text = $"Magic Keyboard — {level}%{(state.Charging ? $"  ·  {T("charging")}" : "")}";
        _batteryMenu.ToolTipText = T("click_refresh");

        SetDynamicIcon(level, true, charging: state.Charging);
        LogHistory(level, state.Charging);
        CheckBatteryNotifications(level, state.Charging);
    }

    // バッテリーの通知 (Windows 10/11 ではバルーンチップはトースト通知として表示される)
    //   ・低残量 (初期値 20%) と緊急 (初期値 10%): 放電中に 1 回ずつ。充電するか残量が戻ったら再び通知できる
    //   ・満充電: 100% 未満 → 100% になった時に 1 回 (ケーブルで電池を傷めないように)
    //     充電の検出は USB 接続か「残量が増えた」ことで判断 (充電器につないでいる場合も分かる)
    private static void CheckBatteryNotifications(byte level, bool charging)
    {
        if (_trayIcon == null) return;

        bool rising = _previousLevel > 0 && level > _previousLevel;
        bool chargingNow = charging || rising;

        // 緊急の警告 (低残量より優先)
        if (_criticalThreshold > 0 && level <= _criticalThreshold && !chargingNow)
        {
            if (!_hasNotifiedCritical)
            {
                _trayIcon.ShowBalloonTip(8000, T("notify_critical_title"), string.Format(T("notify_critical_body"), level) + TimeLeftText(level), ToolTipIcon.Error);
                _hasNotifiedCritical = true;
                _hasNotifiedLowBattery = true; // 低残量の通知はもう不要
            }
        }
        else if (_notifyThreshold > 0 && level <= _notifyThreshold && !chargingNow)
        {
            if (!_hasNotifiedLowBattery)
            {
                _trayIcon.ShowBalloonTip(5000, T("notify_title"), string.Format(T("notify_body"), _notifyThreshold, level) + TimeLeftText(level), ToolTipIcon.Warning);
                _hasNotifiedLowBattery = true;
            }
        }

        // 充電したら (または残量が基準より上に戻ったら) 次の放電でまた通知できるように
        if (chargingNow || level > _notifyThreshold) _hasNotifiedLowBattery = false;
        if (chargingNow || level > _criticalThreshold) _hasNotifiedCritical = false;

        // 満充電
        if (_notifyFull && level >= 100 && !_hasNotifiedFull && (_previousLevel is > 0 and < 100 || charging))
        {
            _trayIcon.ShowBalloonTip(5000, T("notify_full_title"), T("notify_full_body"), ToolTipIcon.Info);
            _hasNotifiedFull = true;
        }
        if (level < 95) _hasNotifiedFull = false;

        _previousLevel = level;
    }

    // 「残り約 N 日」(履歴から平均消費を計算。データ不足なら何も付けない)
    private static string TimeLeftText(byte level)
    {
        try
        {
            double perDay = HistoryForm.DrainPerDay(HistoryForm.LoadHistory(HistoryFile), DateTime.Now.AddDays(-14));
            if (perDay <= 0) return "";
            double days = level / perDay;
            string time = days >= 1
                ? string.Format(CultureInfo.CurrentCulture, T("time_days"), days)
                : string.Format(CultureInfo.CurrentCulture, T("time_hours"), days * 24);
            return string.Format(T("notify_time_left"), time);
        }
        catch
        {
            return "";
        }
    }

    // NotifyIcon.Text は 127 文字まで
    private static void SetTrayText(string text)
    {
        if (_trayIcon == null) return;
        _trayIcon.Text = text.Length > 127 ? text[..127] : text;
    }

    // 最後に描いた状態 (ライト/ダークの切り替え時に描き直すため)
    private static BatteryGlyph _lastGlyph = BatteryGlyph.Unknown;
    private static byte _lastLevel;
    private static bool _lastCharging;

    private static void SetDynamicIcon(byte level, bool connected, bool unknown = false, bool charging = false)
    {
        if (_trayIcon == null) return;

        _lastGlyph = unknown ? BatteryGlyph.Unknown : connected ? BatteryGlyph.Level : BatteryGlyph.NotConnected;
        _lastLevel = level;
        _lastCharging = charging;

        // macOS のメニューバーと同じ形の電池。タスクバーがダークなら白、ライトなら黒
        // 高 DPI 環境でぼやけないよう、トレイの実サイズで描画
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using Bitmap bmp = BatteryIcon.Render(size, _lastGlyph, level, charging, AppleTheme.TaskbarDark);
        Icon newIcon = BatteryIcon.ToIcon(bmp);

        _trayIcon.Icon = newIcon;
        _currentIcon?.Dispose();
        _currentIcon = newIcon;

        // メニューの先頭にも同じ電池を小さく表示
        if (_batteryMenu != null)
        {
            int menuSize = _batteryMenu.Owner?.LogicalToDeviceUnits(16) ?? 16;
            Image? old = _batteryMenu.Image;
            _batteryMenu.Image = BatteryIcon.Render(menuSize, _lastGlyph, level, charging, AppleTheme.AppsDark);
            old?.Dispose();
        }
    }

    // Windows のライト/ダーク切り替えに追従
    private static void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
        _uiContext?.Post(_ =>
        {
            BuildContextMenu();
            SetDynamicIcon(_lastLevel, _lastGlyph != BatteryGlyph.NotConnected, _lastGlyph == BatteryGlyph.Unknown, _lastCharging);
        }, null);
    }

    // --- バッテリー履歴 ---

    private static void LogHistory(byte level, bool charging)
    {
        if (level == _lastLoggedLevel && charging == _lastLoggedCharging) return;

        try
        {
            Directory.CreateDirectory(HistoryDir);

            // ファイルが大きくなりすぎたら 1 世代だけ退避
            var info = new FileInfo(HistoryFile);
            if (info.Exists && info.Length > HISTORY_MAX_BYTES)
            {
                File.Move(HistoryFile, Path.ChangeExtension(HistoryFile, ".old.csv"), true);
            }

            bool writeHeader = !File.Exists(HistoryFile);
            using (var writer = new StreamWriter(HistoryFile, append: true))
            {
                if (writeHeader) writer.WriteLine("Timestamp,Level,Charging");
                writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{level},{(charging ? 1 : 0)}");
            }

            _lastLoggedLevel = level;
            _lastLoggedCharging = charging;

            // 履歴ウィンドウが開いていればグラフを更新
            _historyForm?.ReloadData();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LogHistory failed: {ex}");
        }
    }

    // 履歴グラフのウィンドウ (開いていれば前面に出すだけ)
    private static void OpenHistory()
    {
        if (_historyForm != null && !_historyForm.IsDisposed)
        {
            if (_historyForm.WindowState == FormWindowState.Minimized) _historyForm.WindowState = FormWindowState.Normal;
            _historyForm.Activate();
            return;
        }

        _historyForm = new HistoryForm(T, HistoryFile, _notifyThreshold);
        _historyForm.FormClosed += (s, e) =>
        {
            _historyForm?.Dispose();
            _historyForm = null;
        };
        _historyForm.Show();
    }

    // --- 設定 ---

    private static void ShowSettingsDialog()
    {
        Color labelColor = Color.LightGray;
        Color inputBack = Color.FromArgb(50, 50, 50);

        using Form configForm = new Form
        {
            ClientSize = new Size(520, 570),
            Text = T("dialog_title"),
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f),
            // 96 DPI (100%) で設計したレイアウトを画面の拡大率に合わせて拡大する
            AutoScaleDimensions = new SizeF(96F, 96F),
            AutoScaleMode = AutoScaleMode.Dpi,
            ShowIcon = false
        };
        using Font hintFont = new Font("Segoe UI", 8f);
        using Font headerFont = new Font("Segoe UI Semibold", 10f);

        Label Header(string text, int top) => new Label { Text = text, Left = 16, Top = top, AutoSize = true, Font = headerFont, ForeColor = Color.White };
        Label Caption(string text, int top) => new Label { Text = text, Left = 28, Top = top + 2, Width = 300, Height = 24, ForeColor = labelColor };
        NumericUpDown Number(int top, int min, int max, int value) =>
            new NumericUpDown { Left = 410, Top = top, Width = 90, Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), BackColor = inputBack, ForeColor = Color.White };
        CheckBox Check(string text, int left, int top, bool value) =>
            new CheckBox { Text = text, Left = left, Top = top, Width = 520 - left - 12, Height = 26, Checked = value, ForeColor = labelColor };

        // --- バッテリー ---
        var hdrBattery = Header("Magic Keyboard", 12);
        var lblInterval = Caption(T("lbl_interval"), 42);
        var numInterval = Number(42, 1, 60, _intervalMinutes);
        var lblThreshold = Caption(T("lbl_threshold"), 72);
        var numThreshold = Number(72, 0, 100, _notifyThreshold);
        var lblCritical = Caption(T("lbl_critical"), 102);
        var numCritical = Number(102, 0, 100, _criticalThreshold);
        var lblHint = new Label { Text = T("lbl_hint"), Left = 28, Top = 130, Width = 472, Height = 20, Font = hintFont, ForeColor = Color.Gray };
        var chkFull = Check(T("chk_notify_full"), 28, 152, _notifyFull);

        // --- 全般 ---
        var hdrGeneral = Header("MagicKeyBattery", 190);
        var lblLang = Caption(T("lbl_language"), 220);
        var cmbLang = new ComboBox { Left = 360, Top = 220, Width = 140, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = inputBack, ForeColor = Color.White };
        foreach ((string code, string name) in Localization.Languages) cmbLang.Items.Add(name);
        cmbLang.SelectedIndex = Math.Max(0, Array.FindIndex(Localization.Languages, l => l.Code == _language));
        var chkStartup = Check(T("chk_startup"), 28, 252, IsStartupEnabled());

        // --- キー配置 ---
        var hdrKeyboard = Header(T("lbl_keyboard"), 290);
        var chkRemap = Check(T("chk_remap"), 28, 320, _remapEnabled);
        var chkMedia = Check(T("chk_media_keys"), 46, 346, _mediaKeys);
        var chkSwapIso = Check(T("chk_swap_iso"), 46, 372, _swapIsoKeys);
        var btnKeys = new Button { Text = T("btn_customize_keys"), Left = 46, Top = 402, Width = 200, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = inputBack, ForeColor = Color.White };
        btnKeys.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
        btnKeys.Click += (s, e) => OpenKeyMap();
        void UpdateRemapControls() => chkMedia.Enabled = chkSwapIso.Enabled = btnKeys.Enabled = chkRemap.Checked;
        chkRemap.CheckedChanged += (s, e) => UpdateRemapControls();
        UpdateRemapControls();

        // --- デバイス ---
        var chkSwitching = Check(T("chk_device_switching"), 28, 440, _deviceSwitching);
        var chkClipboard = Check(T("chk_share_clipboard"), 28, 466, _shareClipboard);

        var btnSave = new Button { Text = T("btn_save"), Left = 400, Top = 522, Width = 100, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White };
        btnSave.FlatAppearance.BorderSize = 0;
        var btnCancel = new Button { Text = T("btn_cancel"), Left = 290, Top = 522, Width = 100, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = inputBack, ForeColor = Color.White };
        btnCancel.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
        btnCancel.Click += (s, e) => configForm.Close();

        btnSave.Click += async (s, e) =>
        {
            _intervalMinutes = (int)numInterval.Value;
            _notifyThreshold = (int)numThreshold.Value;
            _criticalThreshold = (int)numCritical.Value;
            _notifyFull = chkFull.Checked;
            _language = Localization.Languages[Math.Max(0, cmbLang.SelectedIndex)].Code;
            Rtl.Enabled = Localization.IsRtl(_language);
            _remapEnabled = chkRemap.Checked;
            _mediaKeys = chkMedia.Checked;
            _swapIsoKeys = chkSwapIso.Checked;
            _deviceSwitching = chkSwitching.Checked;
            _shareClipboard = chkClipboard.Checked;
            ClipboardSync.Enabled = _shareClipboard;

            SaveSettings();
            ToggleStartup(chkStartup.Checked);
            ApplyRemapSettings();

            if (_timer != null) _timer.Interval = _intervalMinutes * 60 * 1000;

            BuildContextMenu();
            configForm.Close();

            await UpdateBatteryLevelAsync();
        };

        configForm.AcceptButton = btnSave;
        configForm.CancelButton = btnCancel;
        configForm.Controls.AddRange(new Control[]
        {
            hdrBattery, lblInterval, numInterval, lblThreshold, numThreshold, lblCritical, numCritical, lblHint, chkFull,
            hdrGeneral, lblLang, cmbLang, chkStartup,
            hdrKeyboard, chkRemap, chkMedia, chkSwapIso, btnKeys,
            chkSwitching, chkClipboard,
            btnCancel, btnSave
        });
        Rtl.Apply(configForm); // アラビア語: 左右反転
        configForm.ShowDialog();
    }

    // 「キー配置」画面 (開いていれば前面に出すだけ)
    private static void OpenKeyMap()
    {
        if (_keyMapForm != null && !_keyMapForm.IsDisposed)
        {
            _keyMapForm.Activate();
            return;
        }

        _keyMapForm = new KeyMapForm(T, _keyMap, map =>
        {
            _keyMap = map;
            SaveSettings();
            KeyboardRemapper.SetMapping(_keyMap);
        });
        _keyMapForm.FormClosed += (s, e) =>
        {
            _keyMapForm?.Dispose();
            _keyMapForm = null;
        };
        _keyMapForm.Show();
    }
    private static void SaveSettings()
    {
        // exe の隣の MagicKeyBattery-data\settings.json (AppStorage.cs)
        SettingsStore.Set("IntervalMinutes", _intervalMinutes);
        SettingsStore.Set("NotifyThreshold", _notifyThreshold);
        SettingsStore.Set("Language", _language);
        SettingsStore.Set("RemapEnabled", _remapEnabled);
        SettingsStore.Set("MediaKeys", _mediaKeys);
        SettingsStore.Set("SwapIsoKeys", _swapIsoKeys);
        SettingsStore.Set("DeviceSwitching", _deviceSwitching);
        SettingsStore.Set("TvIp", _tvIp);
        SettingsStore.Set("TvClientKeyProtected", ProtectSecret(_tvClientKey));
        SettingsStore.Set("TvCertPin", _tvCertPin);
        SettingsStore.Set("F15IsPc", _f15IsPc);
        SettingsStore.Set("RemotePcHost", _remotePcHost);
        SettingsStore.Set("RemotePcTokenProtected", ProtectSecret(_remotePcToken));
        SettingsStore.Set("RemotePcCertPin", _remotePcCertPin);
        SettingsStore.Set("ReceiverEnabled", _receiverEnabled);
        SettingsStore.Set("CriticalThreshold", _criticalThreshold);
        SettingsStore.Set("NotifyFull", _notifyFull);
        SettingsStore.Set("ShareClipboard", _shareClipboard);
        SettingsStore.Set("KeyMap", KeyMapping.Serialize(_keyMap));
        SettingsStore.Remove("TvClientKey"); // 旧バージョンの平文の値は保存しない
        SettingsStore.Save();
    }

    private static void UpdateSerialMenu()
    {
        if (_serialMenu == null) return;
        _serialMenu.Visible = _keyboardSerial != null;
        if (_keyboardSerial != null) _serialMenu.Text = $"{T("serial_label")}: {FormatSerial(_keyboardSerial)}";
    }

    private static void OpenDevices()
    {
        if (_devicesForm != null && !_devicesForm.IsDisposed)
        {
            _devicesForm.Activate();
            return;
        }

        _devicesForm = new DevicesForm(T, new DevicesSettings
        {
            TvIp = _tvIp,
            SaveTvIp = ip =>
            {
                // IP が変わったらペアリングキーと証明書のピンは無効
                if (ip != _tvIp)
                {
                    _tvClientKey = "";
                    _tvCertPin = "";
                }
                _tvIp = ip;
                SaveSettings();
                DeviceSwitcher.ConfigureTv(_tvIp, _tvClientKey, _tvCertPin, SaveTvCredentials);
            },
            F15IsPc = _f15IsPc,
            SaveF15IsPc = isPc =>
            {
                _f15IsPc = isPc;
                SaveSettings();
                DeviceSwitcher.F15Mode = _f15IsPc ? DeviceMode.RemotePc : DeviceMode.Phone;
            },
            RemotePcHost = _remotePcToken.Length > 0 ? _remotePcHost : "",
            SaveRemotePc = (host, token, pin) =>
            {
                _remotePcHost = host;
                _remotePcToken = Convert.ToBase64String(token);
                _remotePcCertPin = pin;
                SaveSettings();
                DeviceSwitcher.ConfigureRemotePc(_remotePcHost, token, _remotePcCertPin);
            },
            ReceiverEnabled = _receiverEnabled,
            SetReceiverEnabled = enabled =>
            {
                _receiverEnabled = enabled;
                SaveSettings();
                return ApplyReceiver();
            }
        });
        _devicesForm.FormClosed += (s, e) =>
        {
            _devicesForm?.Dispose();
            _devicesForm = null;
        };
        _devicesForm.Show();
    }

    // この PC を別の PC から操作できるようにする (受信)
    private static string? ApplyReceiver()
    {
        if (!_receiverEnabled)
        {
            RemoteReceiver.Stop();
            return null;
        }
        return RemoteReceiver.Start();
    }

    private static void ConfigureRemotePcFromSettings()
    {
        DeviceSwitcher.F15Mode = _f15IsPc ? DeviceMode.RemotePc : DeviceMode.Phone;
        byte[]? token = null;
        try { if (_remotePcToken.Length > 0) token = Convert.FromBase64String(_remotePcToken); } catch { }
        DeviceSwitcher.ConfigureRemotePc(_remotePcHost, token, _remotePcCertPin);
    }

    // TV が承認したペアリングキーと証明書のピン (TV 側のスレッドから呼ばれる)
    private static void SaveTvCredentials(string key, string? certPin)
    {
        _tvClientKey = key;
        _tvCertPin = certPin ?? "";
        SaveSettings();
    }

    // ペアリングキーは Windows の DPAPI (現在のユーザー専用) で暗号化してレジストリに保存
    private static readonly byte[] DpapiEntropy = System.Text.Encoding.UTF8.GetBytes("MagicKeyBattery.TvClientKey.v1");

    private static string ProtectSecret(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        byte[] data = System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(plain), DpapiEntropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    private static string UnprotectSecret(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            byte[] data = System.Security.Cryptography.ProtectedData.Unprotect(
                Convert.FromBase64String(stored), DpapiEntropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(data);
        }
        catch
        {
            return ""; // 別ユーザー・別 PC からコピーされた値は使えない → 再ペアリング
        }
    }

    private static void ApplyRemapSettings()
    {
        KeyboardRemapper.RemapEnabled = _remapEnabled;
        KeyboardRemapper.SwitchingEnabled = _deviceSwitching;
        KeyboardRemapper.MediaKeys = _mediaKeys;
        KeyboardRemapper.SwapIsoKeys = _swapIsoKeys;
        KeyboardRemapper.SetMapping(_keyMap);

        if (_remapEnabled || _deviceSwitching) KeyboardRemapper.Start();
        else KeyboardRemapper.Stop();

        if (!_deviceSwitching && DeviceSwitcher.Mode != DeviceMode.Pc) DeviceSwitcher.RequestSwitch(DeviceMode.Pc);

        if (_mediaKeysMenu != null)
        {
            _mediaKeysMenu.Checked = _mediaKeys;
            _mediaKeysMenu.Visible = _remapEnabled;
        }
    }

    private static void LoadSettings()
    {
        // exe の隣の MagicKeyBattery-data\settings.json (初回は旧バージョンのレジストリから自動で移行)
        _intervalMinutes = Math.Clamp(SettingsStore.GetInt("IntervalMinutes", 3), 1, 60);
        _notifyThreshold = Math.Clamp(SettingsStore.GetInt("NotifyThreshold", 20), 0, 100);
        _language = SettingsStore.GetString("Language");
        _remapEnabled = SettingsStore.GetBool("RemapEnabled", false);
        _mediaKeys = SettingsStore.GetBool("MediaKeys", true);
        _swapIsoKeys = SettingsStore.GetBool("SwapIsoKeys", false);
        _deviceSwitching = SettingsStore.GetBool("DeviceSwitching", true);
        _tvIp = SettingsStore.GetString("TvIp");
        _tvClientKey = UnprotectSecret(SettingsStore.GetString("TvClientKeyProtected"));
        _tvCertPin = SettingsStore.GetString("TvCertPin");
        _f15IsPc = SettingsStore.GetBool("F15IsPc", false);
        _remotePcHost = SettingsStore.GetString("RemotePcHost");
        _remotePcToken = UnprotectSecret(SettingsStore.GetString("RemotePcTokenProtected"));
        _remotePcCertPin = SettingsStore.GetString("RemotePcCertPin");
        _receiverEnabled = SettingsStore.GetBool("ReceiverEnabled", false);
        _criticalThreshold = Math.Clamp(SettingsStore.GetInt("CriticalThreshold", 10), 0, 100);
        _notifyFull = SettingsStore.GetBool("NotifyFull", true);
        _shareClipboard = SettingsStore.GetBool("ShareClipboard", true);
        _keyMap = KeyMapping.Parse(SettingsStore.GetString("KeyMap"));

        // 旧バージョンの平文キーがあれば暗号化して保存し直す
        string legacyKey = SettingsStore.GetString("TvClientKey");
        if (_tvClientKey.Length == 0 && legacyKey.Length > 0)
        {
            _tvClientKey = legacyKey;
            _migrateLegacySecret = true;
        }

        // 未設定 (初回起動) または不明な値の場合は OS のロケールから判定
        if (!LocalizedText.ContainsKey(_language))
        {
            string currentCulture = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
            _language = currentCulture.StartsWith("ja") ? "ja" : currentCulture.StartsWith("ar") ? "ar" : "en";
        }
        Rtl.Enabled = Localization.IsRtl(_language);
    }

    private static void ToggleStartup(bool enable)
    {
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(REG_RUN_KEY, true))
            {
                if (key == null) return;
                if (enable) key.SetValue(APP_NAME, $"\"{Application.ExecutablePath}\"");
                else key.DeleteValue(APP_NAME, false);
            }
        }
        catch { }
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(REG_RUN_KEY, false))
            {
                return key != null && key.GetValue(APP_NAME) != null;
            }
        }
        catch { return false; }
    }
}
