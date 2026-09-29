using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MagicKeyBattery;

// 別の PC へキーボード入力を送る (F15 の接続先を「別の PC」にした場合)
//
// 相手の PC でも MagicKeyBattery を「受信」モードで動かす。
//   ・通信は TLS 1.2 以上。受信側の自己署名証明書をペアリング時に記憶し、以降はその証明書にしか接続しない
//   ・ペアリング: 受信側に表示される 6 桁のコード (2 分間・5 回まで) で、証明書のハッシュに対する HMAC を送る
//                 → 途中に別の機器が割り込むと証明書が変わるため HMAC が一致しない
//   ・ペアリング後は 32 バイトのランダムな鍵で認証 (送信側は DPAPI で暗号化保存、受信側はハッシュだけ保存)
//   ・家庭内ネットワークのアドレスからの接続だけ受け付ける
internal static class RemoteProtocol
{
    public const int Port = 50515;

    public const byte FrameKey = 1;
    public const byte FrameReleaseAll = 2;
    public const byte FramePing = 3;
    public const byte FrameLock = 4;

    public const int FrameSize = 6; // [種類][フラグ][vk 2 バイト][scan 2 バイト]
    public const int MaxLineBytes = 4096;

    public static string CertHash(X509Certificate cert)
    {
        using var c = new X509Certificate2(cert);
        return c.GetCertHashString(HashAlgorithmName.SHA256);
    }

    // ペアリングの証明: コードを鍵にして、相手の証明書のハッシュに HMAC をかける
    public static string PairingProof(string code, string certHash)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(code));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes("MKB-PAIR-v1:" + certHash.ToUpperInvariant())));
    }

    public static bool IsAllowedPeer(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address) || TvRemote.IsPrivateAddress(address);
    }

    public static async Task WriteLineAsync(Stream stream, object message, CancellationToken ct)
    {
        byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n");
        await stream.WriteAsync(data, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    // 1 行 (最大 4 KB) を読む: 大きすぎる入力は攻撃とみなして打ち切る
    public static async Task<JsonNode?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(256);
        var one = new byte[1];
        while (buffer.Count < MaxLineBytes)
        {
            int n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) return null;
            if (one[0] == (byte)'\n') return JsonNode.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
            buffer.Add(one[0]);
        }
        return null;
    }
}

// --- 送信側 ---
internal sealed class RemoteLink : IDisposable
{
    private readonly string _host;
    private readonly byte[] _token;
    private readonly string _certPin;

    private TcpClient? _tcp;
    private SslStream? _ssl;
    private CancellationTokenSource? _cts;
    private Channel<byte[]>? _queue;

    public string Host => _host;
    public bool Connected => _ssl != null;

    // 接続が切れた時 (入力先を PC に戻すため)
    public event Action<string>? Disconnected;

    public RemoteLink(string host, byte[] token, string certPin)
    {
        _host = host;
        _token = token;
        _certPin = certPin;
    }

    // null = 成功
    public async Task<string?> ConnectAsync()
    {
        Close();
        var cts = new CancellationTokenSource();
        try
        {
            (TcpClient tcp, SslStream ssl) = await OpenAsync(_host, cert => string.Equals(RemoteProtocol.CertHash(cert), _certPin, StringComparison.OrdinalIgnoreCase), cts.Token).ConfigureAwait(false);

            using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            authTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            await RemoteProtocol.WriteLineAsync(ssl, new { type = "auth", token = Convert.ToBase64String(_token) }, authTimeout.Token).ConfigureAwait(false);
            JsonNode? reply = await RemoteProtocol.ReadLineAsync(ssl, authTimeout.Token).ConfigureAwait(false);
            if (reply?["ok"]?.GetValue<bool>() != true)
            {
                ssl.Dispose();
                tcp.Dispose();
                return (string?)reply?["error"] ?? "Not authorized (pair again)";
            }

            _tcp = tcp;
            _ssl = ssl;
            _cts = cts;
            // 送信待ちが溜まりすぎたら (相手が止まっている) 切断する
            _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
            _ = Task.Run(() => WriterLoopAsync(ssl, _queue, cts.Token));
            _ = Task.Run(() => WatchCloseAsync(ssl, cts.Token));
            return null;
        }
        catch (AuthenticationException)
        {
            cts.Dispose();
            return "Certificate mismatch — pair again";
        }
        catch (Exception ex)
        {
            cts.Dispose();
            return ex is OperationCanceledException ? "Timed out" : ex.Message;
        }
    }

