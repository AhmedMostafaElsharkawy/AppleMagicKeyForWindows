using System;
using System.Collections.Generic;
using System.Linq;

namespace MagicKeyBattery;

// 物理キー → 送るキー (vk, scan, extended)
internal readonly record struct KeyOut(ushort Vk, ushort Scan, bool Extended);

// キーに割り当てられる動作
// ※ 名前はレジストリに保存されるので変更しないこと (追加は末尾に)
internal enum KeyAction
{
    Default,            // 変更しない (通常のキー)
    Disabled,           // 何もしない

    // 1 回だけ実行する操作
    BrightnessDown, BrightnessUp,
    TaskView, StartMenu, ShowDesktop, Search, FileExplorer, WindowsSettings, NotificationCenter,
    EmojiPanel, Dictation, ScreenSnip, LockPc,
    SwitchToPc, SwitchToTv, SwitchToF15,

    // 押している間そのキーとして送るもの (キーリピートもそのまま効く)
    Calculator, PrintScreen, ContextMenu,
    MediaPrevious, MediaPlayPause, MediaNext, VolumeMute, VolumeDown, VolumeUp,
    BrowserBack, BrowserForward,

    // 修飾キー
    ModWin, ModAlt, ModAltGr, ModCtrl, ModRightCtrl, ModShift
}

internal enum KeyGroup { TopRow, Extra, Modifier }

// 設定できる物理キー
internal readonly record struct PhysicalKey(uint Vk, KeyGroup Group, string NameKey, string Label);

internal static class KeyMapping
{
    public static readonly PhysicalKey[] Keys = BuildKeys();

    private static PhysicalKey[] BuildKeys()
    {
        var keys = new List<PhysicalKey>();
        for (int i = 1; i <= 12; i++) keys.Add(new PhysicalKey((uint)(0x6F + i), KeyGroup.TopRow, "", $"F{i}"));
        for (int i = 13; i <= 19; i++) keys.Add(new PhysicalKey((uint)(0x6F + i), KeyGroup.Extra, "", $"F{i}"));
        keys.Add(new PhysicalKey(0xA2, KeyGroup.Modifier, "key_lctrl", ""));
        keys.Add(new PhysicalKey(0xA4, KeyGroup.Modifier, "key_loption", ""));
        keys.Add(new PhysicalKey(0x5B, KeyGroup.Modifier, "key_lcommand", ""));
        keys.Add(new PhysicalKey(0x5C, KeyGroup.Modifier, "key_rcommand", ""));
        keys.Add(new PhysicalKey(0xA5, KeyGroup.Modifier, "key_roption", ""));
        keys.Add(new PhysicalKey(0x14, KeyGroup.Modifier, "key_caps", ""));
        return keys.ToArray();
    }

    // Logitech MX Keys (Windows) と同じ配置 + Magic Keyboard の F キーのアイコン
    public static Dictionary<uint, KeyAction> Defaults() => new()
    {
        [0x70] = KeyAction.BrightnessDown,   // F1
        [0x71] = KeyAction.Default,          // F2 (名前の変更でよく使うので通常のまま)
        [0x72] = KeyAction.TaskView,         // F3
        [0x73] = KeyAction.StartMenu,        // F4
        [0x74] = KeyAction.Default,          // F5
        [0x75] = KeyAction.Default,          // F6
        [0x76] = KeyAction.MediaPrevious,    // F7
        [0x77] = KeyAction.MediaPlayPause,   // F8
        [0x78] = KeyAction.MediaNext,        // F9
        [0x79] = KeyAction.VolumeMute,       // F10
        [0x7A] = KeyAction.VolumeDown,       // F11
        [0x7B] = KeyAction.VolumeUp,         // F12
        [0x7C] = KeyAction.SwitchToPc,       // F13
        [0x7D] = KeyAction.SwitchToTv,       // F14
        [0x7E] = KeyAction.SwitchToF15,      // F15
        [0x7F] = KeyAction.Calculator,       // F16
        [0x80] = KeyAction.PrintScreen,      // F17
        [0x81] = KeyAction.ContextMenu,      // F18
        [0x82] = KeyAction.LockPc,           // F19
        [0xA2] = KeyAction.Default,          // control
        [0xA4] = KeyAction.ModWin,           // 左 option  → Win
        [0x5B] = KeyAction.ModAlt,           // 左 command → Alt
        [0x5C] = KeyAction.ModAltGr,         // 右 command → AltGr
        [0xA5] = KeyAction.ModRightCtrl,     // 右 option  → 右 Ctrl
        [0x14] = KeyAction.Default           // caps lock
    };

    public static bool IsModifierAction(KeyAction a) => a >= KeyAction.ModWin;
    public static bool IsSwitch(KeyAction a) => a is KeyAction.SwitchToPc or KeyAction.SwitchToTv or KeyAction.SwitchToF15;
    public static bool IsRepeatable(KeyAction a) => a is KeyAction.BrightnessDown or KeyAction.BrightnessUp;

