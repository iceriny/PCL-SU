using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test;

[TestClass]
public class SetupFeedbackTest
{
    [TestMethod]
    public void RepositoryLabelsAndIssueStateKeepOrdinaryReportsVisible()
    {
        var response = JsonNode.Parse("""
            [
              {"number": 1, "state": "open", "labels": [{"id": 7, "name": "bug"}]},
              {"number": 2, "state": "closed", "state_reason": "completed"},
              {"number": 3, "state": "closed", "state_reason": "not_planned"},
              {"number": 4, "state": "open", "labels": [{"id": 29, "name": "IN PROGRESS"}]},
              {"number": 5, "state": "open", "pull_request": {}}
            ]
            """)!.AsArray();
        var view = new PageSetupFeedbackViewModel();
        var issues = PageSetupFeedbackViewModel.ParseFeedbackList(response);
        view.SetItems(issues);

        Assert.HasCount(4, issues);
        Assert.AreEqual("1", view.WaitingProcessRows[0].Feedback.Id);
        Assert.AreEqual("2", view.CompletedRows[0].Feedback.Id);
        Assert.AreEqual("3", view.DeclineRows[0].Feedback.Id);
        Assert.AreEqual("4", view.ProcessingRows[0].Feedback.Id);
    }
}