    public void SendKey(ushort vk, ushort scan, bool extended, bool up) =>
        Enqueue(RemoteProtocol.FrameKey, (byte)((up ? 1 : 0) | (extended ? 2 : 0)), vk, scan);

    public void ReleaseAll() => Enqueue(RemoteProtocol.FrameReleaseAll, 0, 0, 0);

    public void Lock() => Enqueue(RemoteProtocol.FrameLock, 0, 0, 0);

    private void Enqueue(byte type, byte flags, ushort vk, ushort scan)
    {
        Channel<byte[]>? queue = _queue;
        if (queue == null) return;
        byte[] frame = { type, flags, (byte)vk, (byte)(vk >> 8), (byte)scan, (byte)(scan >> 8) };
        if (!queue.Writer.TryWrite(frame)) Fail("Send queue full");
    }

    private async Task WriterLoopAsync(SslStream ssl, Channel<byte[]> queue, CancellationToken ct)
    {
        byte[] ping = { RemoteProtocol.FramePing, 0, 0, 0, 0, 0 };
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 15 秒ごとに生存確認 (受信側は 60 秒無通信で切断する)
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(15));
                byte[] frame;
                try
                {
                    frame = await queue.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    frame = ping;
                }
                await ssl.WriteAsync(frame, ct).ConfigureAwait(false);
                await ssl.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Fail(ex.Message);
        }
        catch { }
    }

    // 受信側から何か届くのは切断の時だけ
    private async Task WatchCloseAsync(SslStream ssl, CancellationToken ct)
    {
        var buffer = new byte[64];
        try
        {
            while (await ssl.ReadAsync(buffer, ct).ConfigureAwait(false) > 0) { }
            if (!ct.IsCancellationRequested) Fail("Connection closed");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Fail(ex.Message);
        }
        catch { }
    }

    private void Fail(string reason)
    {
        if (_ssl == null) return;
        Close();
        Disconnected?.Invoke(reason);
    }

    public void Close()
    {
        try { _cts?.Cancel(); } catch { }
        _queue?.Writer.TryComplete();
        try { _ssl?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _ssl = null;
        _tcp = null;
        _queue = null;
    }

    // 送信待ちのキー (特にキーを離す操作) を送ってから閉じる
    public async Task CloseGracefullyAsync()
    {
        Channel<byte[]>? queue = _queue;
        if (queue != null)
        {
            for (int i = 0; i < 20 && queue.Reader.Count > 0; i++) await Task.Delay(25).ConfigureAwait(false);
        }
        Close();
    }

    public void Dispose() => Close();

    private static async Task<(TcpClient, SslStream)> OpenAsync(string host, Func<X509Certificate, bool> validate, CancellationToken ct)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? ip) ? new[] { ip } : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        IPAddress? target = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork && RemoteProtocol.IsAllowedPeer(a));
        if (target == null) throw new InvalidOperationException("Only computers on the local network are allowed");

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(4));
            await tcp.ConnectAsync(target, RemoteProtocol.Port, connectTimeout.Token).ConfigureAwait(false);

            var ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errors) => cert != null && validate(cert));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "MagicKeyBattery",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, connectTimeout.Token).ConfigureAwait(false);
            return (tcp, ssl);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    // ペアリング: 受信側に表示されたコードで証明書を確認し、認証用の鍵を受け取る
    public static async Task<(byte[]? Token, string? CertPin, string? Error)> PairAsync(string host, string code)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(code.Trim(), "^[0-9]{6}$")) return (null, null, "Invalid code (6 digits)");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string? seenHash = null;
        try
        {
            (TcpClient tcp, SslStream ssl) = await OpenAsync(host, cert => { seenHash = RemoteProtocol.CertHash(cert); return true; }, cts.Token).ConfigureAwait(false);
            using (tcp)
            using (ssl)
            {
                string proof = RemoteProtocol.PairingProof(code.Trim(), seenHash!);
                await RemoteProtocol.WriteLineAsync(ssl, new { type = "pair", name = Environment.MachineName, proof }, cts.Token).ConfigureAwait(false);
                JsonNode? reply = await RemoteProtocol.ReadLineAsync(ssl, cts.Token).ConfigureAwait(false);
                if (reply?["ok"]?.GetValue<bool>() != true) return (null, null, (string?)reply?["error"] ?? "Pairing failed");

                byte[] token = Convert.FromBase64String((string?)reply["token"] ?? "");
                if (token.Length != 32) return (null, null, "Invalid response");
                return (token, seenHash, null);
            }
        }
        catch (Exception ex)
        {
            return (null, null, ex is OperationCanceledException ? "Timed out (is the receiver turned on?)" : ex.Message);
        }
    }
}

