using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MagicKeyBattery;

internal enum DeviceMode { Pc, Tv, Phone, RemotePc }

// MX Keys の Easy-Switch 風: F13 = この PC / F14 = TV / F15 = スマホ
//
// Magic Keyboard は 1 台としかペアリングできず、接続先を切り替えると F13 が PC に届かなくなるため、
// キーボードは PC に接続したまま、アプリが入力を転送する:
//   TV    : LG webOS のネットワーク API (リモコンのボタン + テキスト入力)
//   スマホ: scrcpy (Android のワイヤレスデバッグ経由で物理キーボードとして入力)
internal static class DeviceSwitcher
{
    public static volatile DeviceMode Mode = DeviceMode.Pc;

    private static SynchronizationContext? _ui;
    private static Func<string, string> _t = k => k;
    private static TvRemote? _tv;
    private static ModeBadge? _badge;
    private static bool _switching;

    public static TvRemote? Tv => _tv;

    // F15 の接続先: Android スマホ or 別の PC
    public static DeviceMode F15Mode { get; set; } = DeviceMode.Phone;

    private static RemoteLink? _remote;
    private static RemoteKeyOutput? _remoteOutput;

    public static void ConfigureRemotePc(string? host, byte[]? token, string? certPin)
    {
        if (Mode == DeviceMode.RemotePc) LeaveRemotePc();
        _remote?.Dispose();
        _remote = null;
        _remoteOutput = null;
        if (string.IsNullOrWhiteSpace(host) || token == null || token.Length != 32 || string.IsNullOrEmpty(certPin)) return;

        _remote = new RemoteLink(host.Trim(), token, certPin);
        _remoteOutput = new RemoteKeyOutput(_remote);
        // 相手の PC でコピーしたテキストをこの PC のクリップボードへ (クリップボードは UI スレッドで)
        _remote.ClipboardReceived += text => _ui?.Post(_ => ClipboardSync.SetFromRemote(text), null);
        _remote.Disconnected += reason => _ui?.Post(_ =>
        {
            if (Mode != DeviceMode.RemotePc) return;
            KeyboardRemapper.SetOutput(KeyboardRemapper.LocalKeyOutput.Instance);
            SetMode(DeviceMode.Pc);
            Badge.Flash($"🖥 {_t("remote_disconnected")}: {reason}", 4000);
        }, null);
    }

    // この PC のクリップボードが変わった時 (別の PC を操作中なら送る)
    public static void OnLocalClipboardChanged(string text)
    {
        if (Mode == DeviceMode.RemotePc && _remote?.Connected == true) _remote.SendClipboard(text);
    }

    private static void LeaveRemotePc()
    {
        // 相手の PC で押されたままのキーを離してから、この PC への入力に戻す
        KeyboardRemapper.ReleaseHeldKeys();
        _remoteOutput?.ReleaseAll();
        KeyboardRemapper.SetOutput(KeyboardRemapper.LocalKeyOutput.Instance);
        RemoteLink? link = _remote;
        if (link != null) _ = link.CloseGracefullyAsync();
    }

    public static void Init(SynchronizationContext ui, Func<string, string> translate)
    {
        _ui = ui;
        _t = translate;
        PhoneLink.Exited += () => _ui?.Post(_ => { if (Mode == DeviceMode.Phone) SetMode(DeviceMode.Pc); }, null);
    }

    public static void ConfigureTv(string? ip, string? clientKey, string? certPin, Action<string, string?> saveCredentials)
    {
        _tv?.Dispose();
        _tv = string.IsNullOrWhiteSpace(ip) ? null : new TvRemote(ip.Trim(), clientKey, certPin, saveCredentials);
        if (_tv != null)
        {
            // TV に届かない場合はキーを握りつぶし続けないよう PC に戻す
            _tv.ConnectionFailed += error => _ui?.Post(_ =>
            {
                if (Mode != DeviceMode.Tv) return;
                SetMode(DeviceMode.Pc);
                Badge.Flash($"📺 {_t("tv_unreachable")}: {error}", 4000);
            }, null);
        }
        if (_tv == null && Mode == DeviceMode.Tv) SetMode(DeviceMode.Pc);
    }

