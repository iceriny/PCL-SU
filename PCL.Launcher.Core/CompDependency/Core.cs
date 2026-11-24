using PCL.Core.Minecraft.ResourceProject;
using CompFile = PCL.Comp.CompFile;
using CompLoaderType = PCL.Comp.CompLoaderType;
using CompProject = PCL.Comp.CompProject;
using LocalCompFile = PCL.LocalComp.LocalCompFile;

namespace PCL;

public static partial class CompDependency
{
    public static ModDependencyRequest BuildRequest(
        CompFile file,
        CompProject project,
        string targetMinecraftVersion,
        List<CompLoaderType> targetLoaders,
        string targetModsFolder,
        CancellationToken cancellationToken = default,
        IProgress<ModDependencyProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(project);

        var source = GetSource(project.FromCurseForge);
        return new ModDependencyRequest
        {
            TargetMinecraftVersion = targetMinecraftVersion,
            TargetLoaders = ToLoaderNames(targetLoaders),
            RequiredDependencies = GetRequiredDependencies(file, source),
            RootProject = new ModDependencyReference { ProjectId = project.Id, Source = source },
            InstalledMods = ScanInstalledMods(
                targetModsFolder, targetMinecraftVersion, targetLoaders, cancellationToken, progress),
            ProjectResolver = (dependencySource, projectId) =>
                ResolveProjectFiles(dependencySource, projectId, cancellationToken)
        };
    }

    private static List<ModDependencyReference> GetRequiredDependencies(CompFile file, string source)
    {
        return file.RawDependencies
            .Concat(file.Dependencies)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => new ModDependencyReference { ProjectId = id, Source = source })
            .ToList();
    }

    private static List<InstalledModIdentity> ScanInstalledMods(
        string targetModsFolder,
        string targetMinecraftVersion,
        List<CompLoaderType> targetLoaders,
        CancellationToken cancellationToken,
        IProgress<ModDependencyProgress>? progress)
    {
        var result = new List<InstalledModIdentity>();
        if (string.IsNullOrWhiteSpace(targetModsFolder) || !Directory.Exists(targetModsFolder))
            return result;

        var paths = Directory.GetFiles(targetModsFolder)
            .Where(path => LocalCompFile.IsModFile(path)
                && !path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var cache = new Lazy<JsonObject>(LocalComp.LoadLocalModInfoCache);
        var completed = 0;
        progress?.Report(new(ModDependencyStage.ScanningInstalledMods, completed, paths.Count));
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var localFile = new LocalCompFile(path);
                var cacheKey = localFile.ModrinthHash + targetMinecraftVersion + string.Join("", targetLoaders);
                if (cache.Value[cacheKey] is JsonObject cachedFile)
                    localFile.FromJson(cachedFile);

                var projectId = localFile.Comp?.Id ?? localFile.CompFile?.ProjectId;
                if (string.IsNullOrWhiteSpace(projectId))
                    continue;
                var fromCurseForge = localFile.Comp?.FromCurseForge ?? localFile.CompFile.FromCurseForge;
                var gameVersions = localFile
                        .CompFile?.GameVersions
                        ?.Where(static version => !string.IsNullOrWhiteSpace(version))
                        .Select(Comp.NormalizeGameVersion)
                        .Where(static version => !string.IsNullOrWhiteSpace(version))
                        .ToList()
                    ?? [];
                var loaders = ToLoaderNames(localFile.CompFile?.ModLoaders);

                result.Add(
                    new InstalledModIdentity
                    {
                        SourceProjectId = projectId,
                        Source = GetSource(fromCurseForge),
                        GameVersions = gameVersions,
                        Loaders = loaders
                    });
            }
            catch (Exception ex)
            {
                LauncherCommon.Log(
                    ex,
                    $"[CompDeps] 读取已安装 Mod 失败：{path}",
                    LauncherLogLevel.Developer);
            }
            finally
            {
                completed++;
                progress?.Report(new(ModDependencyStage.ScanningInstalledMods, completed, paths.Count - completed));
            }
        }

        return result;
    }

    private static ModDependencyProject? ResolveProjectFiles(
        string source, string projectId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(projectId))
            return null;

        var fromCurseForge =
            string.Equals(source, "CurseForge", StringComparison.OrdinalIgnoreCase);
        var files = Comp.CompFilesGet(
            projectId, fromCurseForge, loadDependencies: false, cancellationToken: cancellationToken);
        if (!Comp.compProjectCache.TryGetValue(projectId, out var compProject))
            return null;

        if (compProject.FromCurseForge != fromCurseForge)
            return null;

        return new ModDependencyProject
        {
            ProjectId = compProject.Id,
            Source = source,
            ProjectName = compProject.TranslatedName ?? compProject.RawName,
            Files =
            [
                .. files.Select(compFile => new ModDependencyFile
                {
                    Id = compFile.Id,
                    DisplayName = compFile.DisplayName,
                    Version = compFile.Version,
                    GameVersions = compFile
                            .GameVersions
                            ?.Where(static version =>
                                !string.IsNullOrWhiteSpace(version))
                            .Select(Comp.NormalizeGameVersion)
                            .Where(static version =>
                                !string.IsNullOrWhiteSpace(version))
                            .ToList()
                        ?? [],
                    Loaders = ToLoaderNames(compFile.ModLoaders),
                    ReleaseType = MapReleaseType(compFile.Status),
                    ReleaseDate = compFile.ReleaseDate,
                    RequiredDependencies = GetRequiredDependencies(compFile, source)
                })
            ]
        };
    }
}
