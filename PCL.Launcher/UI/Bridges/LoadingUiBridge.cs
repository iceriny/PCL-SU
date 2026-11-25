using Avalonia.Threading;
using PCL.UI.SystemIntegration;
using PCL.Views.Pages.Speed;
using PCL.Views.Shell;

namespace PCL.UI.Bridges;

/// <summary>加载框架通知：下载按钮、任务栏进度与任务管理页事件。</summary>
public sealed class LoadingUiBridge : ILoadingUi
{
    public void TaskAdded(Loading.LoaderBase loader)
    {
        NotifyTaskPage(page => page.RefreshNow());
    }

    public void TaskRemoved(Loading.LoaderBase loader)
    {
        NotifyTaskPage(page => page.RemoveTask(loader));
    }

    public void TaskRefreshed(Loading.LoaderBase loader)
    {
        NotifyTaskPage(page => page.RefreshNow());
    }

    public void RefreshDownloadButton()
    {
        ExtraButtonsPanel.Current?.RefreshDownloadButton();
    }

    public void UpdateButtonProgress(double progress)
    {
        ExtraButtonsPanel.Current?.UpdateButtonProgress(progress);
    }

    public void UpdateTaskbar(TaskbarProgressState state, double progress)
    {
        TaskbarProgress.Update(state, progress);
    }

    /// <summary>首次进入任务页时会读取完整任务列表，此前只需刷新下载按钮。</summary>
    private static void NotifyTaskPage(Action<PageSpeedRight> update)
    {
        var page = PageSpeedRight.Current;
        Dispatcher.UIThread.Post(() =>
        {
            if (page is not null) update(page);
            ExtraButtonsPanel.Current?.RefreshDownloadButton();
        });
    }
}