    // スマホのウィンドウは動画を受信し続けるため、PC に戻って 2 分使わなければ閉じる
    private static System.Windows.Forms.Timer? _phoneIdleTimer;

    private static void SchedulePhoneClose()
    {
        _phoneIdleTimer ??= CreatePhoneIdleTimer();
        _phoneIdleTimer.Stop();
        _phoneIdleTimer.Start();
    }

    private static System.Windows.Forms.Timer CreatePhoneIdleTimer()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 2 * 60 * 1000 };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            if (Mode != DeviceMode.Phone) PhoneLink.Close();
        };
        return timer;
    }

    // フックから呼ばれる: 重い処理は UI スレッドの非同期処理に回す
    public static void RequestSwitch(DeviceMode target) => _ui?.Post(async _ => await SwitchAsync(target), null);

    public static void Shutdown()
    {
        if (Mode == DeviceMode.RemotePc) LeaveRemotePc();
        _remote?.Dispose();
        _phoneIdleTimer?.Stop();
        PhoneLink.Close();
        _tv?.Dispose();
        _badge?.Close();
    }

    private static async Task SwitchAsync(DeviceMode target)
    {
        if (_switching) return;
        _switching = true;
        try
        {
            _phoneIdleTimer?.Stop();
            if (Mode == DeviceMode.Phone && target != DeviceMode.Phone)
            {
                PhoneLink.Hide();
                SchedulePhoneClose();
            }
            // TV の接続は TV モードの間だけ
            if (Mode == DeviceMode.Tv && target != DeviceMode.Tv) _tv?.Disconnect();
            if (Mode == DeviceMode.RemotePc && target != DeviceMode.RemotePc) LeaveRemotePc();

            switch (target)
            {
                case DeviceMode.Pc:
                    SetMode(DeviceMode.Pc);
                    break;

                case DeviceMode.Tv:
                    if (_tv == null)
                    {
                        Badge.Flash($"📺 {_t("tv_not_configured")}");
                        SetMode(DeviceMode.Pc);
                        return;
                    }
                    SetMode(DeviceMode.Tv);
                    _tv.Warmup();
                    break;

                case DeviceMode.RemotePc:
                    if (Mode == DeviceMode.RemotePc) { SetMode(DeviceMode.RemotePc); return; }
                    if (_remote == null || _remoteOutput == null)
                    {
                        Badge.Flash($"🖥 {_t("remote_not_configured")}", 3000);
                        return;
                    }
                    Badge.ShowPersistent($"🖥 {_t("remote_connecting")}");
                    string? remoteError = await _remote.ConnectAsync();
                    if (remoteError == null)
                    {
                        KeyboardRemapper.SetOutput(_remoteOutput);
                        SetMode(DeviceMode.RemotePc);

                        // この PC でコピーした内容を相手でも貼り付けられるように送っておく
                        if (_remote.ClipboardSupported && ClipboardSync.ReadShareableText() is string clip)
                            _remote.SendClipboard(clip);
                    }
                    else
                    {
                        SetMode(DeviceMode.Pc);
                        Badge.Flash($"🖥 {remoteError}", 4000);
                    }
                    break;

                case DeviceMode.Phone:
                    if (!PhoneLink.Installed)
                    {
                        Badge.Flash($"📱 {_t("phone_not_installed")}");
                        return;
                    }
                    Badge.ShowPersistent($"📱 {_t("phone_connecting")}");
                    string? error = await PhoneLink.ShowAsync();
                    if (error == null)
                    {
                        SetMode(DeviceMode.Phone);
                    }
                    else
                    {
                        SetMode(DeviceMode.Pc);
                        Badge.Flash($"📱 {error}", 4000);
                    }
                    break;
            }
        }
        finally
        {
            _switching = false;
        }
    }

    private static void SetMode(DeviceMode mode)
    {
        _tvShift = _tvAltGr = false;
        // Caps Lock の状態は UI スレッドで読む (フックの専用スレッドでは GetKeyState が正しくない)
        _tvCaps = Control.IsKeyLocked(Keys.CapsLock);
        Mode = mode;
        switch (mode)
        {
            case DeviceMode.Pc: Badge.Flash($"💻 {_t("mode_pc")}"); break;
            case DeviceMode.Tv: Badge.ShowPersistent($"📺 {_t("mode_tv")}   ·   F13 = {_t("mode_pc")}"); break;
            case DeviceMode.Phone: Badge.ShowPersistent($"📱 {_t("mode_phone")}   ·   F13 = {_t("mode_pc")}"); break;
            case DeviceMode.RemotePc: Badge.ShowPersistent($"🖥 {_t("mode_remote")} {_remote?.Host}   ·   F13 = {_t("mode_pc")}"); break;
        }
    }

    private static ModeBadge Badge => _badge == null || _badge.IsDisposed ? (_badge = new ModeBadge()) : _badge;

    // --- TV への転送 (フックのスレッド = UI スレッドから呼ばれる。送信はキューに積むだけ) ---

    private static volatile bool _tvShift, _tvAltGr, _tvCaps;
    private static bool _tvPlaying = true;

    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint idThread);
    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    public static void ForwardToTv(uint vk, uint scan, bool up)
    {
        TvRemote? tv = _tv;
        if (tv == null) return;

        switch (vk)
        {
            case 0xA0: case 0xA1: case 0x10: _tvShift = !up; return;  // Shift
            case 0x5C: _tvAltGr = !up; return;                        // 右 command = AltGr
            case 0x14: if (!up) _tvCaps = !_tvCaps; return;          // Caps Lock (TV モード中は PC 側で切り替わらないので自分で追跡)
            case 0xA2: case 0xA3: case 0xA4: case 0xA5: case 0x5B: case 0x11: case 0x12: return; // 他の修飾キー
        }
        if (up) return;

        // TV の画面に文字入力欄 (検索など) が開いている時だけ、文字・数字を文字として送る
        bool typing = tv.TextInputFocused;

        string? button = vk switch
        {
            0x26 => "UP",
            0x28 => "DOWN",
            0x25 => "LEFT",
            0x27 => "RIGHT",
            0x0D => typing ? null : "ENTER",
            0x1B => "BACK",
            0x24 => "HOME",       // Home
            0x73 => "HOME",       // F4 (Launchpad)
            0x21 => "CHANNELUP",  // Page Up
            0x22 => "CHANNELDOWN",// Page Down
            0x75 => "CHANNELUP",  // F6 (Magic Keyboard ではアイコン無し → TV ではチャンネル)
            0x74 => "CHANNELDOWN",// F5
            0x76 => "REWIND",     // F7
            0x78 => "FASTFORWARD",// F9
            0x79 => "MUTE",       // F10
            0x7A => "VOLUMEDOWN", // F11
            0x7B => "VOLUMEUP",   // F12
            _ => null
        };

        if (!typing && button == null)
        {
            // 数字 (上段・テンキー) = チャンネル番号の直接入力。テンキーの +/- = チャンネル上下
            if (vk >= 0x30 && vk <= 0x39 && !_tvShift && !_tvAltGr) button = ((char)vk).ToString();
            else if (vk >= 0x60 && vk <= 0x69) button = ((char)('0' + vk - 0x60)).ToString();
            else if (vk == 0x6B) button = "CHANNELUP";
            else if (vk == 0x6D) button = "CHANNELDOWN";
            else if (vk == 0x08) button = "BACK";
        }

        if (vk == 0x77) // F8: 再生/一時停止
        {
            button = _tvPlaying ? "PAUSE" : "PLAY";
            _tvPlaying = !_tvPlaying;
        }

        if (button != null)
        {
            tv.Button(button);
            return;
        }

        if (!typing) return; // 入力欄が無い時の文字キーは何もしない

        if (vk == 0x08) // Backspace
        {
            tv.DeleteCharacters(1);
            return;
        }
        if (vk == 0x0D) // Enter: 入力を確定
        {
            tv.SendEnterKey();
            return;
        }

        string? text = TranslateToText(vk, scan);
        if (!string.IsNullOrEmpty(text)) tv.InsertText(text);
    }

    // Shift / AltGr / Caps を考慮して、現在のキーボードレイアウトで文字に変換
    private static string? TranslateToText(uint vk, uint scan)
    {
        var state = new byte[256];
        if (_tvShift) state[0x10] = 0x80;
        if (_tvAltGr) { state[0x11] = 0x80; state[0x12] = 0x80; }
        if (_tvCaps) state[0x14] = 0x01;

        IntPtr layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
        var buffer = new StringBuilder(8);
        // wFlags=4: キーボードの状態 (デッドキー) を変更しない
        int count = ToUnicodeEx(vk, scan, state, buffer, buffer.Capacity, 4, layout);
        if (count <= 0) return null;

        string s = buffer.ToString(0, count);
        foreach (char c in s)
        {
            if (char.IsControl(c)) return null;
        }
        return s;
    }
}

