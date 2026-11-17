using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static PCL.ModAnimation;

namespace PCL.Core.Test;

[TestClass]
[DoNotParallelize]
public sealed class AnimationClockTest
{
    [TestMethod]
    public void NonRenderingApplicationStillRunsDelayedCallbacksAndStopsCleanly()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Network.DownloadTestEnvironment.EnsureInitialized();
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                try
                {
                    var frame = new DispatcherFrame();
                    var callbacks = 0;
                    AniStart();
                    var firstTimer = CurrentTimer();
                    AniStart(); // 重复初始化不得创建多条计时回路。
                    Assert.IsFalse(firstTimer!.IsEnabled);
                    Assert.AreNotSame(firstTimer, CurrentTimer());
                    AniStart(AaCode(() => { callbacks++; frame.Continue = false; }, 150), "fallback-callback");
                    var deadline = new DispatcherTimer(DispatcherPriority.Send)
                    {
                        Interval = TimeSpan.FromSeconds(3)
                    };
                    deadline.Tick += (_, _) => frame.Continue = false;
                    deadline.Start();
                    try { Dispatcher.PushFrame(frame); }
                    finally { deadline.Stop(); }
                    Assert.AreEqual(1, callbacks, "The fallback must advance business callbacks without any rendered window.");
                    Assert.IsTrue(aniRunning);
                }
                finally
                {
                    var activeTimer = CurrentTimer();
                    application.Shutdown();
                    var shutdownFrame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                        new Action(() => shutdownFrame.Continue = false));
                    Dispatcher.PushFrame(shutdownFrame);
                    Assert.IsFalse(aniRunning, "Shutdown must detach both frame sources.");
                    Assert.IsFalse(activeTimer!.IsEnabled);
                    Assert.IsNull(CurrentTimer());
                    aniGroups.Clear();
                }
            }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static DispatcherTimer? CurrentTimer() => (DispatcherTimer?)typeof(global::PCL.ModAnimation)
        .GetField("aniFallbackTimer", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
}
