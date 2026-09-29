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
    private static readonly string HistoryDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagicKeyBattery");
    private static readonly string HistoryFile = Path.Combine(HistoryDir, "history.csv");
    private const long HISTORY_MAX_BYTES = 512 * 1024;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    private const string REG_RUN_KEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string REG_APP_KEY = @"SOFTWARE\MagicKeyBattery\Settings";
    private const string APP_NAME = "MagicKeyBattery";

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

    // --- 多言語辞書 ---
    private static readonly Dictionary<string, Dictionary<string, string>> LocalizedText = new()
    {
        ["en"] = new()
        {
            ["checking"] = "Checking connection...",
            ["not_connected"] = "Not Connected",
            ["click_retry"] = "Not Connected (Click to retry)",
            ["click_refresh"] = "Click to refresh now",
            ["charging"] = "Charging",
            ["unavailable"] = "Battery unavailable",
            ["unavailable_retry"] = "Battery unavailable (Click to retry)",
            ["open_history"] = "Battery History...",
            ["no_history"] = "No battery history has been recorded yet.",
            ["history_title"] = "Magic Keyboard Battery History",
            ["range_24h"] = "24 hours",
            ["range_7d"] = "7 days",
            ["range_30d"] = "30 days",
            ["range_all"] = "All",
            ["stat_current"] = "Current level",
            ["stat_drain"] = "Average drain",
            ["stat_drain_value"] = "{0:0.#}% / day",
            ["stat_remaining"] = "Estimated time left",
            ["stat_days_value"] = "~{0:0.#} days",
            ["stat_hours_value"] = "~{0:0} hours",
            ["stat_not_enough"] = "Not enough data",
            ["stat_last_charge"] = "Last charged",
            ["ago_days"] = "{0} days ago",
            ["ago_hours"] = "{0} hours ago",
            ["ago_minutes"] = "{0} min ago",
            ["chart_battery"] = "Battery level",
            ["chart_threshold"] = "Alert {0}%",
            ["chart_threshold_legend"] = "Low battery alert",
            ["btn_open_csv"] = "Open CSV",
            ["btn_refresh"] = "Refresh",
            ["open_settings"] = "Settings...",
            ["exit_app"] = "Quit MagicKeyBattery",
            ["already_running"] = "MagicKeyBattery is already running in the system tray.",
            ["dialog_title"] = "MagicKeyBattery Settings",
            ["lbl_interval"] = "Update Interval (Min):",
            ["lbl_threshold"] = "Low Battery Notification (%):",
            ["lbl_hint"] = "*Set to 0 to disable notifications",
            ["chk_startup"] = "Run at Windows Startup",
            ["lbl_keyboard"] = "Keyboard layout (like Logitech MX Keys)",
            ["chk_remap"] = "Use MX Keys layout (Ctrl | Win | Alt ... AltGr | Ctrl)",
            ["chk_media_keys"] = "F1–F12 as media keys (hold Ctrl/Alt/Shift/Win for F-keys)",
            ["chk_swap_iso"] = "Swap ^ and < keys (German Apple keyboard)",
            ["menu_media_keys"] = "F1–F12 as Media Keys",
            ["open_devices"] = "Devices (F13 PC · F14 TV · F15 Phone/PC)...",
            ["serial_label"] = "Serial",
            ["serial_copied"] = "Serial number copied to the clipboard.",
            ["chk_device_switching"] = "F13 / F14 / F15 switch typing to PC / TV / Phone",
            ["mode_pc"] = "PC",
            ["mode_tv"] = "Typing on TV",
            ["mode_phone"] = "Typing on phone",
            ["tv_not_configured"] = "TV not set up (tray → Devices)",
            ["tv_unreachable"] = "TV not reachable — back to PC",
            ["mode_remote"] = "Typing on",
            ["remote_connecting"] = "Connecting to the other PC...",
            ["remote_not_configured"] = "Other PC not paired (tray → Devices)",
            ["remote_disconnected"] = "Other PC disconnected — back to this PC",
            ["devices_f15"] = "F15 connects to",
            ["devices_f15_phone"] = "📱 Android phone",
            ["devices_f15_pc"] = "🖥 Another PC",
            ["devices_pc_hint"] = "On the other PC: run MagicKeyBattery → tray → Devices → turn on \"Allow this PC to be controlled\" → \"Show pairing code\". Enter that PC's address and the code here (once). Both PCs must be on the same network.",
            ["devices_pc_address"] = "PC address:",
            ["devices_f15_note"] = "Connections to the other PC are encrypted (TLS) and locked to that PC's certificate after pairing. Only computers on your local network are accepted. Windows may ask to allow MagicKeyBattery through the firewall on the receiving PC — allow it for Private networks only.",
            ["devices_receiver"] = "🖥 This PC as a receiver",
            ["devices_receiver_enable"] = "Allow this PC to be controlled from another PC's keyboard",
            ["devices_receiver_address"] = "This PC's address",
            ["devices_receiver_code"] = "Show pairing code",
            ["devices_receiver_code_hint"] = "Enter this code on the other PC within 2 minutes",
            ["devices_receiver_forget"] = "Remove paired PCs",
            ["devices_receiver_forgotten"] = "All paired PCs removed",
            ["devices_receiver_paired"] = "Paired",
            ["phone_not_installed"] = "scrcpy is not installed",
            ["phone_connecting"] = "Connecting to phone...",
            ["devices_title"] = "Devices",
            ["devices_tv"] = "📺 TV (F14) — LG webOS",
            ["devices_tv_ip"] = "TV IP address:",
            ["devices_find"] = "Find",
            ["devices_pair"] = "Pair",
            ["devices_tv_hint"] = "The TV must be on. On the first pairing, accept the prompt on the TV with the remote. If no prompt appears, turn on \"LG Connect Apps\" in the TV network settings.",
            ["devices_searching"] = "Searching...",
            ["devices_not_found"] = "Not found",
            ["devices_tv_accept"] = "Accept the prompt on the TV...",
            ["devices_paired"] = "Paired",
            ["devices_phone"] = "📱 Phone (F15) — Android",
            ["devices_phone_hint"] = "On the phone: Settings → Developer options → Wireless debugging → ON → \"Pair device with pairing code\". Enter the IP:port and code it shows (once). The phone and PC must be on the same Wi-Fi.",
            ["devices_pair_address"] = "Pairing IP:port:",
            ["devices_pair_code"] = "Pairing code:",
            ["devices_test"] = "Test",
            ["devices_pairing"] = "Pairing...",
            ["devices_phone_found"] = "Phone connected",
            ["devices_close"] = "Close",
            ["btn_save"] = "Save",
            ["notify_title"] = "Low Battery Warning",
            ["notify_body"] = "Magic Keyboard battery is below {0}% (Current: {1}%)"
        },
        ["ja"] = new()
        {
            ["checking"] = "接続確認中...",
            ["not_connected"] = "未接続",
            ["click_retry"] = "未接続 (クリックで再試行)",
            ["click_refresh"] = "クリックで今すぐ更新",
            ["charging"] = "充電中",
            ["unavailable"] = "残量取得不可",
            ["unavailable_retry"] = "残量取得不可 (クリックで再試行)",
            ["open_history"] = "バッテリー履歴...",
            ["no_history"] = "バッテリー履歴はまだ記録されていません。",
            ["history_title"] = "Magic Keyboard バッテリー履歴",
            ["range_24h"] = "24時間",
            ["range_7d"] = "7日",
            ["range_30d"] = "30日",
            ["range_all"] = "すべて",
            ["stat_current"] = "現在の残量",
            ["stat_drain"] = "平均消費",
            ["stat_drain_value"] = "{0:0.#}% / 日",
            ["stat_remaining"] = "推定残り時間",
            ["stat_days_value"] = "約 {0:0.#} 日",
            ["stat_hours_value"] = "約 {0:0} 時間",
            ["stat_not_enough"] = "データ不足",
            ["stat_last_charge"] = "最後の充電",
            ["ago_days"] = "{0} 日前",
            ["ago_hours"] = "{0} 時間前",
            ["ago_minutes"] = "{0} 分前",
            ["chart_battery"] = "バッテリー残量",
            ["chart_threshold"] = "通知 {0}%",
            ["chart_threshold_legend"] = "低残量通知",
            ["btn_open_csv"] = "CSV を開く",
            ["btn_refresh"] = "再読み込み",
            ["open_settings"] = "設定...",
            ["exit_app"] = "MagicKeyBattery を終了",
            ["already_running"] = "MagicKeyBattery は既にタスクトレイで実行中です。",
            ["dialog_title"] = "MagicKeyBattery 設定",
            ["lbl_interval"] = "自動更新間隔 (分):",
            ["lbl_threshold"] = "低残量通知を行う基準 (%):",
            ["lbl_hint"] = "※0に設定すると通知オフになります",
            ["chk_startup"] = "Windows起動時に実行する",
            ["lbl_keyboard"] = "キー配置 (Logitech MX Keys 風)",
            ["chk_remap"] = "MX Keys 配列を使う (Ctrl | Win | Alt ... AltGr | Ctrl)",
            ["chk_media_keys"] = "F1〜F12 をメディアキーに (Ctrl/Alt/Shift/Win で F キー)",
            ["chk_swap_iso"] = "^ と < キーを入れ替える (ドイツ語 Apple キーボード)",
            ["menu_media_keys"] = "F1〜F12 をメディアキーに",
            ["open_devices"] = "デバイス (F13 PC · F14 TV · F15 スマホ/PC)...",
            ["serial_label"] = "シリアル",
            ["serial_copied"] = "シリアル番号をクリップボードにコピーしました。",
            ["chk_device_switching"] = "F13 / F14 / F15 で入力先を PC / TV / スマホに切り替える",
            ["mode_pc"] = "PC",
            ["mode_tv"] = "TV に入力中",
            ["mode_phone"] = "スマホに入力中",
            ["tv_not_configured"] = "TV が未設定です (トレイ → デバイス)",
            ["tv_unreachable"] = "TV に接続できません — PC に戻りました",
            ["mode_remote"] = "入力先:",
            ["remote_connecting"] = "別の PC に接続中...",
            ["remote_not_configured"] = "別の PC がペアリングされていません (トレイ → デバイス)",
            ["remote_disconnected"] = "別の PC との接続が切れました — この PC に戻りました",
            ["devices_f15"] = "F15 の接続先",
            ["devices_f15_phone"] = "📱 Android スマホ",
            ["devices_f15_pc"] = "🖥 別の PC",
            ["devices_pc_hint"] = "相手の PC: MagicKeyBattery を起動 → トレイ → デバイス →「この PC の操作を許可」をオン →「ペアリングコードを表示」。その PC のアドレスとコードをここに入力します (初回のみ)。両方の PC を同じネットワークに接続してください。",
            ["devices_pc_address"] = "PC のアドレス:",
            ["devices_f15_note"] = "別の PC との通信は暗号化 (TLS) され、ペアリング後はその PC の証明書にしか接続しません。家庭内ネットワークのコンピューターだけを受け付けます。受信側の PC でファイアウォールの許可を求められたら「プライベート ネットワーク」だけを許可してください。",
            ["devices_receiver"] = "🖥 この PC を受信側にする",
            ["devices_receiver_enable"] = "別の PC のキーボードからこの PC の操作を許可する",
            ["devices_receiver_address"] = "この PC のアドレス",
            ["devices_receiver_code"] = "ペアリングコードを表示",
            ["devices_receiver_code_hint"] = "2 分以内に相手の PC でこのコードを入力してください",
            ["devices_receiver_forget"] = "ペアリング済みの PC を削除",
            ["devices_receiver_forgotten"] = "ペアリング済みの PC をすべて削除しました",
            ["devices_receiver_paired"] = "ペアリング済み",
            ["phone_not_installed"] = "scrcpy がインストールされていません",
            ["phone_connecting"] = "スマホに接続中...",
            ["devices_title"] = "デバイス",
            ["devices_tv"] = "📺 TV (F14) — LG webOS",
            ["devices_tv_ip"] = "TV の IP アドレス:",
            ["devices_find"] = "検索",
            ["devices_pair"] = "ペアリング",
            ["devices_tv_hint"] = "TV の電源を入れておいてください。初回は TV に表示される確認をリモコンで許可します。表示されない場合は TV のネットワーク設定で「LG Connect Apps」をオンにしてください。",
            ["devices_searching"] = "検索中...",
            ["devices_not_found"] = "見つかりません",
            ["devices_tv_accept"] = "TV の確認画面で許可してください...",
            ["devices_paired"] = "ペアリング済み",
            ["devices_phone"] = "📱 スマホ (F15) — Android",
            ["devices_phone_hint"] = "スマホ: 設定 → 開発者向けオプション → ワイヤレスデバッグ → ON →「ペア設定コードによるデバイスのペア設定」。表示された IP:ポート とコードを入力します (初回のみ)。スマホと PC は同じ Wi-Fi に接続してください。",
            ["devices_pair_address"] = "ペア設定 IP:ポート:",
            ["devices_pair_code"] = "ペア設定コード:",
            ["devices_test"] = "接続テスト",
            ["devices_pairing"] = "ペアリング中...",
            ["devices_phone_found"] = "スマホに接続しました",
            ["devices_close"] = "閉じる",
            ["btn_save"] = "保存",
            ["notify_title"] = "バッテリー残量警告",
            ["notify_body"] = "Magic Keyboard の残量が {0}% 以下（現在: {1}%）になりました。"
        }
    };

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
            MessageBox.Show(T("already_running"), APP_NAME, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            Padding = new Padding(0, 5, 0, 5)
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
                Padding = new Padding(0, 3, 0, 3)
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

        contextMenu.Items.Add(Item(T("open_devices"), TrayMenuGlyph.Display, (s, e) => OpenDevices()));
        contextMenu.Items.Add(Item(T("open_history"), TrayMenuGlyph.Chart, (s, e) => OpenHistory()));
        contextMenu.Items.Add(Item(T("open_settings"), TrayMenuGlyph.Gear, (s, e) => ShowSettingsDialog()));
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(Item(T("exit_app"), TrayMenuGlyph.Power, (s, e) => ExitApplication()));

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

        // 充電中は低残量通知を出さない
        if (_notifyThreshold > 0 && level <= _notifyThreshold && !state.Charging)
        {
            if (!_hasNotifiedLowBattery)
            {
                // Windows 10/11 ではバルーンチップはトースト通知として表示される
                _trayIcon.ShowBalloonTip(
                    5000,
                    T("notify_title"),
                    string.Format(T("notify_body"), _notifyThreshold, level),
                    ToolTipIcon.Warning
                );
                _hasNotifiedLowBattery = true;
            }
        }
        else
        {
            _hasNotifiedLowBattery = false;
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
        using Form configForm = new Form
        {
            Width = 460,
            Height = 450,
            Text = T("dialog_title"),
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.White
        };
        using Font hintFont = new Font(configForm.Font.FontFamily, 7.5f);

        Label lblInterval = new Label { Text = T("lbl_interval"), Left = 20, Top = 20, Width = 150, ForeColor = Color.LightGray };
        NumericUpDown numInterval = new NumericUpDown { Left = 180, Top = 18, Width = 80, Minimum = 1, Maximum = 60, Value = _intervalMinutes, BackColor = Color.FromArgb(50, 50, 50), ForeColor = Color.White };

        Label lblThreshold = new Label { Text = T("lbl_threshold"), Left = 20, Top = 60, Width = 150, ForeColor = Color.LightGray };
        NumericUpDown numThreshold = new NumericUpDown { Left = 180, Top = 58, Width = 80, Minimum = 0, Maximum = 100, Value = _notifyThreshold, BackColor = Color.FromArgb(50, 50, 50), ForeColor = Color.White };
        Label lblHint = new Label { Text = T("lbl_hint"), Left = 20, Top = 85, Width = 280, Font = hintFont, ForeColor = Color.Gray };

        Label lblLang = new Label { Text = "Language / 言語:", Left = 20, Top = 115, Width = 150, ForeColor = Color.LightGray };
        ComboBox cmbLang = new ComboBox { Left = 180, Top = 112, Width = 80, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(50, 50, 50), ForeColor = Color.White };
        cmbLang.Items.AddRange(new object[] { "en", "ja" });
        cmbLang.SelectedItem = _language;

        CheckBox chkStartup = new CheckBox { Text = T("chk_startup"), Left = 20, Top = 155, Width = 250, Checked = IsStartupEnabled(), ForeColor = Color.LightGray };

        // --- キー配置 ---
        Label lblKeyboard = new Label { Text = T("lbl_keyboard"), Left = 20, Top = 195, Width = 400, ForeColor = Color.White };
        CheckBox chkRemap = new CheckBox { Text = T("chk_remap"), Left = 20, Top = 220, Width = 410, Checked = _remapEnabled, ForeColor = Color.LightGray };
        CheckBox chkMedia = new CheckBox { Text = T("chk_media_keys"), Left = 40, Top = 248, Width = 390, Checked = _mediaKeys, ForeColor = Color.LightGray };
        CheckBox chkSwapIso = new CheckBox { Text = T("chk_swap_iso"), Left = 40, Top = 276, Width = 390, Checked = _swapIsoKeys, ForeColor = Color.LightGray };
        CheckBox chkSwitching = new CheckBox { Text = T("chk_device_switching"), Left = 20, Top = 306, Width = 410, Checked = _deviceSwitching, ForeColor = Color.LightGray };
        void UpdateRemapControls() => chkMedia.Enabled = chkSwapIso.Enabled = chkRemap.Checked;
        chkRemap.CheckedChanged += (s, e) => UpdateRemapControls();
        UpdateRemapControls();

        Button btnSave = new Button { Text = T("btn_save"), Left = 175, Top = 355, Width = 90, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White };
        btnSave.FlatAppearance.BorderSize = 0;

        btnSave.Click += async (s, e) =>
        {
            _intervalMinutes = (int)numInterval.Value;
            _notifyThreshold = (int)numThreshold.Value;
            _language = cmbLang.SelectedItem?.ToString() ?? "en";
            _remapEnabled = chkRemap.Checked;
            _mediaKeys = chkMedia.Checked;
            _swapIsoKeys = chkSwapIso.Checked;
            _deviceSwitching = chkSwitching.Checked;

            SaveSettings();
            ToggleStartup(chkStartup.Checked);
            ApplyRemapSettings();

            if (_timer != null) _timer.Interval = _intervalMinutes * 60 * 1000;

            BuildContextMenu();
            configForm.Close();

            await UpdateBatteryLevelAsync();
        };

        configForm.Controls.AddRange(new Control[] { lblInterval, numInterval, lblThreshold, numThreshold, lblHint, lblLang, cmbLang, chkStartup, lblKeyboard, chkRemap, chkMedia, chkSwapIso, chkSwitching, btnSave });
        configForm.ShowDialog();
    }

    private static void SaveSettings()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(REG_APP_KEY))
            {
                key.SetValue("IntervalMinutes", _intervalMinutes);
                key.SetValue("NotifyThreshold", _notifyThreshold);
                key.SetValue("Language", _language);
                key.SetValue("RemapEnabled", _remapEnabled ? 1 : 0);
                key.SetValue("MediaKeys", _mediaKeys ? 1 : 0);
                key.SetValue("SwapIsoKeys", _swapIsoKeys ? 1 : 0);
                key.SetValue("DeviceSwitching", _deviceSwitching ? 1 : 0);
                key.SetValue("TvIp", _tvIp);
                key.SetValue("TvClientKeyProtected", ProtectSecret(_tvClientKey));
                key.SetValue("TvCertPin", _tvCertPin);
                key.SetValue("F15IsPc", _f15IsPc ? 1 : 0);
                key.SetValue("RemotePcHost", _remotePcHost);
                key.SetValue("RemotePcTokenProtected", ProtectSecret(_remotePcToken));
                key.SetValue("RemotePcCertPin", _remotePcCertPin);
                key.SetValue("ReceiverEnabled", _receiverEnabled ? 1 : 0);
                key.DeleteValue("TvClientKey", false); // 旧バージョンの平文の値を削除
            }
        }
        catch { }
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
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(REG_APP_KEY))
            {
                if (key != null)
                {
                    _intervalMinutes = Math.Clamp(Convert.ToInt32(key.GetValue("IntervalMinutes", 3)), 1, 60);
                    _notifyThreshold = Math.Clamp(Convert.ToInt32(key.GetValue("NotifyThreshold", 20)), 0, 100);
                    _language = key.GetValue("Language", "")?.ToString() ?? "";
                    _remapEnabled = Convert.ToInt32(key.GetValue("RemapEnabled", 0)) != 0;
                    _mediaKeys = Convert.ToInt32(key.GetValue("MediaKeys", 1)) != 0;
                    _swapIsoKeys = Convert.ToInt32(key.GetValue("SwapIsoKeys", 0)) != 0;
                    _deviceSwitching = Convert.ToInt32(key.GetValue("DeviceSwitching", 1)) != 0;
                    _tvIp = key.GetValue("TvIp", "")?.ToString() ?? "";
                    _tvClientKey = UnprotectSecret(key.GetValue("TvClientKeyProtected", "")?.ToString() ?? "");
                    _tvCertPin = key.GetValue("TvCertPin", "")?.ToString() ?? "";
                    _f15IsPc = Convert.ToInt32(key.GetValue("F15IsPc", 0)) != 0;
                    _remotePcHost = key.GetValue("RemotePcHost", "")?.ToString() ?? "";
                    _remotePcToken = UnprotectSecret(key.GetValue("RemotePcTokenProtected", "")?.ToString() ?? "");
                    _remotePcCertPin = key.GetValue("RemotePcCertPin", "")?.ToString() ?? "";
                    _receiverEnabled = Convert.ToInt32(key.GetValue("ReceiverEnabled", 0)) != 0;

                    // 旧バージョンの平文キーがあれば暗号化して保存し直す
                    string legacyKey = key.GetValue("TvClientKey", "")?.ToString() ?? "";
                    if (_tvClientKey.Length == 0 && legacyKey.Length > 0)
                    {
                        _tvClientKey = legacyKey;
                        _migrateLegacySecret = true;
                    }
                }
            }
        }
        catch { }

        // 未設定 (初回起動) または不明な値の場合は OS のロケールから判定
        if (!LocalizedText.ContainsKey(_language))
        {
            string currentCulture = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
            _language = currentCulture.StartsWith("ja") ? "ja" : "en";
        }
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