// --- LG webOS TV (SSAP over WebSocket) ---
internal sealed class TvRemote : IDisposable
{
    private static readonly string[] Permissions =
    {
        "LAUNCH", "CONTROL_AUDIO", "CONTROL_DISPLAY", "CONTROL_INPUT_MEDIA_PLAYBACK", "CONTROL_INPUT_TEXT",
        "CONTROL_MOUSE_AND_KEYBOARD", "READ_INPUT_DEVICE_LIST", "READ_INSTALLED_APPS", "CONTROL_POWER",
        "READ_CURRENT_CHANNEL", "READ_RUNNING_APPS", "WRITE_NOTIFICATION_TOAST"
    };

    private readonly string _ip;
    private string? _clientKey;
    private string? _certPin;          // TV の証明書の SHA-256 (初回接続で記憶)
    private string? _observedPin;
    private volatile bool _pinMismatch;
    private readonly Action<string, string?> _saveCredentials;
    // キューの上限: TV が応答しない間に押したキーが溜まり続けないように (古いものから捨てる)
    private readonly Channel<Func<Task>> _queue = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();

    private ClientWebSocket? _ws;
    private ClientWebSocket? _pointer;
    private TaskCompletionSource<string>? _registered;
    private int _nextId;

    public string Ip => _ip;

