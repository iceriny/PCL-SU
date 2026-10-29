using PCL.Core.App.Localization;
using PCL.Core.Utils;

namespace PCL;

public class UpdatesWrapperModel : IUpdateSource
{
    private readonly IEnumerable<IUpdateSource> _sources;
    private IUpdateSource _announcementSource;
    private IUpdateSource _versionSource;

    public UpdatesWrapperModel(IEnumerable<IUpdateSource> sources)
    {
        _sources = sources;
    }

    public string SourceName
    {
        get => _versionSource?.SourceName ?? "";
        set
        {
            if (_versionSource is null)
                return;
            _versionSource.SourceName = value;
        }
    }

    public bool IsAvailable()
    {
        return _sources.Any(x => x.IsAvailable());
    }

    public bool RefreshCache()
    {
        Exception? lastError = null;
        foreach (var item in _sources)
            try
            {
                if (!item.RefreshCache()) continue;
                _versionSource = item;
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                ModBase.Log(ex, $"[Update] {item.SourceName} 暂不可用");
            }

        throw new Exception(Lang.Text("Update.Task.GetVersionInfoFailed"), lastError);
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
        => FromVersionSources(source => source.GetLatestVersion(channel, arch));

    private T FromVersionSources<T>(Func<IUpdateSource, T> fetch)
    {
        Exception? lastError = null;
        foreach (var item in _sources.Prepend(_versionSource).Where(source => source is not null).Distinct())
            try
            {
                var ret = fetch(item);
                _versionSource = item;
                return ret;
            }
            catch (Exception ex)
            {
                lastError = ex;
                ModBase.Log(ex, $"[Update] {item.SourceName} 无法获取最新版本信息");
            }

        ModBase.Log("[Update] 错误！所有的版本源都无法使用！");
        throw new Exception(Lang.Text("Update.Task.GetVersionInfoFailed"), lastError);
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
        => FromVersionSources(source => source.IsLatest(channel, arch, currentVersion, currentVersionCode));

    public VersionAnnouncementDataModel GetAnnouncementList()
    {
        foreach (var item in _sources)
            try
            {
                if (_announcementSource is not null)
                    try
                    {
                        return _announcementSource.GetAnnouncementList();
                    }
                    catch (Exception ex)
                    {
                        ModBase.Log(ex, $"[Update] 缓存的公告源 {_announcementSource.SourceName} 不可用");
                    }

                var ret = item.GetAnnouncementList();
                _announcementSource = item;
                return ret;
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[Update] {item.SourceName} 无法获取最新公告信息");
            }

        ModBase.Log("[Update] 错误！所有的公告源都无法使用！");
        throw new Exception(Lang.Text("Update.Task.GetAnnouncementFailed"));
    }

    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
        => FromVersionSources(source => source.GetDownloadLoader(channel, arch, output));

    public async Task<bool> IsLatestAsync(UpdateChannel channel, UpdateArch arch, SemVer currentVersion,
        int currentVersionCode)
    {
        return await Task.Run(() => IsLatest(channel, arch, currentVersion, currentVersionCode));
    }
}
