using System.Collections.Concurrent;

namespace PCL.Network.Loaders;

public class LoaderDownload : Loading.LoaderBase
{
    private readonly object _fileRemainLock = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private Task _whenDrained = Task.CompletedTask;
    private int _fileRemain;
    public SafeList<DownloadFile> files;

    public LoaderDownload(string name, List<DownloadFile> fileTasks)
    {
        this.name = name;
        files = new SafeList<DownloadFile>(fileTasks ?? new List<DownloadFile>());
    }

    public int FailCount { get; set; }

    public bool PreserveExistingFiles { get; init; }

    internal bool AllowRestart { get; init; } = true;

    /// <summary>当前下载 worker 退出后完成，包括取消后的文件收尾。</summary>
    internal Task WhenDrained => Volatile.Read(ref _whenDrained);

    public override double Progress
    {
        get =>
            State >= LoadState.Finished ? 1 :
            files.Any() ? files.Average(file => file.Progress) : 0;
        set => throw new Exception("文件下载不允许指定进度");
    }

    public void RefreshStat()
    {
    }

    public override void Start(object input = null, bool isForceRestart = false)
    {
        lock (lockState)
        {
            if (State == LoadState.Loading || !WhenDrained.IsCompleted || (!AllowRestart && State != LoadState.Waiting))
                return;

            if (input is List<DownloadFile> inputFiles)
                files = new SafeList<DownloadFile>(inputFiles);
            lock (_fileRemainLock)
            {
                _fileRemain = files.Count;
            }

            var cancellation = new CancellationTokenSource();
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _cancellationTokenSource = cancellation;
            Volatile.Write(ref _whenDrained, drained.Task);
            try
            {
                NetFacade.NetManager.Start(this);
                State = LoadState.Loading;
                LauncherCommon.RunInNewThread(() => Run(cancellation, drained), $"DL/{Uuid}");
            }
            catch (Exception ex)
            {
                try { OnFail([ex]); }
                finally { drained.TrySetResult(); }
            }
        }
    }

    private void Run(CancellationTokenSource cancellation, TaskCompletionSource drained)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            if (!files.Any())
            {
                OnFinish();
                return;
            }

