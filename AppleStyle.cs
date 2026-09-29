using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MagicKeyBattery;

// macOS 風の見た目 (メニューバーの電池アイコン、モノクロの線アイコン、角丸メニュー)
// Windows のライト/ダークに合わせて色を切り替える
internal static class AppleTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // タスクバー (トレイアイコンの背景) がダークか
    public static bool TaskbarDark => ReadDword("SystemUsesLightTheme", 0) == 0;

    // アプリ (メニュー) がダークか
    public static bool AppsDark => ReadDword("AppsUseLightTheme", 1) == 0;

    // macOS のシステムカラー
    public static Color Accent(bool dark) => dark ? Color.FromArgb(10, 132, 255) : Color.FromArgb(0, 122, 255);
    public static Color Red(bool dark) => dark ? Color.FromArgb(255, 69, 58) : Color.FromArgb(255, 59, 48);
    public static Color Green(bool dark) => dark ? Color.FromArgb(48, 209, 88) : Color.FromArgb(52, 199, 89);

    public static Color MenuBackground(bool dark) => dark ? Color.FromArgb(44, 44, 46) : Color.FromArgb(246, 246, 246);
    public static Color MenuBorder(bool dark) => dark ? Color.FromArgb(72, 72, 74) : Color.FromArgb(209, 209, 214);
    public static Color MenuText(bool dark) => dark ? Color.FromArgb(242, 242, 247) : Color.FromArgb(28, 28, 30);
    public static Color MenuSecondary(bool dark) => dark ? Color.FromArgb(152, 152, 157) : Color.FromArgb(128, 128, 133);
    public static Color MenuSeparator(bool dark) => dark ? Color.FromArgb(62, 62, 64) : Color.FromArgb(222, 222, 226);

    private static int ReadDword(string name, int fallback)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        if (d <= 0.01f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal enum BatteryGlyph { Level, NotConnected, Unknown }

// macOS のメニューバーと同じ形の電池 (細い角丸の枠 + 残量の塗り + 充電中の稲妻)
internal static class BatteryIcon
{
    // 座標は 16x16 基準。size に合わせて拡大して描く
    public static Bitmap Render(int size, BatteryGlyph glyph, int level, bool charging, bool darkBackground)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float s = size / 16f;
        g.ScaleTransform(s, s);

        Color fg = darkBackground ? Color.White : Color.Black;
        bool dim = glyph != BatteryGlyph.Level;
        Color frameColor = Color.FromArgb(dim ? 110 : 190, fg);

        // 本体と端子
        var body = new RectangleF(0.9f, 4.1f, 12.4f, 7.8f);
        using (var framePath = AppleTheme.RoundedRect(body, 2.4f))
        using (var framePen = new Pen(frameColor, 1.15f))
        {
            g.DrawPath(framePen, framePath);
        }
        using (var nub = AppleTheme.RoundedRect(new RectangleF(13.9f, 6.5f, 1.4f, 3.0f), 0.7f))
        using (var nubBrush = new SolidBrush(Color.FromArgb(dim ? 90 : 150, fg)))
        {
            g.FillPath(nubBrush, nub);
        }

        if (glyph == BatteryGlyph.Level)
        {
            // 残量: 20% 以下は赤、充電中は緑、それ以外は前景色
            Color fillColor = charging ? AppleTheme.Green(darkBackground)
                            : level <= 20 ? AppleTheme.Red(darkBackground)
                            : fg;
            float maxWidth = body.Width - 3.2f;
            float width = Math.Max(1.2f, maxWidth * Math.Clamp(level, 0, 100) / 100f);
            using var fillPath = AppleTheme.RoundedRect(new RectangleF(body.Left + 1.6f, body.Top + 1.6f, width, body.Height - 3.2f), 1.1f);
            using var fillBrush = new SolidBrush(fillColor);
            g.FillPath(fillBrush, fillPath);
        }
        else if (glyph == BatteryGlyph.NotConnected)
        {
            // 未接続: 斜線 (背景を切り抜いてから線を描く)
            using var cut = new Pen(Color.Transparent, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawLine(cut, 3.2f, 13.6f, 12.0f, 2.4f);
            g.CompositingMode = CompositingMode.SourceOver;
            using var slash = new Pen(Color.FromArgb(200, fg), 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(slash, 3.2f, 13.6f, 12.0f, 2.4f);
        }
        else
        {
            // 残量不明: 中央に小さな「?」
            using var font = new Font("Segoe UI Semibold", 7.2f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.FromArgb(200, fg));
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("?", font, brush, new RectangleF(body.Left, body.Top - 0.4f, body.Width, body.Height), format);
        }

        if (charging)
        {
            // 稲妻: 周りを切り抜いて (macOS と同じ)、前景色で塗る
            PointF[] bolt = { new(8.4f, 2.6f), new(4.6f, 8.6f), new(7.3f, 8.6f), new(6.4f, 13.4f), new(10.4f, 7.2f), new(7.7f, 7.2f), new(8.9f, 2.6f) };
            using var boltPath = new GraphicsPath();
            boltPath.AddPolygon(bolt);
            using (var cut = new Pen(Color.Transparent, 2.2f) { LineJoin = LineJoin.Round })
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawPath(cut, boltPath);
                g.CompositingMode = CompositingMode.SourceOver;
            }
            using var boltBrush = new SolidBrush(fg);
            g.FillPath(boltBrush, boltPath);
        }

        return bmp;
    }

    // Bitmap → トレイ用の Icon (ハンドルを所有する複製を返す)
    public static Icon ToIcon(Bitmap bmp)
    {
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}

internal enum TrayMenuGlyph { Key, Keyboard, Display, Chart, Gear, Power }

// SF Symbols 風のモノクロ線アイコン (16x16 基準)
internal static class MenuIcons
{
    public static Bitmap Render(TrayMenuGlyph glyph, int size, Color color)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float s = size / 16f;
        g.ScaleTransform(s, s);

        using var pen = new Pen(color, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);

        switch (glyph)
        {
            case TrayMenuGlyph.Key:
                g.DrawEllipse(pen, 1.8f, 5.2f, 5.6f, 5.6f);
                g.DrawLine(pen, 7.4f, 8f, 14.2f, 8f);
                g.DrawLine(pen, 12.2f, 8f, 12.2f, 10.4f);
                g.DrawLine(pen, 14.2f, 8f, 14.2f, 10.8f);
                break;

            case TrayMenuGlyph.Keyboard:
                using (var outline = AppleTheme.RoundedRect(new RectangleF(1.2f, 3.8f, 13.6f, 8.6f), 1.8f)) g.DrawPath(pen, outline);
                for (int row = 0; row < 2; row++)
                    for (int col = 0; col < 5; col++)
                        g.FillEllipse(brush, 3.2f + col * 2.4f - 0.55f, 5.9f + row * 2.1f - 0.55f, 1.1f, 1.1f);
                g.DrawLine(pen, 5.2f, 10.3f, 10.8f, 10.3f);
                break;

            case TrayMenuGlyph.Display:
                using (var screen = AppleTheme.RoundedRect(new RectangleF(1.2f, 2.4f, 13.6f, 9f), 1.6f)) g.DrawPath(pen, screen);
                g.DrawLine(pen, 8f, 11.4f, 8f, 13.4f);
                g.DrawLine(pen, 5.2f, 13.6f, 10.8f, 13.6f);
                break;

            case TrayMenuGlyph.Chart:
                g.DrawLines(pen, new[] { new PointF(1.8f, 12.8f), new PointF(5.4f, 8.2f), new PointF(8.4f, 10.4f), new PointF(12.2f, 4.6f), new PointF(14.2f, 6.2f) });
                g.DrawLine(pen, 1.6f, 14.4f, 14.4f, 14.4f);
                break;

            case TrayMenuGlyph.Gear:
                DrawGear(g, pen);
                break;

            case TrayMenuGlyph.Power:
                g.DrawArc(pen, 2.2f, 2.8f, 11.6f, 11.6f, -50, 280);
                g.DrawLine(pen, 8f, 1.6f, 8f, 7.6f);
                break;
        }
        return bmp;
    }

    private static void DrawGear(Graphics g, Pen pen)
    {
        // 8 枚の歯を持つ歯車の輪郭
        const int teeth = 8;
        var points = new PointF[teeth * 4];
        for (int i = 0; i < teeth; i++)
        {
            double a = i * Math.PI * 2 / teeth;
            double w = Math.PI / teeth * 0.55;
            points[i * 4 + 0] = Polar(a - w - 0.18, 5.4f);
            points[i * 4 + 1] = Polar(a - w, 6.9f);
            points[i * 4 + 2] = Polar(a + w, 6.9f);
            points[i * 4 + 3] = Polar(a + w + 0.18, 5.4f);
        }
        g.DrawPolygon(pen, points);
        g.DrawEllipse(pen, 8f - 2.2f, 8f - 2.2f, 4.4f, 4.4f);

        static PointF Polar(double angle, float radius) => new(8f + radius * (float)Math.Cos(angle), 8f + radius * (float)Math.Sin(angle));
    }
}

// macOS 風のメニュー: 角丸、細い区切り線、選択行は青い角丸のハイライト、チェックは ✓ だけ
internal sealed class AppleMenuRenderer : ToolStripRenderer
{
    private readonly bool _dark;

    public AppleMenuRenderer(bool dark) => _dark = dark;

    protected override void Initialize(ToolStrip toolStrip)
    {
        base.Initialize(toolStrip);
        toolStrip.BackColor = AppleTheme.MenuBackground(_dark);
        toolStrip.ForeColor = AppleTheme.MenuText(_dark);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(AppleTheme.MenuBackground(_dark));
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // 角丸は DWM が描くので、枠線だけ控えめに
        using var pen = new Pen(AppleTheme.MenuBorder(_dark));
        Rectangle r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { } // Windows の灰色の帯は描かない

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float inset = e.Item.Owner?.LogicalToDeviceUnits(5) ?? 5;
        var r = new RectangleF(inset, 1, e.Item.Width - inset * 2, e.Item.Height - 2);
        using var path = AppleTheme.RoundedRect(r, inset);
        using var brush = new SolidBrush(AppleTheme.Accent(_dark));
        g.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? AppleTheme.MenuSecondary(_dark)
                    : e.Item.Selected ? Color.White
                    : AppleTheme.MenuText(_dark);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image == null) return;
        // 選択中は白いアイコンで描く (青いハイライトの上でも見えるように): 透明度はそのまま、色だけ白に
        if (e.Item.Selected && e.Item.Enabled)
        {
            var toWhite = new System.Drawing.Imaging.ColorMatrix(new[]
            {
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 },
                new float[] { 1, 1, 1, 0, 1 }
            });
            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetColorMatrix(toWhite);
            Rectangle r = e.ImageRectangle;
            e.Graphics.DrawImage(e.Image, r, 0, 0, e.Image.Width, e.Image.Height, GraphicsUnit.Pixel, attributes);
            return;
        }
        e.Graphics.DrawImage(e.Image, e.ImageRectangle);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // 枠なしの ✓ だけ
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = e.ImageRectangle;
        Color color = e.Item.Selected ? Color.White : AppleTheme.MenuText(_dark);
        float s = r.Width / 16f;
        using var pen = new Pen(color, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(r.X + 3.2f * s, r.Y + 8.4f * s),
            new PointF(r.X + 6.6f * s, r.Y + 11.6f * s),
            new PointF(r.X + 12.8f * s, r.Y + 4.6f * s)
        });
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int inset = e.Item.Owner?.LogicalToDeviceUnits(12) ?? 12;
        int y = e.Item.Height / 2;
        using var pen = new Pen(AppleTheme.MenuSeparator(_dark));
        e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
    }

    // Windows 11: メニューのウィンドウを角丸にする (DWM が滑らかに描く)
    public static void ApplyRoundedCorners(ToolStripDropDown menu)
    {
        menu.HandleCreated += (s, e) =>
        {
            int preference = 2; // DWMWCP_ROUND
            try { DwmSetWindowAttribute(menu.Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref preference, sizeof(int)); }
            catch { } // Windows 10 では無視
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