// キーボード変換の送り先としての「別の PC」
internal sealed class RemoteKeyOutput : IKeyOutput
{
    private readonly RemoteLink _link;
    private readonly HashSet<int> _modifiersDown = new();

    public RemoteKeyOutput(RemoteLink link) => _link = link;

    public void Key(ushort vk, ushort scan, bool extended, bool up)
    {
        // 相手の PC で修飾キーが押されているかを自分で追跡 (F1〜F12 のメディアキー判定用)
        int id = ModifierId(vk, scan, extended);
        if (id != 0)
        {
            lock (_modifiersDown)
            {
                if (up) _modifiersDown.Remove(id);
                else _modifiersDown.Add(id);
            }
        }
        _link.SendKey(vk, scan, extended, up);
    }

    public void Lock() => _link.Lock();

    public void Brightness(int delta) { } // 相手のモニターの明るさは対象外

    public bool AnyModifierDown()
    {
        lock (_modifiersDown) return _modifiersDown.Count > 0;
    }

    // フックのスレッドと UI スレッドの両方から呼ばれる
    public void ReleaseAll()
    {
        lock (_modifiersDown) _modifiersDown.Clear();
        _link.ReleaseAll();
    }

    private static int ModifierId(ushort vk, ushort scan, bool extended)
    {
        if (vk != 0)
        {
            return vk switch
            {
                0x10 or 0xA0 or 0xA1 => 1,               // Shift
                0x11 or 0xA2 or 0xA3 => extended ? 3 : 2, // Ctrl
                0x12 or 0xA4 or 0xA5 => extended ? 5 : 4, // Alt
                0x5B => 6,
                0x5C => 7,
                _ => 0
            };
        }
        return scan switch
        {
            0x2A => 1,
            0x36 => 8,
            0x1D => extended ? 3 : 2,
            0x38 => extended ? 5 : 4,
            0x5B when extended => 6,
            0x5C when extended => 7,
            _ => 0
        };
    }
}

// --- 受信側 (操作される PC) ---
internal static class RemoteReceiver
{
    private const string RegKey = @"SOFTWARE\MagicKeyBattery\Receiver";
    private static readonly string CertFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagicKeyBattery", "receiver-cert.bin");
    private static readonly byte[] CertEntropy = Encoding.UTF8.GetBytes("MagicKeyBattery.ReceiverCert.v1");
    private const int MaxConnections = 4;
    private const int MaxPairingAttempts = 5;

    private static TcpListener? _listener;
    private static CancellationTokenSource? _cts;
    private static X509Certificate2? _cert;
    private static int _connections;

    private static readonly object _pairLock = new();
    private static string? _pairingCode;
    private static DateTime _pairingExpires;
    private static int _pairingAttempts;

    public static bool Running => _listener != null;

    // 通常はすべてのネットワークで待ち受け (接続元は家庭内ネットワークに限定)。テスト時は 127.0.0.1 のみ
    internal static IPAddress BindAddress { get; set; } = IPAddress.Any;

    // UI への通知 (ペアリング成功・接続/切断)
    public static event Action<string>? Status;

    [DllImport("user32.dll")] private static extern bool LockWorkStation();

