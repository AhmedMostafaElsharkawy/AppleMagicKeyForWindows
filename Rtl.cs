using System.Windows.Forms;

namespace MagicKeyBattery;

// アラビア語など右から左に書く言語の画面対応
internal static class Rtl
{
    // 現在の言語が RTL か (Program が言語を読み込んだ/変更した時に設定)
    public static bool Enabled { get; set; }

    public static MessageBoxOptions MessageBoxOptions =>
        Enabled ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0;

    // 画面を右から左にする (コントロールを追加し終わった後に呼ぶ)
    //   ・RightToLeft は各コントロールに明示的に設定する: 親から継承しただけだと、ボタンなどで
    //     アラビア文字の下側が欠けて描画される (実機で確認)
    //   ・絶対座標で並べたコントロールは左右反転する。TableLayoutPanel / FlowLayoutPanel の中は
    //     パネル自身が並びを反転するので座標は触らない。Dock しているコントロールもそのまま
    public static void Apply(Control root)
    {
        if (!Enabled) return;
        root.RightToLeft = RightToLeft.Yes;
        Walk(root);
    }

    private static void Walk(Control parent)
    {
        bool layoutPanel = parent is TableLayoutPanel || parent is FlowLayoutPanel;
        int width = parent.ClientSize.Width;

        foreach (Control child in parent.Controls)
        {
            if (!layoutPanel && child.Dock == DockStyle.None)
                child.Left = width - child.Left - child.Width;

            // IP アドレス・コードなどの入力欄は左から右のまま
            // スクロールするパネル自体を RTL にすると不要な横スクロールバーが出るので、パネルは LTR (中身は RTL)
            child.RightToLeft = child is TextBox || child is ScrollableControl { AutoScroll: true } ? RightToLeft.No : RightToLeft.Yes;

            // ボタン・チェックボックス・ラジオボタンの GDI 描画 (TextRenderer) はアラビア文字の下側を欠けさせるため
            // GDI+ で描く (実機で確認)。ラベルは GDI のままで問題ない (GDI+ は行間が広く複数行の説明がはみ出る)
            // ※ GDI+ は絵文字を描けないので、ボタン類のアラビア語の文字列には絵文字を入れないこと
            if (child is ButtonBase button) button.UseCompatibleTextRendering = true;

            if (child.HasChildren) Walk(child);
        }
    }
}
