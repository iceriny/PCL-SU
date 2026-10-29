using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PCL.Core.App.Essentials;

public static class UpdateHelper
{
    internal sealed record InstallResult(int UpdaterProcessId, string? Error);

    internal static void SaveResult(string target, InstallResult result)
    {
        var path = Path.Combine(Path.GetDirectoryName(target)!, "PCL", "UpdateResult.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result));
    }

    internal static InstallResult? ConsumeResult(string target)
    {
        var directory = Path.Combine(Path.GetDirectoryName(target)!, "PCL");
        var path = Path.Combine(directory, "UpdateResult.json");
        if (!File.Exists(path)) return null;
        var result = JsonSerializer.Deserialize<InstallResult>(File.ReadAllText(path))
            ?? throw new InvalidDataException("更新结果为空");
        if (result.Error is null)
        {
            if (result.UpdaterProcessId != 0)
            {
                Process? updater = null;
                try { updater = Process.GetProcessById(result.UpdaterProcessId); }
                catch (ArgumentException) { /* 更新进程已经退出。 */ }
                using (updater)
                    if (updater is not null && !updater.WaitForExit(5000)) return null;
            }
            File.Delete(Path.Combine(directory, "Plain Craft Launcher SU.exe"));
        }
        File.Delete(path);
        return result;
    }

    /// <summary>
    /// 更新启动器（替换文件）
    /// </summary>
    /// <param name="source">用于替换的来源文件路径</param>
    /// <param name="target">目标文件路径</param>
    public static Exception? Replace(string source, string target)
    {
        var staging = $"{target}.update.{Guid.NewGuid():N}.tmp";
        try
        {
            // 来源可能是正在运行的更新进程，先完整复制到目标所在目录再原子替换。
            File.Copy(source, staging);
            File.Replace(staging, target, null);
            return null;
        }
        catch (Exception ex)
        {
            try { File.Delete(staging); }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                return new AggregateException("替换失败，且未能清理更新暂存文件", ex, cleanupEx);
            }
            return ex;
        }
    }
}
