using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using PCL.Core.IO;

namespace PCL.Core.App.Essentials;

public static class UpdateHelper
{
    internal sealed record InstallResult(int UpdaterProcessId, string? Error, string? PayloadSha256 = null);

    public static string GetStagedPath(string target) =>
        Path.Combine(Path.GetDirectoryName(target)!, "PCL", "Plain Craft Launcher SU.exe");

    public static string GetStagedPath(string target, string sha256)
    {
        if (!IsSha256(sha256)) throw new ArgumentException("更新文件缺少有效的 SHA-256", nameof(sha256));
        return Path.Combine(Path.GetDirectoryName(target)!, "PCL", "Updates", sha256.ToLowerInvariant() + ".exe");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && !value.AsSpan().ContainsAnyExcept("0123456789abcdefABCDEF");

    internal static bool IsExpectedHandoff(string runningPath, string source, string target)
    {
        var running = Path.GetFullPath(runningPath);
        var sourcePath = Path.GetFullPath(source);
        var targetPath = Path.GetFullPath(target);
        if (!running.Equals(sourcePath, StringComparison.OrdinalIgnoreCase) ||
            running.Equals(targetPath, StringComparison.OrdinalIgnoreCase)) return false;
        if (sourcePath.Equals(GetStagedPath(targetPath), StringComparison.OrdinalIgnoreCase)) return true;
        var hash = Path.GetFileNameWithoutExtension(sourcePath);
        return IsSha256(hash) && sourcePath.Equals(GetStagedPath(targetPath, hash), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetResultPath(string target) =>
        Path.Combine(Path.GetDirectoryName(GetStagedPath(target))!, "UpdateResult.json");

    internal static void SaveResult(string target, InstallResult result)
    {
        var path = GetResultPath(target);
        var staging = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(staging, JsonSerializer.Serialize(result));
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    internal static InstallResult? ReadResult(string target)
    {
        var path = GetResultPath(target);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<InstallResult>(File.ReadAllText(path))
            ?? throw new InvalidDataException("更新结果为空");
    }

    internal static InstallResult? ConsumeResult(string target)
    {
        var result = ReadResult(target);
        if (result is null) return null;
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
            File.Delete(result.PayloadSha256 is null ? GetStagedPath(target) : GetStagedPath(target, result.PayloadSha256));
        }
        File.Delete(GetResultPath(target));
        return result;
    }

    /// <summary>将完整的更新程序复制到目标目录，然后原子替换原文件。</summary>
    public static Exception? Replace(string source, string target) => Replace(source, target, null);

    internal static Exception? Replace(string source, string target, Action? beforeCommit)
    {
        var staging = $"{target}.update.{Guid.NewGuid():N}.tmp";
        try
        {
            if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException($"启动器文件为只读：{target}");
            File.Copy(source, staging);
            beforeCommit?.Invoke();
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    File.Move(staging, target, overwrite: true);
                    return null;
                }
                catch (Exception ex) when (Files.IsRetryableReplaceError(ex) && elapsed.ElapsedMilliseconds < 5000)
                {
                    Thread.Sleep(150);
                }
            }
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
