using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace MagicKeyBattery;

// 設定・履歴・証明書の保存場所
//   exe の隣の「MagicKeyBattery-data」フォルダー (フォルダーごとコピーすれば設定も一緒に持ち運べる)
//   exe の場所に書き込めない場合 (Program Files など) は %LOCALAPPDATA%\MagicKeyBattery
internal static class AppStorage
{
    public const string DataFolderName = "MagicKeyBattery-data";

    // 旧バージョンの保存場所 (初回起動時にここから移行する)
    public static readonly string LegacyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagicKeyBattery");
    public const string LegacyRegistryKey = @"SOFTWARE\MagicKeyBattery";

    public static string DataDir { get; }
    public static bool IsPortable { get; }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string HistoryFile => Path.Combine(DataDir, "history.csv");
    public static string ReceiverCertFile => Path.Combine(DataDir, "receiver-cert.bin");

    static AppStorage()
    {
        string besideExe = Path.Combine(AppContext.BaseDirectory, DataFolderName);
        if (CanWrite(besideExe))
        {
            DataDir = besideExe;
            IsPortable = true;
        }
        else
        {
            DataDir = LegacyDir;
            IsPortable = false;
            Directory.CreateDirectory(DataDir);
        }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // 旧バージョンの履歴・証明書を新しい場所にコピー (新しい場所に無い時だけ。元は残す)
    public static void MigrateLegacyFiles()
    {
        if (string.Equals(Path.GetFullPath(DataDir).TrimEnd('\\'), Path.GetFullPath(LegacyDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        foreach (string name in new[] { "history.csv", "history.old.csv", "receiver-cert.bin" })
        {
            try
            {
                string from = Path.Combine(LegacyDir, name), to = Path.Combine(DataDir, name);
                if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
            }
            catch { }
        }
    }
}

// 設定の保存 (settings.json)。値は文字列・数値・文字列の配列
//   書き込みは一時ファイル → 置き換え (途中で落ちても壊れない)
//   初回は旧バージョンのレジストリから読み込んで移行する
internal static class SettingsStore
{
    private static readonly object _lock = new();
    private static JsonObject _values = new();
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (File.Exists(AppStorage.SettingsFile))
            {
                _values = JsonNode.Parse(File.ReadAllText(AppStorage.SettingsFile, Encoding.UTF8)) as JsonObject ?? new JsonObject();
                return;
            }
        }
        catch
        {
            // 壊れていたら退避して、初期値から始める
            try { File.Copy(AppStorage.SettingsFile, AppStorage.SettingsFile + ".broken", true); } catch { }
            _values = new JsonObject();
            return;
        }

        // settings.json がまだ無い → 旧バージョンのレジストリから移行
        MigrateFromRegistry();
        AppStorage.MigrateLegacyFiles();
        SaveLocked();
    }

    private static void MigrateFromRegistry()
    {
        try
        {
            using RegistryKey? settings = Registry.CurrentUser.OpenSubKey(AppStorage.LegacyRegistryKey + @"\Settings");
            if (settings != null)
            {
                foreach (string name in settings.GetValueNames())
                {
                    object? value = settings.GetValue(name);
                    _values[name] = value switch
                    {
                        int i => JsonValue.Create(i),
                        string s => JsonValue.Create(s),
                        _ => null
                    };
                }
            }

            using RegistryKey? receiver = Registry.CurrentUser.OpenSubKey(AppStorage.LegacyRegistryKey + @"\Receiver");
            if (receiver?.GetValue("Clients") is string[] clients) SetStringsLocked("ReceiverClients", clients);
        }
        catch { }
    }

    public static int GetInt(string name, int fallback)
    {
        lock (_lock)
        {
            EnsureLoaded();
            JsonNode? node = _values[name];
            if (node is JsonValue v)
            {
                if (v.TryGetValue(out int i)) return i;
                if (v.TryGetValue(out string? s) && int.TryParse(s, out int parsed)) return parsed;
            }
            return fallback;
        }
    }

    public static bool GetBool(string name, bool fallback) => GetInt(name, fallback ? 1 : 0) != 0;

    public static string GetString(string name, string fallback = "")
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _values[name] is JsonValue v && v.TryGetValue(out string? s) ? s : fallback;
        }
    }

    public static string[] GetStrings(string name)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var list = new List<string>();
            if (_values[name] is JsonArray array)
            {
                foreach (JsonNode? item in array)
                {
                    if (item is JsonValue v && v.TryGetValue(out string? s)) list.Add(s);
                }
            }
            return list.ToArray();
        }
    }

    public static void Set(string name, int value) { lock (_lock) { EnsureLoaded(); _values[name] = value; } }
    public static void Set(string name, bool value) => Set(name, value ? 1 : 0);
    public static void Set(string name, string value) { lock (_lock) { EnsureLoaded(); _values[name] = value; } }
    public static void SetStrings(string name, IEnumerable<string> values) { lock (_lock) { EnsureLoaded(); SetStringsLocked(name, values); } }
    public static void Remove(string name) { lock (_lock) { EnsureLoaded(); _values.Remove(name); } }

    private static void SetStringsLocked(string name, IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string v in values) array.Add(v);
        _values[name] = array;
    }

    public static void Save()
    {
        lock (_lock)
        {
            EnsureLoaded();
            SaveLocked();
        }
    }

    private static void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(AppStorage.DataDir);
            string temp = AppStorage.SettingsFile + ".tmp";
            File.WriteAllText(temp, _values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temp, AppStorage.SettingsFile, true);
        }
        catch { }
    }
}
