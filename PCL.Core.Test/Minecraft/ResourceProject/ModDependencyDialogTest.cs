using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.ResourceProject;
using Dialog = PCL.MyMsgCompDependency;

namespace PCL.Core.Test.Minecraft.ResourceProject;

[TestClass]
[DoNotParallelize]
public sealed class ModDependencyDialogTest
{
    [TestMethod]
    public void CheckingStaysCancellableAndLateResultsCannotReplaceTheSelection()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Network.DownloadTestEnvironment.EnsureInitialized();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources["ColorBrushSemiTransparent"] = Brushes.Transparent;
                application.Resources["ColorBrush1"] = Brushes.Gray;
                application.Resources["ColorBrush3"] = Brushes.Gray;
                application.Resources["ColorBrush4"] = Brushes.Gray;
                application.Resources["Download.Comp.Dependency.Unresolved.Message"] = "Unresolved: {0}";
                application.Resources["Download.Comp.Dependency.Unresolved.ListItem"] = "{0} {1}: {2}";
                try { RunScenario(); }
                finally { application.Shutdown(); }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                SynchronizationContext.SetSynchronizationContext(null);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(25)), "The dependency dialog scenario did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RunScenario()
    {
        IProgress<ModDependencyProgress>? progress = null;
        var lateResult = new TaskCompletionSource<ModDependencyResolutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var converter = new global::PCL.ModMain.MyMsgBoxConverter
        {
            DependencyResolver = (token, reporter) =>
            {
                observedToken = token;
                progress = reporter;
                return lateResult.Task;
            }
        };
        var checking = new Dialog(converter);
        var check = BeginCheck(checking);
        PumpUntil(() => progress is not null);
        Assert.AreEqual(Visibility.Collapsed, Element<FrameworkElement>(checking, "BtnWithDependencies").Visibility);
        Assert.AreEqual(Visibility.Collapsed, Element<FrameworkElement>(checking, "BtnCurrentOnly").Visibility);
        checking.Confirm();
        Assert.IsFalse(converter.IsExited, "Enter must not start a download during dependency checks.");

        progress!.Report(new(ModDependencyStage.ResolvingDependencies, 3, 1));
        PumpUntil(() => Field<double>(checking, "_targetProgress") > 0.7);
        for (var index = 0; index < 80; index++) Tick(checking);
        var beforeDiscovery = Element<ScaleTransform>(checking, "ProgressScale").ScaleX;
        progress.Report(new(ModDependencyStage.ResolvingDependencies, 3, 20));
        Pump();
        for (var index = 0; index < 80; index++) Tick(checking);
        Assert.IsTrue(Element<ScaleTransform>(checking, "ProgressScale").ScaleX >= beforeDiscovery,
            "Discovering more transitive dependencies must not move progress backwards.");
        for (var index = 0; index < 30000; index++) Tick(checking);
        Assert.IsTrue(Element<ScaleTransform>(checking, "ProgressScale").ScaleX <= 0.99, "An active check must never display completion.");

        checking.Cancel();
        StopClose(checking);
        Assert.IsTrue(observedToken.IsCancellationRequested);
        var cancelled = (Dialog.Selection)converter.Result;
        Assert.AreEqual(global::PCL.ModComp.CompDepsInstallTypes.Cancel, cancelled.Choice);
        lateResult.SetResult(new ModDependencyResolutionResult());
        PumpUntil(() => check.IsCompleted);
        Assert.AreSame(cancelled, converter.Result);
        Assert.AreEqual(Visibility.Collapsed, Element<FrameworkElement>(checking, "PanResult").Visibility);
        Assert.IsFalse(Field<bool>(checking, "_ready"), "A late result must not reopen a cancelled confirmation.");

        var resolved = new ModDependencyResolutionResult();
        resolved.ToInstall.Add(new ResolvedDependencyInstall
        {
            ProjectId = "dependency", ProjectName = "Dependency", Source = "Modrinth",
            File = new ModDependencyFile { Id = "file", DisplayName = "Dependency file", Version = "1.0" }
        });
        var completedConverter = new global::PCL.ModMain.MyMsgBoxConverter
        {
            DependencyResolver = (_, _) => Task.FromResult(resolved)
        };
        var completed = new Dialog(completedConverter);
        var completedCheck = BeginCheck(completed);
        PumpUntil(() => completedCheck.IsCompleted);
        AdvanceResultAnimation();
        Assert.AreEqual(Visibility.Collapsed, Element<FrameworkElement>(completed, "PanChecking").Visibility);
        Assert.AreEqual(Visibility.Visible, Element<FrameworkElement>(completed, "PanResult").Visibility);
        Assert.AreEqual(Visibility.Visible, Element<FrameworkElement>(completed, "BtnWithDependencies").Visibility);
        Assert.AreEqual(Visibility.Visible, Element<FrameworkElement>(completed, "BtnCurrentOnly").Visibility);
        Assert.AreEqual(1d, Element<ScaleTransform>(completed, "ProgressScale").ScaleX, 0.000001);
        completed.Confirm();
        StopClose(completed);
        Assert.AreEqual(global::PCL.ModComp.CompDepsInstallTypes.WithDeps,
            ((Dialog.Selection)completedConverter.Result).Choice);

        var satisfiedConverter = new global::PCL.ModMain.MyMsgBoxConverter
        {
            DependencyResolver = (_, _) => Task.FromResult(new ModDependencyResolutionResult())
        };
        var satisfied = new Dialog(satisfiedConverter);
        var satisfiedCheck = BeginCheck(satisfied);
        PumpUntil(() => satisfiedCheck.IsCompleted);
        AdvanceResultAnimation();
        Assert.AreEqual(Visibility.Collapsed, Element<FrameworkElement>(satisfied, "BtnWithDependencies").Visibility);
        Assert.AreEqual(Visibility.Visible, Element<FrameworkElement>(satisfied, "BtnCurrentOnly").Visibility);
        satisfied.Confirm();
        StopClose(satisfied);
        Assert.AreEqual(global::PCL.ModComp.CompDepsInstallTypes.WithoutDeps,
            ((Dialog.Selection)satisfiedConverter.Result).Choice);

        var unresolved = new ModDependencyResolutionResult();
        unresolved.ToInstall.Add(resolved.ToInstall[0]);
        unresolved.Unresolved.Add(new UnresolvedDependency
        {
            ProjectId = "missing", Source = "Modrinth", Reason = "No compatible version"
        });
        var unresolvedConverter = new global::PCL.ModMain.MyMsgBoxConverter
        {
            DependencyResolver = (_, _) => Task.FromResult(unresolved)
        };
        var incomplete = new Dialog(unresolvedConverter);
        var unresolvedCheck = BeginCheck(incomplete);
        PumpUntil(() => unresolvedCheck.IsCompleted);
        AdvanceResultAnimation();
        Assert.AreEqual(Visibility.Collapsed, Element<FrameworkElement>(incomplete, "BtnWithDependencies").Visibility);
        StringAssert.Contains(Element<TextBox>(incomplete, "LabResult").Text, "No compatible version");
        incomplete.Confirm();
        StopClose(incomplete);
        Assert.AreEqual(global::PCL.ModComp.CompDepsInstallTypes.WithoutDeps,
            ((Dialog.Selection)unresolvedConverter.Result).Choice,
            "Unresolved dependencies must only offer the current mod, even if some other dependencies resolved.");
    }

    private static Task BeginCheck(Dialog dialog) => (Task)typeof(Dialog)
        .GetMethod("CheckAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null)!;

    private static T Field<T>(Dialog dialog, string name) => (T)typeof(Dialog)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;

    private static T Element<T>(Dialog dialog, string name) => (T)dialog.FindName(name);

    private static void Tick(Dialog dialog) => typeof(Dialog)
        .GetMethod("ProgressTick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [null, EventArgs.Empty]);

    private static void StopClose(Dialog dialog) => global::PCL.ModAnimation.AniStop("MyMsgBox " + Field<int>(dialog, "_uuid"));

    private static void AdvanceResultAnimation()
    {
        for (var index = 0; index < 20; index++)
        {
            global::PCL.ModAnimation.AniStart(Array.Empty<global::PCL.ModAnimation.AniData>(),
                "CompDependency Test Clock", refreshTime: true);
            global::PCL.ModAnimation.AniTimer(40);
        }
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var watch = Stopwatch.StartNew();
        while (!complete() && watch.Elapsed < TimeSpan.FromSeconds(5)) Pump();
        Assert.IsTrue(complete(), "The dependency dialog did not reach its expected state.");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