    // 再試行しても TV に届かなかった時 (TV モードを自動で解除するため)
    public event Action<string>? ConnectionFailed;

    public TvRemote(string ip, string? clientKey, string? certPin, Action<string, string?> saveCredentials)
    {
        _ip = ip;
        _clientKey = string.IsNullOrWhiteSpace(clientKey) ? null : clientKey;
        _certPin = string.IsNullOrWhiteSpace(certPin) ? null : certPin;
        _saveCredentials = saveCredentials;
        _ = Task.Run(WorkerAsync);
    }

    public void Warmup() => Enqueue(() => EnsureConnectedAsync(allowPrompt: false));
    public void Button(string name) => Enqueue(async () =>
    {
        await EnsureConnectedAsync(allowPrompt: false);
        await SendTextAsync(_pointer!, $"type:button\nname:{name}\n\n");
    });
    public void InsertText(string text) => Enqueue(() => RequestAsync("ssap://com.webos.service.ime/insertText", new { text, replace = 0 }));
    public void DeleteCharacters(int count) => Enqueue(() => RequestAsync("ssap://com.webos.service.ime/deleteCharacters", new { count }));
    public void SendEnterKey() => Enqueue(() => RequestAsync("ssap://com.webos.service.ime/sendEnterKey", new { }));

    // TV の画面上の文字入力欄にフォーカスがあるか (TV から通知される)
    public volatile bool TextInputFocused;
    public void Toast(string message) => Enqueue(() => RequestAsync("ssap://system.notifications/createToast", new { message }));

