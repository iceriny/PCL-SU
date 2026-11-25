using System.Net;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Http;

namespace PCL;

public static partial class GameDownload
{
    /// <summary>
    ///     LabyMod 列表，主加载器。
    /// </summary>
    public static readonly Loading.LoaderTask<int, DlLabyModListResult> DlLabyModListLoader =
        new("DlLabyModList Main", DlLabyModListMain);

    /// <summary>
    ///     LabyMod 列表，官方源。
    /// </summary>
    public static readonly Loading.LoaderTask<int, DlLabyModListResult>
        DlLabyModListOfficialLoader =
            new("DlLabyModList Official", DlLabyModListOfficialMain);

    private static void DlLabyModListMain(Loading.LoaderTask<int, DlLabyModListResult> loader)
    {
        var timeoutSeconds = Config.Download.VersionListSource == 1 ? 35 : 60;
        DlSourceLoader(loader, [new(DlLabyModListOfficialLoader, timeoutSeconds)], loader.isForceRestarting);
    }

    private static void DlLabyModListOfficialMain(
        Loading.LoaderTask<int, DlLabyModListResult> loader)
    {
        JsonObject resultProduction;
        using (var productionResponse = HttpRequest
                   .Create("https://releases.r2.labymod.net/api/v1/manifest/production/latest.json")
                   .WithHttpVersionOption(HttpVersion.Version20)
                   .SendAsync()
                   .GetAwaiter()
                   .GetResult())
        {
            resultProduction = (JsonObject)LauncherCommon.GetJson(productionResponse.AsString());
        }

        JsonObject resultSnapshot;
        using (var snapshotResponse = HttpRequest
                   .Create("https://releases.r2.labymod.net/api/v1/manifest/snapshot/latest.json")
                   .WithHttpVersionOption(HttpVersion.Version20)
                   .SendAsync()
                   .GetAwaiter()
                   .GetResult())
        {
            snapshotResponse.EnsureSuccessStatusCode();
            resultSnapshot = (JsonObject)LauncherCommon.GetJson(snapshotResponse.AsString());
        }

        var result = new JsonObject
        {
            {
                "production", resultProduction
            },
            {
                "snapshot", resultSnapshot
            }
        };
        try
        {
            var output = new DlLabyModListResult
            {
                Value = result
            };
            if (output.Value["production"]?["labyModVersion"] is null
                || output.Value["snapshot"]?["labyModVersion"] is null)
                throw new Exception(
                    Lang.Text(
                        "Minecraft.Download.Error.VersionListOperationFailed",
                        "LabyMod",
                        result));
            loader.output = output;
        }
        catch (Exception ex)
        {
            throw new Exception(
                Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "LabyMod", result),
                ex);
        }
    }

    public struct DlLabyModListResult
    {
        /// <summary>
        ///     获取到的数据。
        /// </summary>
        public JsonObject Value;
    }
}
