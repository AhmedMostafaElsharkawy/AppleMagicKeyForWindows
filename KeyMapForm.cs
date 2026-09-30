using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MagicKeyBattery;

// 「キー配置」: 各キーの動作を選ぶ画面
internal sealed class KeyMapForm : Form
{
    private readonly Func<string, string> _t;
    private readonly Action<Dictionary<uint, KeyAction>> _save;
    private readonly Dictionary<uint, ComboBox> _combos = new();

    // コンボボックスの項目 (表示名と動作)
    private sealed record ActionItem(KeyAction Action, string Text)
    {
        public override string ToString() => Text;
    }

    public KeyMapForm(Func<string, string> translate, IReadOnlyDictionary<uint, KeyAction> current, Action<Dictionary<uint, KeyAction>> save)
    {
        _t = translate;
        _save = save;

        // レイアウトを止めてから作る: 拡大率に合わせた拡大 (AutoScale) を完成した画面に対して 1 回だけ行うため
        SuspendLayout();

        Text = _t("keymap_title");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        // 96 DPI (100%) で設計したレイアウトを画面の拡大率に合わせて拡大する
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 640);
        BackColor = HistoryForm.Surface;
        ForeColor = HistoryForm.TextPrimary;
        Font = new Font("Segoe UI", 9f);
        ShowIcon = false;

        var hint = new Label
        {
            Text = _t("keymap_hint"), Left = 16, Top = 10, Width = 448, Height = 70, ForeColor = HistoryForm.TextMuted
        };

        // キーの一覧 (スクロール)
        var list = new Panel { Left = 0, Top = 84, Width = 480, Height = 496, AutoScroll = true, BackColor = HistoryForm.Surface };
        int y = 4;
        foreach ((KeyGroup group, string header) in new[]
        {
            (KeyGroup.TopRow, "keymap_section_top"),
            (KeyGroup.Extra, "keymap_section_extra"),
            (KeyGroup.Modifier, "keymap_section_mods")
        })
        {
            list.Controls.Add(new Label
            {
                Text = _t(header), Left = 16, Top = y, AutoSize = true, ForeColor = HistoryForm.TextPrimary,
                Font = new Font("Segoe UI Semibold", 10f)
            });
            y += 30;

            foreach (PhysicalKey key in KeyMapping.Keys.Where(k => k.Group == group))
            {
                string name = key.NameKey.Length > 0 ? _t(key.NameKey) : key.Label;
                list.Controls.Add(new Label { Text = name, Left = 28, Top = y + 3, Width = 170, Height = 24, ForeColor = HistoryForm.TextSecondary });

                var combo = new ComboBox
                {
                    Left = 204, Top = y, Width = 230, DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = HistoryForm.SurfaceRaised, ForeColor = HistoryForm.TextPrimary, FlatStyle = FlatStyle.Flat
                };
                foreach (KeyAction action in KeyMapping.ActionsFor(group))
                    combo.Items.Add(new ActionItem(action, ActionName(action, group)));
                Select(combo, current.TryGetValue(key.Vk, out KeyAction value) ? value : KeyAction.Default);

                _combos[key.Vk] = combo;
                list.Controls.Add(combo);
                y += 32;
            }
            y += 10;
        }
        // スクロール範囲の下の余白 (左右反転しても横スクロールが出ない位置に置く)
        list.Controls.Add(new Label { Left = 200, Top = y, Height = 8, Width = 1 });

        // ボタン
        var btnReset = MakeButton(_t("keymap_reset"), 16, 596, 190);
        var btnCancel = MakeButton(_t("btn_cancel"), 280, 596, 90);
        var btnSave = MakeButton(_t("btn_save"), 374, 596, 90);
        btnSave.BackColor = AppleTheme.Accent(true);
        btnSave.FlatAppearance.BorderColor = AppleTheme.Accent(true);

        btnReset.Click += (s, e) =>
        {
            Dictionary<uint, KeyAction> defaults = KeyMapping.Defaults();
            foreach ((uint vk, ComboBox combo) in _combos) Select(combo, defaults.TryGetValue(vk, out KeyAction a) ? a : KeyAction.Default);
        };
        btnCancel.Click += (s, e) => Close();
        btnSave.Click += (s, e) =>
        {
            var map = new Dictionary<uint, KeyAction>();
            foreach ((uint vk, ComboBox combo) in _combos)
                map[vk] = combo.SelectedItem is ActionItem item ? item.Action : KeyAction.Default;
            _save(map);
            Close();
        };

        AcceptButton = btnSave;
        CancelButton = btnCancel;
        Controls.AddRange(new Control[] { hint, list, btnReset, btnCancel, btnSave });
        Rtl.Apply(this);

        ResumeLayout(false);
        PerformLayout();
    }

    private string ActionName(KeyAction action, KeyGroup group) =>
        action == KeyAction.Default && group == KeyGroup.Modifier ? _t("act_ModDefault") : _t("act_" + action);

    private static void Select(ComboBox combo, KeyAction action)
    {
        foreach (ActionItem item in combo.Items)
        {
            if (item.Action == action)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static Button MakeButton(string text, int left, int top, int width)
    {
        var btn = new Button
        {
            Text = text, Left = left, Top = top, Width = width, Height = 30,
            FlatStyle = FlatStyle.Flat, BackColor = HistoryForm.SurfaceRaised, ForeColor = HistoryForm.TextPrimary, Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderColor = HistoryForm.GridLine;
        return btn;
    }
}