    public static string? Start()
    {
        if (_listener != null) return null;
        try
        {
            _cert ??= LoadOrCreateCertificate();
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(BindAddress, RemoteProtocol.Port);
            _listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
            return null;
        }
        catch (Exception ex)
        {
            Stop();
            return ex.Message;
        }
    }

    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
        lock (_pairLock) _pairingCode = null;
    }

    // 2 分間だけ有効なペアリングコード
    public static string NewPairingCode()
    {
        lock (_pairLock)
        {
            _pairingCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _pairingExpires = DateTime.UtcNow.AddMinutes(2);
            _pairingAttempts = 0;
            return _pairingCode;
        }
    }

    public static IReadOnlyList<string> PairedDeviceNames()
    {
        var names = new List<string>();
        foreach ((string name, _) in LoadClients()) names.Add(name);
        return names;
    }

    public static void RemoveAllPairedDevices()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKey);
        key.DeleteValue("Clients", false);
        // 接続中のものも切断するため再起動
        if (_listener != null)
        {
            Stop();
            Start();
        }
    }

    public static IEnumerable<string> LocalAddresses()
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (TvRemote.IsPrivateAddress(ua.Address)) yield return ua.Address.ToString();
            }
        }
    }

    private static async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            if (remote == null || !RemoteProtocol.IsAllowedPeer(remote.Address) || Interlocked.Increment(ref _connections) > MaxConnections)
            {
                if (remote != null && RemoteProtocol.IsAllowedPeer(remote.Address)) Interlocked.Decrement(ref _connections);
                client.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try { await HandleClientAsync(client, ct).ConfigureAwait(false); }
                catch (Exception ex) { Debug.WriteLine($"RemoteReceiver: {ex.Message}"); }
                finally
                {
                    client.Dispose();
                    Interlocked.Decrement(ref _connections);
                }
            });
        }
    }

    private static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        using var ssl = new SslStream(client.GetStream(), false);

        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            handshake.CancelAfter(TimeSpan.FromSeconds(10));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _cert,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, handshake.Token).ConfigureAwait(false);
        }

        JsonNode? hello;
        using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            hello = await RemoteProtocol.ReadLineAsync(ssl, readTimeout.Token).ConfigureAwait(false);
        }

        string? type = (string?)hello?["type"];
        if (type == "pair")
        {
            await HandlePairAsync(ssl, hello!, ct).ConfigureAwait(false);
            return;
        }
        if (type != "auth") return;

        string? clientName = Authenticate((string?)hello?["token"]);
        if (clientName == null)
        {
            await RemoteProtocol.WriteLineAsync(ssl, new { ok = false, error = "Not authorized (pair again)" }, ct).ConfigureAwait(false);
            return;
        }
        await RemoteProtocol.WriteLineAsync(ssl, new { ok = true }, ct).ConfigureAwait(false);
        Status?.Invoke($"⌨ {clientName}");

        await ReceiveKeysAsync(ssl, ct).ConfigureAwait(false);
    }

    private static async Task HandlePairAsync(SslStream ssl, JsonNode hello, CancellationToken ct)
    {
        string proof = (string?)hello["proof"] ?? "";
        string name = ((string?)hello["name"] ?? "PC").Trim();
        if (name.Length == 0 || name.Length > 64) name = "PC";

        string? error = null;
        lock (_pairLock)
        {
            if (_pairingCode == null || DateTime.UtcNow > _pairingExpires)
            {
                error = "No pairing code active on the receiver";
            }
            else
            {
                string expected = RemoteProtocol.PairingProof(_pairingCode, RemoteProtocol.CertHash(_cert!));
                bool match = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(proof.ToUpperInvariant()));
                if (match)
                {
                    _pairingCode = null; // 1 回だけ
                }
                else
                {
                    error = "Wrong code";
                    if (++_pairingAttempts >= MaxPairingAttempts) _pairingCode = null; // 総当たり対策
                }
            }
        }

        if (error != null)
        {
            await RemoteProtocol.WriteLineAsync(ssl, new { ok = false, error }, ct).ConfigureAwait(false);
            return;
        }

        byte[] token = RandomNumberGenerator.GetBytes(32);
        SaveClient(name, Convert.ToHexString(SHA256.HashData(token)));
        await RemoteProtocol.WriteLineAsync(ssl, new { ok = true, token = Convert.ToBase64String(token) }, ct).ConfigureAwait(false);
        Status?.Invoke($"✔ {name}");
    }

    private static async Task ReceiveKeysAsync(SslStream ssl, CancellationToken ct)
    {
        // この接続で押されたままのキー (切断時にすべて離す)
        var down = new HashSet<(ushort Vk, ushort Scan, bool Extended)>();
        var frame = new byte[RemoteProtocol.FrameSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                await ssl.ReadExactlyAsync(frame, idle.Token).ConfigureAwait(false);

                byte type = frame[0];
                bool up = (frame[1] & 1) != 0, extended = (frame[1] & 2) != 0;
                ushort vk = (ushort)(frame[2] | frame[3] << 8), scan = (ushort)(frame[4] | frame[5] << 8);

                switch (type)
                {
                    case RemoteProtocol.FrameKey:
                        if (vk > 0xFE || scan > 0x1FF || (frame[1] & ~3) != 0) return; // 不正な値は切断
                        KeyboardRemapper.InjectLocal(vk, scan, extended, up);
                        if (up) down.Remove((vk, scan, extended));
                        else down.Add((vk, scan, extended));
                        break;
                    case RemoteProtocol.FrameReleaseAll:
                        ReleaseAll(down);
                        break;
                    case RemoteProtocol.FramePing:
                        break;
                    case RemoteProtocol.FrameLock:
                        ReleaseAll(down);
                        LockWorkStation();
                        break;
                    default:
                        return;
                }
            }
        }
        catch { }
        finally
        {
            ReleaseAll(down);
        }
    }

    private static void ReleaseAll(HashSet<(ushort Vk, ushort Scan, bool Extended)> down)
    {
        foreach ((ushort vk, ushort scan, bool extended) in down) KeyboardRemapper.InjectLocal(vk, scan, extended, up: true);
        down.Clear();
    }

    // --- 認証情報 ---

    private static string? Authenticate(string? tokenBase64)
    {
        if (string.IsNullOrEmpty(tokenBase64)) return null;
        byte[] token;
        try { token = Convert.FromBase64String(tokenBase64); } catch { return null; }
        if (token.Length != 32) return null;

        byte[] hash = Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(token)));
        foreach ((string name, string storedHash) in LoadClients())
        {
            if (CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(storedHash.ToUpperInvariant()))) return name;
        }
        return null;
    }

    private static List<(string Name, string Hash)> LoadClients()
    {
        var clients = new List<(string, string)>();
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegKey);
        if (key?.GetValue("Clients") is string[] entries)
        {
            foreach (string entry in entries)
            {
                string[] parts = entry.Split('|');
                if (parts.Length >= 2 && parts[1].Length == 64) clients.Add((parts[0], parts[1]));
            }
        }
        return clients;
    }

    private static void SaveClient(string name, string hash)
    {
        var entries = new List<string>();
        foreach ((string n, string h) in LoadClients())
        {
            if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) entries.Add($"{n}|{h}");
        }
        entries.Add($"{name.Replace("|", "")}|{hash}|{DateTime.Now:yyyy-MM-dd}");
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKey);
        key.SetValue("Clients", entries.ToArray(), RegistryValueKind.MultiString);
    }

    // 受信側の証明書 (秘密鍵を含むので DPAPI で暗号化してファイルに保存)
    private static X509Certificate2 LoadOrCreateCertificate()
    {
        if (File.Exists(CertFile))
        {
            try
            {
                byte[] pfx = ProtectedData.Unprotect(File.ReadAllBytes(CertFile), CertEntropy, DataProtectionScope.CurrentUser);
                return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
            }
            catch { }
        }

        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=MagicKeyBattery Receiver", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        byte[] pfxBytes = created.Export(X509ContentType.Pfx);
        Directory.CreateDirectory(Path.GetDirectoryName(CertFile)!);
        File.WriteAllBytes(CertFile, ProtectedData.Protect(pfxBytes, CertEntropy, DataProtectionScope.CurrentUser));
        // SChannel (Windows の TLS) は一時的な鍵を使えないため、PFX から読み込み直す
        return X509CertificateLoader.LoadPkcs12(pfxBytes, null, X509KeyStorageFlags.UserKeySet);
    }
}
