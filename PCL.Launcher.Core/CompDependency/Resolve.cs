using PCL.Core.Minecraft.ResourceProject;
using PCL.Network;
using PCL.Core.App.Localization;

namespace PCL;

public static partial class CompDependency
{
    public static List<DownloadFile> BuildDependencyDownloads(
        ModDependencyResolutionResult result,
        string targetPath)
    {
        ArgumentNullException.ThrowIfNull(result);

        targetPath = Path.GetFullPath(targetPath);
        var targetFolder = Path.GetDirectoryName(targetPath)
            ?? throw new IOException("模组下载目录无效。");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var paths = new HashSet<string>(StringComparer.FromComparison(comparison)) { targetPath };
        var downloads = new List<DownloadFile>();
        var existingInstalls = new HashSet<ResolvedDependencyInstall>();
        foreach (var install in result.ToInstall.AsEnumerable().Reverse())
        {
            var fromCurseForge = string.Equals(
                install.Source,
                "CurseForge",
                StringComparison.OrdinalIgnoreCase);
            var depCompFile = Comp.compFilesCache.GetValueOrDefault(install.ProjectId)
                ?.FirstOrDefault(file => file.FromCurseForge == fromCurseForge && string.Equals(
                    file.Id,
                    install.File.Id,
                    StringComparison.OrdinalIgnoreCase));
            if (depCompFile is null
                || !Comp.compProjectCache.TryGetValue(install.ProjectId, out var depProject)
                || depProject.FromCurseForge != fromCurseForge)
            {
                AddUnresolved(install, Lang.Text("Download.Comp.Dependency.Check.MissingFile"));
                continue;
            }

            var depFileName = Comp.CompFileNameGet(depProject, depCompFile);
            var destination = Path.GetFullPath(Path.Combine(targetFolder, depFileName));
            if (!string.Equals(Path.GetDirectoryName(destination), targetFolder, comparison)
                || !paths.Add(destination))
            {
                AddUnresolved(install, Lang.Text("Download.Comp.Dependency.Check.FileConflict", depFileName));
                continue;
            }

            var dependency = depCompFile.ToNetFile(destination, Comp.DownloadReason.Dependency);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                // 仅在有校验值且内容匹配时复用已有文件。
                if (dependency.Check is not { hash.Length: > 0 } checker || checker.Check(destination) is not null)
                    AddUnresolved(install, Lang.Text("Download.Comp.Dependency.Check.FileConflict", depFileName));
                else
                    existingInstalls.Add(install);
                continue;
            }

            downloads.Add(dependency);
        }

        result.ToInstall.RemoveAll(existingInstalls.Contains);
        return downloads;

        void AddUnresolved(ResolvedDependencyInstall install, string reason)
        {
            result.Unresolved.Add(new UnresolvedDependency
            {
                ProjectId = install.ProjectId,
                Source = install.Source,
                Reason = reason
            });
        }
    }
}
