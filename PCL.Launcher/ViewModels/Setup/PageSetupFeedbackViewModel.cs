using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using PCL.Core.App.Localization;
using PCL.Core.ViewModel;
using PCL.Network;

namespace PCL;

/// <summary>设置页面中「反馈」页的视图模型。</summary>
public partial class PageSetupFeedbackViewModel : ViewModelBase
{
    private Dictionary<string, (ObservableCollection<SetupFeedbackItemRow> Rows, string Icon)>?
        _groups;

    [ObservableProperty] public partial bool ShowCompleted { get; set; }

    [ObservableProperty] public partial bool ShowDecline { get; set; }

    [ObservableProperty] public partial bool ShowDuplicate { get; set; }

    [ObservableProperty] public partial bool ShowIgnored { get; set; }

    [ObservableProperty] public partial bool ShowPause { get; set; }

    [ObservableProperty] public partial bool ShowProcessing { get; set; }

    [ObservableProperty] public partial bool ShowUpnext { get; set; }

    [ObservableProperty] public partial bool ShowWait { get; set; }

    [ObservableProperty] public partial bool ShowWaitingProcess { get; set; }

    public ObservableCollection<SetupFeedbackItemRow> ProcessingRows { get; } = [];

    public ObservableCollection<SetupFeedbackItemRow> WaitingProcessRows { get; } = [];

    public ObservableCollection<SetupFeedbackItemRow> WaitRows { get; } = [];

    public ObservableCollection<SetupFeedbackItemRow> PauseRows { get; } = [];

    public ObservableCollection<SetupFeedbackItemRow> UpnextRows { get; } = [];

    public ObservableCollection<SetupFeedbackItemRow> CompletedRows { get; } = [];

    public ObservableCollection<SetupFeedbackItemRow> DeclineRows { get; } = [];

    public ObservableCollection<SetupFeedbackItemRow> IgnoredRows { get; } = [];

    public ObservableCollection<SetupFeedbackItemRow> DuplicateRows { get; } = [];

    private Dictionary<string, (ObservableCollection<SetupFeedbackItemRow> Rows, string Icon)>
        Groups =>
        _groups ??=
            new Dictionary<string, (ObservableCollection<SetupFeedbackItemRow> Rows, string Icon)>(StringComparer.OrdinalIgnoreCase)
            {
                ["in progress"] = (ProcessingRows, "Blocks/CommandBlock.png"),
                ["open"] = (WaitingProcessRows, "Blocks/RedstoneBlock.png"),
                ["waiting"] = (WaitRows, "Blocks/Anvil.png"),
                ["on hold"] = (PauseRows, "Blocks/RedstoneLampOff.png"),
                ["planned"] = (UpnextRows, "Blocks/RedstoneLampOn.png"),
                ["closed"] = (CompletedRows, "Blocks/Grass.png"),
                ["wontfix"] = (DeclineRows, "Blocks/CobbleStone.png"),
                ["invalid"] = (IgnoredRows, "Blocks/CobbleStone.png"),
                ["duplicate"] = (DuplicateRows, "Blocks/CobbleStone.png")
            };

    public static List<FeedbackItem> FetchFeedbackList()
    {
        var json = Requester.FetchJson(
            "https://api.github.com/repos/iceriny/PCL-SU/issues?state=all&sort=created&per_page=100",
            new RequestParam
            {
                Retries = 3,
                UseBrowserUserAgent = true
            });
        if (json is not JsonArray list)
            throw new InvalidOperationException(Lang.Text("Setup.Feedback.LoadFailed"));

        return ParseFeedbackList(list);
    }

    internal static List<FeedbackItem> ParseFeedbackList(JsonArray list)
    {
        var result = new List<FeedbackItem>();
        foreach (var node in list)
        {
            if (node is not JsonObject issue) continue;
            if (issue["pull_request"] is not null) continue;

            var issueType = (issue["type"] as JsonObject)?["name"]?.ToString() ?? Lang.Text("Setup.Feedback.Uncategorized");

            var labels = new List<string>();
            if (issue["labels"] is JsonArray labelArray)
                foreach (var label in labelArray)
                    if (label is JsonObject labelObject)
                        labels.Add(labelObject["name"]?.ToString() ?? string.Empty);

            result.Add(
                new FeedbackItem
                {
                    Id = issue["number"]?.ToString() ?? "?",
                    Title = issue["title"]?.ToString() ?? string.Empty,
                    User = issue["user"]?["login"]?.ToString() ?? string.Empty,
                    CreatedAt = DateTime.TryParse(
                        issue["created_at"]?.ToString(),
                        out var createdAt)
                        ? createdAt
                        : default,
                    Content = issue["body"]?.ToString() ?? string.Empty,
                    Url = issue["html_url"]?.ToString() ?? string.Empty,
                    Labels = labels,
                    Closed = issue["state"]?.ToString() == "closed",
                    NotPlanned = issue["state_reason"]?.ToString() == "not_planned",
                    Type = issueType
                });
        }

        return result;
    }

    public void SetItems(List<FeedbackItem>? source)
    {
        foreach (var group in Groups.Values)
            group.Rows.Clear();
        foreach (var item in source ?? [])
        {
            var status = item.Labels.Find(Groups.ContainsKey);
            if (status is null && item.Closed) status = item.NotPlanned ? "wontfix" : "closed";
            var rows = Groups[status ?? "open"];
            rows.Rows.Add(
                new SetupFeedbackItemRow(
                    $"#{item.Id} {item.Title}",
                    item.User,
                    item.CreatedAt,
                    LauncherPaths.ImageRoot + rows.Icon,
                    item.Type,
                    item));
        }

        ShowProcessing = ProcessingRows.Count != 0;
        ShowWaitingProcess = WaitingProcessRows.Count != 0;
        ShowWait = WaitRows.Count != 0;
        ShowPause = PauseRows.Count != 0;
        ShowUpnext = UpnextRows.Count != 0;
        ShowCompleted = CompletedRows.Count != 0;
        ShowDecline = DeclineRows.Count != 0;
        ShowIgnored = IgnoredRows.Count != 0;
        ShowDuplicate = DuplicateRows.Count != 0;
    }
}

/// <summary>反馈列表中的一行显示数据。</summary>
public sealed class SetupFeedbackItemRow(
    string title,
    string user,
    DateTime createdAt,
    string logo,
    string issueType,
    FeedbackItem feedback) : LocalizedObservableObject
{
    public string Title { get; } = title;

    public string Info => $"{user} | {Lang.Date(createdAt)}";

    public string Logo { get; } = logo;

    public string[] Tags { get; } = string.IsNullOrWhiteSpace(issueType) ? [] : [issueType];

    public FeedbackItem Feedback { get; } = feedback;
}

/// <summary>一条 GitHub Issue 反馈。</summary>
public sealed class FeedbackItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string User { get; init; }
    public required string Content { get; init; }
    public required string Url { get; init; }
    public required List<string> Labels { get; init; }
    public bool Closed { get; init; }
    public bool NotPlanned { get; init; }
    public required string Type { get; init; }
    public DateTime CreatedAt { get; init; }
}