    // 設定画面の「ペアリング」: TV に許可のプロンプトを出して承認を待つ
    public async Task<string?> PairAsync()
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            try
            {
                ResetConnection();
                // ペアリングはユーザーの明示的な操作なので、TV の証明書を記憶し直す (TV を交換した場合など)
                _certPin = null;
                _pinMismatch = false;
                await EnsureConnectedAsync(allowPrompt: true);
                await RequestAsync("ssap://system.notifications/createToast", new { message = "MagicKeyBattery ⌨" });
                done.TrySetResult(null);
            }
            catch (Exception ex)
            {
                // ペアリング画面で結果を表示するので、ワーカーの再試行はしない
                ResetConnection();
                done.TrySetResult(ex.Message);
            }
        });
        return await done.Task;
    }

    private void Enqueue(Func<Task> job) => _queue.Writer.TryWrite(job);

    private async Task WorkerAsync()
    {
        await foreach (Func<Task> job in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
        {
            try
            {
                await job().ConfigureAwait(false);
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                // 接続が切れていた場合は 1 回だけ再接続して再試行
                Debug.WriteLine($"TvRemote: {ex.Message}");
                ResetConnection();
                try { await job().ConfigureAwait(false); }
                catch (Exception ex2)
                {
                    Debug.WriteLine($"TvRemote retry: {ex2.Message}");
                    ResetConnection();
                    ConnectionFailed?.Invoke(ex2.Message);
                }
            }
        }
    }

    private async Task RequestAsync(string uri, object payload)
    {
        await EnsureConnectedAsync(allowPrompt: false).ConfigureAwait(false);
        string json = JsonSerializer.Serialize(new { type = "request", id = $"req_{Interlocked.Increment(ref _nextId)}", uri, payload });
        await SendTextAsync(_ws!, json).ConfigureAwait(false);
    }

    private async Task EnsureConnectedAsync(bool allowPrompt)
    {
        if (_ws?.State == WebSocketState.Open && _pointer?.State == WebSocketState.Open) return;
        ResetConnection();

        if (_clientKey == null && !allowPrompt) throw new InvalidOperationException("TV is not paired");

        // 暗号化された wss://:3001 のみ使う (平文の ws://:3000 はペアリングキーや入力内容が LAN 上で読めてしまう)
        _ws = await ConnectAsync($"wss://{_ip}:3001").ConfigureAwait(false)
              ?? throw new InvalidOperationException(_pinMismatch ? "TV certificate changed — pair again" : "TV not reachable");

        _registered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ClientWebSocket mainSocket = _ws;
        _ = Task.Run(() => ReceiveLoopAsync(mainSocket));

        var payload = new Dictionary<string, object>
        {
            ["forcePairing"] = false,
            ["pairingType"] = "PROMPT",
            ["manifest"] = new
            {
                manifestVersion = 1,
                appVersion = "1.0",
                appId = "com.magickeybattery.remote",
                vendorId = "com.magickeybattery",
                localizedAppNames = new Dictionary<string, string> { [""] = "MagicKeyBattery" },
                permissions = Permissions
            }
        };
        if (_clientKey != null) payload["client-key"] = _clientKey;
        await SendTextAsync(_ws, JsonSerializer.Serialize(new { type = "register", id = "register_0", payload })).ConfigureAwait(false);

        // 承認済みならすぐ返る。初回はユーザーが TV で許可するまで待つ
        var timeout = Task.Delay(allowPrompt ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(8));
        if (await Task.WhenAny(_registered.Task, timeout).ConfigureAwait(false) != _registered.Task)
            throw new TimeoutException("TV did not accept the connection");

        string key = await _registered.Task.ConfigureAwait(false);
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("TV returned no pairing key");

        // 初回接続で TV の証明書を記憶 (以降は同じ証明書の相手にしか接続しない)
        bool pinLearned = _certPin == null && _observedPin != null;
        if (pinLearned) _certPin = _observedPin;
        if (key != _clientKey || pinLearned)
        {
            _clientKey = key;
            _saveCredentials(key, _certPin);
        }

        // リモコンのボタン用ソケット
        var socketPath = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSocketPath = socketPath;
        await SendTextAsync(_ws, JsonSerializer.Serialize(new { type = "request", id = "pointer_0", uri = "ssap://com.webos.service.networkinput/getPointerInputSocket" })).ConfigureAwait(false);
        if (await Task.WhenAny(socketPath.Task, Task.Delay(5000)).ConfigureAwait(false) != socketPath.Task)
            throw new TimeoutException("No pointer socket");

        // TV が返したアドレスは検証してから使う (別のホストへ接続させられないように)
        string pointerUrl = BuildPointerUrl(await socketPath.Task.ConfigureAwait(false));
        _pointer = await ConnectAsync(pointerUrl).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Pointer socket failed");
        ClientWebSocket pointerSocket = _pointer;
        _ = Task.Run(() => DrainAsync(pointerSocket));

        // 文字入力欄が開いた/閉じたの通知を受け取る (数字を「チャンネル番号」と「文字」で使い分けるため)
        TextInputFocused = false;
        await SendTextAsync(_ws, JsonSerializer.Serialize(new { type = "subscribe", id = "keyboard_0", uri = "ssap://com.webos.service.ime/registerRemoteKeyboard" })).ConfigureAwait(false);
    }

    // 同じ TV・同じ暗号化ポートのパスだけを許可
    private string BuildPointerUrl(string socketPath)
    {
        if (!Uri.TryCreate(socketPath, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "ws" && uri.Scheme != "wss"))
            throw new InvalidOperationException("Invalid pointer socket address");
        if (!string.Equals(uri.Host, _ip, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Pointer socket points to another host");
        return $"wss://{_ip}:3001{uri.AbsolutePath}";
    }

    private TaskCompletionSource<string>? _pendingSocketPath;

    private async Task<ClientWebSocket?> ConnectAsync(string url)
    {
        var ws = new ClientWebSocket();
        // TV の証明書は LG 独自の CA なので Windows では検証できない → 証明書ピンニング (初回に記憶した証明書と一致すること)
        ws.Options.RemoteCertificateValidationCallback = (sender, cert, chain, errors) => ValidateCertificate(cert);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            await ws.ConnectAsync(new Uri(url), timeout.Token).ConfigureAwait(false);
            return ws;
        }
        catch
        {
            ws.Dispose();
            return null;
        }
    }

    private bool ValidateCertificate(System.Security.Cryptography.X509Certificates.X509Certificate? cert)
    {
        if (cert == null) return false;
        using var cert2 = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert);
        string pin = cert2.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);

        if (_certPin == null)
        {
            _observedPin = pin; // 初回 (ペアリング時) のみ: 登録が成功したら保存
            return true;
        }

        bool ok = string.Equals(pin, _certPin, StringComparison.OrdinalIgnoreCase);
        _pinMismatch = !ok;
        return ok;
    }

    private const int MaxMessageBytes = 256 * 1024;

    private async Task ReceiveLoopAsync(ClientWebSocket ws)
    {
        var buffer = new byte[16 * 1024];
        var message = new StringBuilder();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await ws.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                // 異常に大きいメッセージはメモリを消費させる攻撃とみなして切断
                if (message.Length > MaxMessageBytes) break;
                if (!result.EndOfMessage) continue;

                JsonNode? node = JsonNode.Parse(message.ToString());
                message.Clear();
                string? type = (string?)node?["type"];
                string? id = (string?)node?["id"];

                if (type == "registered")
                    _registered?.TrySetResult((string?)node?["payload"]?["client-key"] ?? "");
                else if (type == "error" && id == "register_0")
                    _registered?.TrySetException(new InvalidOperationException((string?)node?["error"] ?? "register error"));
                else if (id == "pointer_0" && (string?)node?["payload"]?["socketPath"] is string path)
                    _pendingSocketPath?.TrySetResult(path);
                else if (id == "keyboard_0" && node?["payload"]?["currentWidget"] is JsonNode widget)
                    TextInputFocused = widget["focus"] is JsonValue focus && focus.TryGetValue(out bool focused) && focused;
            }
        }
        catch { }
        _registered?.TrySetException(new InvalidOperationException("TV connection closed"));
    }

    private async Task DrainAsync(ClientWebSocket ws)
    {
        var buffer = new byte[1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult r = await ws.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch { }
    }

    private static Task SendTextAsync(ClientWebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private void ResetConnection()
    {
        TextInputFocused = false;
        try { _pointer?.Abort(); _pointer?.Dispose(); } catch { }
        try { _ws?.Abort(); _ws?.Dispose(); } catch { }
        _pointer = null;
        _ws = null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        ResetConnection();
    }

    // SSDP で LG webOS TV を探す
    public static async Task<string?> DiscoverAsync()
    {
        using var udp = new UdpClient();
        byte[] request = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:lge-com:service:webos-second-screen:1\r\n\r\n");
        await udp.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                UdpReceiveResult result = await udp.ReceiveAsync(timeout.Token);
                // 家庭内ネットワークのアドレスだけを TV として受け入れる
                if (IsPrivateAddress(result.RemoteEndPoint.Address)) return result.RemoteEndPoint.Address.ToString();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    // TV モードを抜けたら接続を閉じる (次に F14 を押した時に再接続)
    public void Disconnect() => Enqueue(() =>
    {
        ResetConnection();
        return Task.CompletedTask;
    });
}

// --- Android スマホ (scrcpy) ---
internal static class PhoneLink
{
    public static readonly string ToolsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagicKeyBattery", "scrcpy");
    private static string Adb => Path.Combine(ToolsDir, "adb.exe");
    private static string Scrcpy => Path.Combine(ToolsDir, "scrcpy.exe");

    public static bool Installed => File.Exists(Adb) && File.Exists(Scrcpy);

    public static event Action? Exited;

    private static Process? _scrcpy;
    private const string WindowTitle = "MagicKeyBattery - Phone";

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    // null = 成功、それ以外はエラーメッセージ
    public static async Task<string?> ShowAsync()
    {
        if (_scrcpy != null && !_scrcpy.HasExited)
        {
            IntPtr existing = FindWindow(null, WindowTitle);
            if (existing != IntPtr.Zero)
            {
                ForceForeground(existing);
                return null;
            }
        }

        string? serial = await Task.Run(FindDevice);
        if (serial == null) return "No phone found (turn on Wireless debugging)";

        var psi = new ProcessStartInfo(Scrcpy)
        {
            WorkingDirectory = ToolsDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // uhid: 物理キーボードとして入力 (Android 側のキーボード配列がそのまま使われる)
        foreach (string arg in new[] { "-s", serial, "--keyboard=uhid", "--no-audio", "--max-size=1280", "--window-title", WindowTitle, "--shortcut-mod=lsuper+rsuper" })
            psi.ArgumentList.Add(arg);

        Process? started = Process.Start(psi);
        if (started == null) return "scrcpy failed to start";
        _scrcpy = started;
        started.EnableRaisingEvents = true;
        started.Exited += (s, e) => Exited?.Invoke();

        // ウィンドウが出るまで待つ (最大 15 秒)
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(250);
            if (started.HasExited) return "scrcpy exited (is the phone connected?)";
            IntPtr hwnd = FindWindow(null, WindowTitle);
            if (hwnd != IntPtr.Zero)
            {
                ForceForeground(hwnd);
                return null;
            }
        }
        return "scrcpy window did not appear";
    }

    // F13 で PC に戻る: スマホのウィンドウを最小化すると直前のウィンドウが前面に戻る
    public static void Hide()
    {
        IntPtr hwnd = FindWindow(null, WindowTitle);
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, 6 /* SW_MINIMIZE */);
    }

    // scrcpy を終了 (画面の受信を止めて PC とスマホの電池・通信を節約)
    public static void Close()
    {
        Process? p = _scrcpy;
        _scrcpy = null;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                p.CloseMainWindow();
                if (!p.WaitForExit(2000)) p.Kill();
            }
        }
        catch { }
        finally
        {
            p.Dispose();
        }
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        if (SetForegroundWindow(hwnd)) return;

        // 別プロセスのウィンドウを前面に出すには、直前に入力があったことにする必要がある (Alt を 1 回送る)
        keybd_event(0x12, 0, 0, new IntPtr(0x4D4B4231));
        keybd_event(0x12, 0, 2, new IntPtr(0x4D4B4231));
        SetForegroundWindow(hwnd);
    }

    // 接続済みのデバイス → 無ければワイヤレスデバッグの mDNS で探して接続
    public static string? FindDevice()
    {
        string? serial = FirstConnectedDevice();
        if (serial != null) return serial;

        string services = RunAdb(8000, "mdns", "services");
        foreach (string line in services.Split('\n'))
        {
            if (!line.Contains("_adb-tls-connect._tcp")) continue;
            foreach (string part in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // 家庭内ネットワークの IP:ポート だけに接続
                if (part.Contains(':') && IPEndPoint.TryParse(part.Trim(), out IPEndPoint? endpoint) && TvRemote.IsPrivateAddress(endpoint.Address))
                {
                    RunAdb(8000, "connect", endpoint.ToString());
                    serial = FirstConnectedDevice();
                    if (serial != null) return serial;
                }
            }
        }
        return null;
    }

    private static string? FirstConnectedDevice()
    {
        string output = RunAdb(8000, "devices");
        foreach (string line in output.Split('\n'))
        {
            string[] parts = line.Trim().Split('\t');
            if (parts.Length == 2 && parts[1] == "device") return parts[0];
        }
        return null;
    }

    // 設定画面の「ペアリング」: スマホの「ペアリングコードでデバイスをペアリング」に表示される IP:ポート とコード
    public static string Pair(string address, string code)
    {
        // 入力の検証: 家庭内ネットワークの IP:ポート と 6 桁のコードだけ (adb に余計な引数を渡させない)
        if (!IPEndPoint.TryParse(address.Trim(), out IPEndPoint? endpoint) || endpoint.Port == 0 || !TvRemote.IsPrivateAddress(endpoint.Address))
            return "Invalid address (expected e.g. 192.168.1.20:37123)";
        if (!System.Text.RegularExpressions.Regex.IsMatch(code.Trim(), "^[0-9]{6}$"))
            return "Invalid code (6 digits)";

        // コードはコマンドラインではなく標準入力で渡す (他のプロセスから引数が見えないように)
        return RunAdbWithInput(20000, code.Trim() + "\n", "pair", endpoint.ToString()).Trim();
    }

    public static string RunAdb(int timeoutMs, params string[] arguments) => RunAdbWithInput(timeoutMs, null, arguments);

    // 引数は ArgumentList で渡す (文字列連結によるコマンド注入を防ぐ)
    private static string RunAdbWithInput(int timeoutMs, string? stdin, params string[] arguments)
    {
        if (!File.Exists(Adb)) return "";
        var psi = new ProcessStartInfo(Adb)
        {
            WorkingDirectory = ToolsDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null
        };
        foreach (string arg in arguments) psi.ArgumentList.Add(arg);
        try
        {
            using Process p = Process.Start(psi)!;
            if (stdin != null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
            }
            return stdout.Result + stderr.Result;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}

// 画面下部に今の入力先を表示する小さなバッジ (フォーカスを奪わない、クリックも透過)
internal sealed class ModeBadge : Form
{
    private readonly System.Windows.Forms.Timer _hideTimer = new();
    private string _text = "";

    public ModeBadge()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(0x26, 0x26, 0x24);
        ForeColor = Color.White;
        Opacity = 0.92;
        Font = new Font("Segoe UI Semibold", 11f);
        AutoScaleMode = AutoScaleMode.Dpi;
        _hideTimer.Tick += (s, e) => { _hideTimer.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */ | 0x00000020 /* TRANSPARENT */ | 0x00080000 /* LAYERED */;
            return cp;
        }
    }

    public void ShowPersistent(string text) => Display(text, 0);

    public void Flash(string text, int milliseconds = 1300) => Display(text, milliseconds);

    private void Display(string text, int milliseconds)
    {
        _text = text;
        _hideTimer.Stop();

        Size textSize = TextRenderer.MeasureText(text, Font);
        int padX = LogicalToDeviceUnits(18), padY = LogicalToDeviceUnits(10);
        Size = new Size(textSize.Width + padX * 2, textSize.Height + padY * 2);
        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - LogicalToDeviceUnits(24));

        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
        {
            int r = LogicalToDeviceUnits(10) * 2;
            path.AddArc(0, 0, r, r, 180, 90);
            path.AddArc(Width - r, 0, r, r, 270, 90);
            path.AddArc(Width - r, Height - r, r, r, 0, 90);
            path.AddArc(0, Height - r, r, r, 90, 90);
            path.CloseFigure();
            Region = new Region(path);
        }

        if (!Visible) Show();
        Invalidate();

        if (milliseconds > 0)
        {
            _hideTimer.Interval = milliseconds;
            _hideTimer.Start();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        if (Rtl.Enabled) flags |= TextFormatFlags.RightToLeft;
        TextRenderer.DrawText(e.Graphics, _text, Font, ClientRectangle, ForeColor, flags);
    }
}