    // 押しっぱなしでキーリピートさせてよいもの (音量・修飾キー)。
    // 再生/一時停止や電卓などは繰り返すと何度も切り替わる/起動するので最初の 1 回だけ
    public static bool RepeatsWhileHeld(KeyAction a) => a is KeyAction.VolumeDown or KeyAction.VolumeUp || IsModifierAction(a);

    // キーの種類ごとに選べる動作
    public static IReadOnlyList<KeyAction> ActionsFor(KeyGroup group)
    {
        if (group == KeyGroup.Modifier)
        {
            return new[] { KeyAction.Default, KeyAction.ModWin, KeyAction.ModAlt, KeyAction.ModAltGr, KeyAction.ModCtrl, KeyAction.ModRightCtrl, KeyAction.ModShift, KeyAction.Disabled };
        }
        return Enum.GetValues<KeyAction>().Where(a => !IsModifierAction(a)).ToArray();
    }

    // 押している間そのキーとして送る動作の出力
    public static bool TryGetHeldOutput(KeyAction action, out KeyOut output)
    {
        output = action switch
        {
            KeyAction.Calculator => new KeyOut(0xB7, 0, true),         // VK_LAUNCH_APP2
            KeyAction.PrintScreen => new KeyOut(0x2C, 0x37, true),
            KeyAction.ContextMenu => new KeyOut(0x5D, 0x5D, true),
            KeyAction.MediaPrevious => new KeyOut(0xB1, 0, true),
            KeyAction.MediaPlayPause => new KeyOut(0xB3, 0, true),
            KeyAction.MediaNext => new KeyOut(0xB0, 0, true),
            KeyAction.VolumeMute => new KeyOut(0xAD, 0, true),
            KeyAction.VolumeDown => new KeyOut(0xAE, 0, true),
            KeyAction.VolumeUp => new KeyOut(0xAF, 0, true),
            KeyAction.BrowserBack => new KeyOut(0xA6, 0, true),
            KeyAction.BrowserForward => new KeyOut(0xA7, 0, true),
            KeyAction.ModWin => new KeyOut(0x5B, 0x5B, true),
            KeyAction.ModAlt => new KeyOut(0xA4, 0x38, false),
            KeyAction.ModAltGr => new KeyOut(0xA5, 0x38, true),
            KeyAction.ModCtrl => new KeyOut(0xA2, 0x1D, false),
            KeyAction.ModRightCtrl => new KeyOut(0xA3, 0x1D, true),
            KeyAction.ModShift => new KeyOut(0xA0, 0x2A, false),
            _ => default
        };
        return output.Vk != 0;
    }

    // 1 回だけ実行する操作のショートカット (Win + キー)。null = ショートカットではない
    public static KeyOut[]? ChordFor(KeyAction action)
    {
        var win = new KeyOut(0x5B, 0x5B, true);
        return action switch
        {
            KeyAction.TaskView => new[] { win, new KeyOut(0x09, 0x0F, false) },            // Win+Tab
            KeyAction.StartMenu => new[] { win },
            KeyAction.ShowDesktop => new[] { win, new KeyOut(0x44, 0x20, false) },         // Win+D
            KeyAction.Search => new[] { win, new KeyOut(0x53, 0x1F, false) },              // Win+S
            KeyAction.FileExplorer => new[] { win, new KeyOut(0x45, 0x12, false) },        // Win+E
            KeyAction.WindowsSettings => new[] { win, new KeyOut(0x49, 0x17, false) },     // Win+I
            KeyAction.NotificationCenter => new[] { win, new KeyOut(0x4E, 0x31, false) },  // Win+N
            KeyAction.EmojiPanel => new[] { win, new KeyOut(0xBE, 0x34, false) },          // Win+.
            KeyAction.Dictation => new[] { win, new KeyOut(0x48, 0x23, false) },           // Win+H
            KeyAction.ScreenSnip => new[] { win, new KeyOut(0xA0, 0x2A, false), new KeyOut(0x53, 0x1F, false) }, // Win+Shift+S
            _ => null
        };
    }

    // --- 保存形式: "70=BrightnessDown;71=Default;..." ---

    public static string Serialize(IReadOnlyDictionary<uint, KeyAction> map) =>
        string.Join(";", map.OrderBy(p => p.Key).Select(p => $"{p.Key:X2}={p.Value}"));

    // 不明なキー・その種類で使えない動作は無視して初期値のまま
    public static Dictionary<uint, KeyAction> Parse(string? text)
    {
        Dictionary<uint, KeyAction> map = Defaults();
        if (string.IsNullOrWhiteSpace(text)) return map;

        foreach (string entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split('=');
            if (parts.Length != 2) continue;
            if (!uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out uint vk)) continue;
            if (!Enum.TryParse(parts[1], out KeyAction action) || !Enum.IsDefined(action)) continue;

            PhysicalKey? key = Array.Find(Keys, k => k.Vk == vk) is { Vk: > 0 } found ? found : null;
            if (key == null || !ActionsFor(key.Value.Group).Contains(action)) continue;
            map[vk] = action;
        }
        return map;
    }
}
