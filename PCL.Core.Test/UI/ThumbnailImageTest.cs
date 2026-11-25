using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.UI.Controls;

namespace PCL.Core.Test.UI;

[TestClass]
[DoNotParallelize]
public sealed class ThumbnailImageTest
{
    private const string Icon = "avares://PCL.Launcher/Assets/Images/icon.png";
    private const string Placeholder = "avares://PCL.Launcher/Assets/Images/Icons/NoIcon.png";

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    [TestMethod]
    public void ScalesAndSharesThumbnailsWithoutInvalidatingOtherControls()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThumbnailImageTest), AvaloniaTestIsolationLevel.PerTest);
        session.Dispatch(() =>
        {
            var first = new MyImage { ActualSource = Icon, DecodeBoxSize = 50 };
            Assert.IsNull(BitmapOf(first));
            var second = new MyImage { Source = Icon, DecodeBoxSize = 50 };
            var rows = new StackPanel { Children = { first, second } };
            var window = new Window { Content = rows };
            try
            {
                window.Show();
                window.SetRenderScaling(1.25);
                WaitFor(() => BitmapOf(first)?.PixelSize == new PixelSize(63, 63)
                    && BitmapOf(second)?.PixelSize == new PixelSize(63, 63));
                Assert.AreSame(BitmapOf(first), BitmapOf(second));
                rows.Children.Remove(first);
                Assert.IsNull(BitmapOf(first));
                Assert.AreEqual(new PixelSize(63, 63), BitmapOf(second)!.PixelSize);

                var cached = BitmapOf(second);
                rows.Children.Clear();
                rows.Children.Add(first);
                WaitFor(() => ReferenceEquals(cached, BitmapOf(first)));
                window.SetRenderScaling(2);
                WaitFor(() => BitmapOf(first)?.PixelSize == new PixelSize(100, 100));
            }
            finally { window.Close(); }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void DetachedOriginalIsReusedAndFileReplacementAppearsOnReattach()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThumbnailImageTest), AvaloniaTestIsolationLevel.PerTest);
        session.Dispatch(() =>
        {
            var path = Path.GetTempFileName();
            var image = new MyImage { Source = path };
            var window = new Window { Content = image };
            try
            {
                CopyAsset(Icon, path);
                window.Show();
                WaitFor(() => BitmapOf(image)?.PixelSize == new PixelSize(512, 512));
                var original = BitmapOf(image);
                window.Content = null;
                window.Content = image;
                Assert.AreSame(original, BitmapOf(image));
                window.Content = null;
                CopyAsset(Placeholder, path);
                window.Content = image;
                WaitFor(() => BitmapOf(image)?.PixelSize == new PixelSize(128, 128));
                Assert.AreNotSame(original, BitmapOf(image));
                image.Unload();
                Assert.IsNull(BitmapOf(image));
            }
            finally
            {
                window.Close();
                File.Delete(path);
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void CopyAsset(string asset, string path)
    {
        using var input = AssetLoader.Open(new Uri(asset));
        using var output = File.Create(path);
        input.CopyTo(output);
    }

    private static Bitmap? BitmapOf(Image image) => image.Source as Bitmap;

    private static void WaitFor(Func<bool> condition) => Assert.IsTrue(SpinWait.SpinUntil(() =>
    {
        Dispatcher.UIThread.RunJobs();
        return condition();
    }, TimeSpan.FromSeconds(5)), "图片未在指定时间内完成更新");
}
