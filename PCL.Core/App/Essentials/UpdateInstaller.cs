using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.App.Essentials;

/// <summary>Windows 自更新的进程交接。安装命令在启动器服务和窗口初始化前运行。</summary>
public static class UpdateInstaller
{
    private const string PipeVariable = "PCL_UPDATE_HANDOFF";
    private const int ReadyTimeoutMs = 30_000;

    public static void Start(string target, string source, bool restart)
    {
        var pipeName = $"PCLSU.Update.{Guid.NewGuid():N}";
        using var ready = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(source)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(target)!
        };
        start.Environment[PipeVariable] = pipeName;
        start.ArgumentList.Add("update");
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(target);
        start.ArgumentList.Add(source);
        start.ArgumentList.Add(restart ? "true" : "false");

        using var installer = Process.Start(start) ?? throw new IOException("未能启动更新进程");
        using var timeout = new CancellationTokenSource(ReadyTimeoutMs);
        try
        {
            var connected = ready.WaitForConnectionAsync(timeout.Token);
            var exited = installer.WaitForExitAsync(timeout.Token);
            Task.WhenAny(connected, exited).GetAwaiter().GetResult();
            timeout.Token.ThrowIfCancellationRequested();
            if (installer.HasExited)
            {
                var result = UpdateHelper.ReadResult(target);
                throw new IOException(result?.UpdaterProcessId == installer.Id && result.Error is not null
                    ? result.Error : $"更新进程提前退出（0x{installer.ExitCode:X8}）");
            }
            connected.GetAwaiter().GetResult();
            ready.WriteByte(1);
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException("更新进程未能及时就绪，请重试。", ex);
        }
        finally
        {
            timeout.Cancel();
        }
    }

    public static int Run(string[] args)
    {
        if (args is not ["update", var pid, var target, var source, var restartFlag] ||
            !int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var previousId) ||
            previousId <= 0 || previousId == Environment.ProcessId ||
            !bool.TryParse(restartFlag, out var restart))
            return 1;
        try
        {
            if (!UpdateHelper.IsExpectedHandoff(Environment.ProcessPath!, source, target)) return 1;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return 1;
        }

        var pipeName = Environment.GetEnvironmentVariable(PipeVariable);
        Environment.SetEnvironmentVariable(PipeVariable, null);
        var logPath = Path.Combine(Path.GetDirectoryName(source)!, "Log", "LastPending_Update.log");
        Exception? error;
        var previousExited = false;
        try
        {
            Log($"正在更新 {target}，等待进程 {previousId}。");
            using var previous = FindPreviousProcess(previousId);
            // 旧版的五参数交接没有管道，旧进程会自行退出。
            if (pipeName is null) WaitForPrevious();
            error = UpdateHelper.Replace(source, target, () =>
            {
                if (pipeName is null) return;
                using var ready = new NamedPipeClientStream(".", pipeName, PipeDirection.In);
                ready.Connect(ReadyTimeoutMs);
                if (ready.ReadByte() != 1) throw new IOException("启动器已取消本次更新。");
                WaitForPrevious();
            });

            void WaitForPrevious()
            {
                if (previous is not null && !previous.WaitForExit(120_000))
                    throw new TimeoutException("等待旧版本退出超时");
                previousExited = true;
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        Record(error);
        if (restart && previousExited)
        {
            try
            {
                var start = new ProcessStartInfo(target) { WorkingDirectory = Path.GetDirectoryName(target)! };
                if (error is not null)
                {
                    start.ArgumentList.Add("update_failed");
                    start.ArgumentList.Add(error.Message);
                }
                using var updated = Process.Start(start) ?? throw new IOException("未能重新启动启动器");
                Log($"已从原路径启动进程 {updated.Id}。");
            }
            catch (Exception ex)
            {
                error = new IOException($"重新启动 {target} 失败：{ex.Message}", ex);
                Record(error);
            }
        }
        return error is null ? 0 : 1;

        void Record(Exception? failure)
        {
            Log(failure?.ToString() ?? "替换完成。");
            try { UpdateHelper.SaveResult(target, new(Environment.ProcessId, failure?.Message)); }
            catch (Exception ex) { Log($"保存更新结果失败：{ex}"); }
        }

        void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static Process? FindPreviousProcess(int id)
    {
        try { return Process.GetProcessById(id); }
        catch (ArgumentException) { return null; }
    }
}
