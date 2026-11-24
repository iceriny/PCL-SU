using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;
using PCL.Core.UI.Dialogs;
using PCL.Core.Utils;
using PCL.UI.Dialogs;

namespace PCL.Core.Test.Updates;

[TestClass]
public sealed class EmbeddedReleaseNotesTest
{
    [TestMethod]
    public void MarkdownDialogShowsSummaryTextAndPreservesLiteralTagsInCode()
    {
        var dialog = new MyMsgMarkdown(new DialogViewModel(new MarkdownDialogRequest
        {
            Title = "更新日志",
            Content = "正文\n<summary>本次更新摘要</summary>\n```\n<summary>示例</summary>\n```"
        }));
        var text = dialog.GetLogicalDescendants().OfType<SelectableTextBlock>()
            .Select(block => block.Text?.TrimEnd('\r', '\n')).ToArray();
        CollectionAssert.Contains(text, "本次更新摘要");
        CollectionAssert.Contains(text, "<summary>示例</summary>");
    }

    [TestMethod]
    public void LauncherEmbedsNotesForItsExactBuild()
    {
        const string prefix = "PCL.ReleaseNotes.";
        var launcher = typeof(global::PCL.Views.MainWindow).Assembly;
        using var metadata = launcher.GetManifestResourceStream("PCL.metadata.json");
        Assert.IsNotNull(metadata);
        var version = JsonSerializer.Deserialize<MetadataModel>(metadata)!.Version;
        Assert.IsTrue(LauncherVersionComparer.TryParseBuildMetadata(version.BuildMetadata, out _, out _));

        var names = launcher.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        Assert.HasCount(5, names);
        Assert.IsTrue(names.All(name =>
            LauncherVersionComparer.Compare(name[prefix.Length..^3], version.FullName) <= 0));
        using var releaseNotes = launcher.GetManifestResourceStream(prefix + version.FullName + ".md");
        Assert.IsNotNull(releaseNotes);
        using var reader = new StreamReader(releaseNotes);
        StringAssert.Contains(reader.ReadToEnd(), "<summary>");
    }
}