            var exceptions = new ConcurrentQueue<Exception>();
            using var semaphore = new SemaphoreSlim(GetMaxParallelFiles());
            var tasks = files
                .Select(async file =>
                {
                    var entered = false;
                    try
                    {
                        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                        entered = true;
                        await ProcessFileAsync(file, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken
                                                                 .IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        file.AddError(ex);
                        file.State = NetState.Interrupted;
                        exceptions.Enqueue(ex);
                        cancellation.Cancel();
                    }
                    finally
                    {
                        if (entered)
                            semaphore.Release();
                    }
                })
                .ToList();

            Task.WhenAll(tasks).GetAwaiter().GetResult();
            if (!exceptions.IsEmpty)
                OnFail(exceptions.ToList());
        }
        catch (OperationCanceledException)
        {
            Abort();
        }
        catch (Exception ex)
        {
            OnFail(
                new List<Exception>
                {
                    ex
                });
        }
        finally
        {
            drained.TrySetResult();
        }
    }

    private int GetMaxParallelFiles()
    {
        return Math.Max(
            1,
            Math.Min(
                files.Count,
                Math.Clamp(
                    NetFacade.NetTaskConnectionLimit,
                    1,
                    NetFacade.NetTaskConnectionLimitMax)));
    }

    private async Task ProcessFileAsync(DownloadFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        file.RegisterLoader(this);

        if (State >= LoadState.Finished)
            return;
        var targetDir = Path.GetDirectoryName(file.LocalPath) ?? throw new IOException("下载路径无效");
        Directory.CreateDirectory(targetDir);
        var checker = file.Check;
        if (checker?.canUseExistsFile == true
            && (!PreserveExistingFiles || !string.IsNullOrEmpty(checker.hash))
            && File.Exists(file.LocalPath))
        {
            var checkResult = string.IsNullOrEmpty(checker.hash)
                ? checker.Check(file.LocalPath)
                : await Task
                    .Run(() => checker.Check(file.LocalPath), cancellationToken)
                    .ConfigureAwait(false);
            if (checkResult is null)
            {
                file.IsCopy = true;
                file.State = NetState.Finished;
                try
                {
                    file.TotalSize = new FileInfo(file.LocalPath).Length;
                }
                catch (IOException)
                {
                    file.TotalSize = -1;
                }

                file.DownloadedBytes = file.TotalSize;
                file.Speed = 0;
                file.ActiveThreads = 0;
                OnFileFinish(file);
                return;
            }
        }

        file.State = NetState.Connecting;
        // 批量任务中未知大小的文件先发普通请求，下载器根据响应大小决定是否改用分片。
        var expectedSize = file.Check?.actualSize ?? -1;
        var enableParallelChunks = files.Count <= 1
            || expectedSize >= AdaptiveRangeDownloader.SmallFileThreshold;
        var downloadPath = PreserveExistingFiles
            ? Path.Combine(targetDir, $".pcl-{Guid.NewGuid():N}.part")
            : file.LocalPath;
        try
        {
            await FileDownloader
                .DownloadAsync(
                    file.Urls,
                    downloadPath,
                    file.UseBrowserUserAgent,
                    file.CustomUserAgent,
                    cancellationToken,
                    enableParallelChunks,
                    file)
                .ConfigureAwait(false);
            if (PreserveExistingFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(downloadPath, file.LocalPath);
            }
        }
        finally
        {
            if (PreserveExistingFiles)
            {
                try { File.Delete(downloadPath); }
                catch (IOException ex) { LauncherCommon.Log(ex, $"无法清理下载临时文件：{downloadPath}"); }
                catch (UnauthorizedAccessException ex) { LauncherCommon.Log(ex, $"无法清理下载临时文件：{downloadPath}"); }
            }
        }
        try
        {
            file.TotalSize = new FileInfo(file.LocalPath).Length;
        }
        catch (IOException)
        {
            file.TotalSize = -1;
        }

        file.IsUnknownSize = file.TotalSize < 0;
        file.DownloadedBytes = Math.Max(0, file.TotalSize);
        file.Speed = 0;
        file.ActiveThreads = 0;
        file.State = NetState.Finished;
        OnFileFinish(file);
    }

    public void OnFileFinish(DownloadFile file)
    {
        lock (_fileRemainLock)
        {
            _fileRemain -= 1;
            if (_fileRemain > 0)
                return;
        }

        OnFinish();
    }

    public void OnFinish()
    {
        RaisePreviewFinish();
        lock (lockState)
        {
            if (State > LoadState.Loading)
                return;
            State = LoadState.Finished;
        }

        NetFacade.NetManager.Finish(this);
    }

    public void OnFileFail(DownloadFile file)
    {
        var errors = file.Errors;
        OnFail(
            errors.Count > 0
                ? errors.ToList()
                : new List<Exception>
                {
                    new($"文件下载失败：{file.LocalPath}")
                });
    }

    public void OnFail(List<Exception> exList)
    {
        lock (lockState)
        {
            if (State > LoadState.Loading)
                return;
            Error = exList.FirstOrDefault() ?? new Exception("未知下载错误");
            State = LoadState.Failed;
        }

        FailCount += exList.Count;
        foreach (var file in files.Where(file => file.State < NetState.Finished))
        {
            file.State = NetState.Interrupted;
            file.Speed = 0;
            file.ActiveThreads = 0;
            file.AddErrors(exList);
        }

        NetFacade.NetManager.Finish(this);
    }

    public override void Abort()
    {
        lock (lockState)
        {
            if (State >= LoadState.Finished)
                return;
            State = LoadState.Aborted;

            _cancellationTokenSource?.Cancel();
            foreach (var file in files.Where(file => file.State < NetState.Finished))
            {
                file.State = NetState.Interrupted;
                file.Speed = 0;
                file.ActiveThreads = 0;
            }

            NetFacade.NetManager.Finish(this);
        }
    }
}
