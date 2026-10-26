using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MagicKeyBattery;

// Windows 起動時の自動実行
//   タスク スケジューラの「ログオン時」トリガーを使う: レジストリの Run キーだと Windows がデスクトップ表示後
//   数十秒遅らせるが、ログオン タスクはすぐ始まる (キーボードの配置がログイン直後から効く)
//   旧バージョンの Run キーが残っていれば削除して移行する
internal static class Startup
{
    private const string TaskName = "MagicKeyBattery";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        if (TaskExists()) return true;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(TaskName) != null;
        }
        catch
        {
            return false;
        }
    }

    // null = 成功、それ以外はエラーメッセージ
    public static string? Set(bool enable)
    {
        string exe = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
        string? error = enable ? RegisterTask(exe) : DeleteTask();
        if (error != null && enable)
        {
            // タスク スケジューラが使えない環境 (ポリシーなど) では Run キーにフォールバック
            error = SetRunKey(exe, true) ? null : error;
            return error;
        }
        SetRunKey(exe, false); // 旧バージョンの Run キーは削除 (タスクに移行 / 無効化)
        return error;
    }

    private static bool SetRunKey(string exe, bool enable)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return false;
            if (enable) key.SetValue(TaskName, $"\"{exe}\"");
            else key.DeleteValue(TaskName, false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // --- タスク スケジューラ (COM: Schedule.Service) ---

    private static dynamic? Connect()
    {
        Type? type = Type.GetTypeFromProgID("Schedule.Service");
        if (type == null) return null;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static bool TaskExists()
    {
        try
        {
            dynamic? service = Connect();
            if (service == null) return false;
            dynamic folder = service.GetFolder("\\");
            dynamic? task = folder.GetTask(TaskName);
            return task is not null && (bool)task.Enabled;
        }
        catch
        {
            return false;
        }
    }

    private static string? RegisterTask(string exe)
    {
        try
        {
            dynamic? service = Connect();
            if (service == null) return "Task Scheduler is not available";
            dynamic folder = service.GetFolder("\\");
            dynamic task = service.NewTask(0);

            task.RegistrationInfo.Description = "Magic Keyboard battery, MX Keys layout and device switching (starts at sign-in)";
            task.RegistrationInfo.Author = "MagicKeyBattery";

            // 現在のユーザーのログオン時、通常の権限で
            task.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
            task.Principal.RunLevel = 0;  // TASK_RUNLEVEL_LUA

            dynamic settings = task.Settings;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.ExecutionTimeLimit = "PT0S";   // 時間制限なし (常駐するため)
            settings.MultipleInstances = 2;         // TASK_INSTANCES_IGNORE_NEW
            settings.StartWhenAvailable = true;
            settings.RunOnlyIfIdle = false;
            settings.AllowHardTerminate = false;
            settings.Priority = 5;                  // 通常 (既定の 7 = 低い優先度だと入力が遅れる)

            dynamic trigger = task.Triggers.Create(9); // TASK_TRIGGER_LOGON
            trigger.UserId = $"{Environment.UserDomainName}\\{Environment.UserName}";
            trigger.Enabled = true;

            dynamic action = task.Actions.Create(0); // TASK_ACTION_EXEC
            action.Path = exe;
            action.WorkingDirectory = Path.GetDirectoryName(exe);

            folder.RegisterTaskDefinition(TaskName, task, 6 /* TASK_CREATE_OR_UPDATE */, null, null, 3 /* INTERACTIVE_TOKEN */, null);
            return null;
        }
        catch (COMException ex)
        {
            return $"Task Scheduler error 0x{ex.HResult:X8}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string? DeleteTask()
    {
        try
        {
            dynamic? service = Connect();
            if (service == null) return null;
            dynamic folder = service.GetFolder("\\");
            try { folder.DeleteTask(TaskName, 0); } catch (COMException) { } // 無ければ無視
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
