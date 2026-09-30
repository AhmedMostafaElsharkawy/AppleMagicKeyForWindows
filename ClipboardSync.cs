using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace MagicKeyBattery;

// 別の PC (F15) とクリップボードのテキストを共有する
//   ・UI スレッドで動かす (クリップボードは STA スレッドからしか触れない)
//   ・パスワードマネージャーが付ける「共有しない」印 (ExcludeClipboardContentFromMonitorProcessing) の付いた内容は送らない
//   ・相手から受け取った内容をそのまま送り返さない (ハッシュで判定)
internal static class ClipboardSync
{
    public const int MaxBytes = 1024 * 1024; // 1 MB を超えるテキストは共有しない

    // ローカルのクリップボードのテキストが変わった時 (共有してよい内容のみ)
    public static event Action<string>? LocalTextChanged;

    public static bool Enabled { get; set; } = true;

    private static ClipboardWindow? _window;
    private static byte[]? _lastRemoteHash;

    public static void Start()
    {
        if (_window != null) return;
        _window = new ClipboardWindow();
    }

    public static void Stop()
    {
        _window?.Dispose();
        _window = null;
    }

    // 共有してよい現在のテキスト (無ければ null)
    public static string? ReadShareableText()
    {
        try
        {
            IDataObject? data = Clipboard.GetDataObject();
            if (data == null) return null;
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return null; // パスワード等
            if (!data.GetDataPresent(DataFormats.UnicodeText)) return null;
            string? text = data.GetData(DataFormats.UnicodeText) as string;
            if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) > MaxBytes) return null;
            return text;
        }
        catch (ExternalException)
        {
            return null; // 他のアプリがクリップボードを開いている
        }
    }

    // 相手から受け取ったテキストをこの PC のクリップボードに入れる
    public static void SetFromRemote(string text)
    {
        if (!Enabled || string.IsNullOrEmpty(text)) return;
        _lastRemoteHash = Hash(text);
        try
        {
            Clipboard.SetDataObject(text, true, 5, 100);
        }
        catch (ExternalException) { }
    }

    private static void OnClipboardUpdate()
    {
        if (!Enabled) return;
        string? text = ReadShareableText();
        if (text == null) return;

        // 相手から来た内容の反映による通知なら送り返さない
        byte[] hash = Hash(text);
        if (_lastRemoteHash != null && CryptographicOperations.FixedTimeEquals(hash, _lastRemoteHash)) return;

        LocalTextChanged?.Invoke(text);
    }

    private static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    // WM_CLIPBOARDUPDATE を受け取るための見えないウィンドウ
    private sealed class ClipboardWindow : NativeWindow, IDisposable
    {
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private static readonly IntPtr HWND_MESSAGE = new(-3);

        [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        public ClipboardWindow()
        {
            CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
            AddClipboardFormatListener(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE) OnClipboardUpdate();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(Handle);
                DestroyHandle();
            }
        }
    }
}
