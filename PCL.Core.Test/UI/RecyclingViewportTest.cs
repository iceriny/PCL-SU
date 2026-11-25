using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.UI.Controls;

namespace PCL.Core.Test.UI;

[TestClass]
[DoNotParallelize]
public sealed class RecyclingViewportTest
{
    [TestMethod]
    public async Task KeepsNearbyRowsAndReleasesOffscreenContent()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(Application), AvaloniaTestIsolationLevel.PerTest);
        await session.Dispatch(VerifyScroll, CancellationToken.None);
    }

    private static void VerifyScroll()
    {
        const int rowCount = 200, rowHeight = 40, visibleRows = 5;
        Application.Current!.Styles.Add(new FluentTheme());
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var window = new Window
        {
            Content = scroll, Width = 320, Height = rowHeight * visibleRows,
            WindowDecorations = WindowDecorations.None
        };
        var rows = new MyVirtualizingElement[rowCount];
        int created = 0, released = 0, peak = 0;
        for (int i = 0; i < rowCount; i++)
        {
            var row = new MyVirtualizingElement(() =>
            {
                created++;
                peak = Math.Max(peak, created - released);
                return new Border { Height = rowHeight };
            }) { Height = rowHeight, RecycleOffscreen = true };
            row.Recycled += _ => released++;
            rows[i] = row;
            list.Children.Add(row);
        }

        void ScrollTo(int row)
        {
            scroll.Offset = new Vector(0, row * rowHeight);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        window.Show();
        try
        {
            ScrollTo(0);
            Assert.AreEqual(rowHeight * visibleRows, scroll.Viewport.Height);
            var initial = rows[0].Child;
            Assert.IsNotNull(initial);
            ScrollTo(2);
            var beforeReturn = created;
            ScrollTo(0);
            Assert.AreSame(initial, rows[0].Child);
            Assert.AreEqual(beforeReturn, created);

            for (int i = 1; i <= rowCount - visibleRows; i++)
            {
                ScrollTo(i);
                Assert.IsNotNull(rows[i + visibleRows - 1].Child);
            }
            Assert.IsNull(rows[0].Child);
            Assert.IsTrue(peak <= visibleRows * 3, $"最多应保留视口及上下各一个视口的行，实测 {peak} 行");

            scroll.Content = null;
            Assert.AreEqual(created, released, "列表离树后应释放所有已创建的内容");
        }
        finally
        {
            window.Close();
        }
    }
}
