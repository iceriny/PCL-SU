using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Configuration.Storage;
using PCL.Core.UI;

namespace PCL.Core.Test;

[TestClass]
[DoNotParallelize]
public sealed class ControlInputRegressionTest
{
    [TestMethod]
    public void KeyboardSelectionsAndLoadingResourcesKeepTheirSemantics()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Network.DownloadTestEnvironment.EnsureInitialized();
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources["ColorBrushTransparent"] = Brushes.Transparent;
                application.Resources["MyComboBoxItem"] = new Style(typeof(global::PCL.MyComboBoxItem));
                application.Resources["ColorBrush3"] = Brushes.Gray;
                application.Resources["Application.Control.Loading.Default"] = "Loading";
                application.Resources["Application.Control.Loading.Failed"] = "Failed";
                try
                {
                    var loading = new global::PCL.MyLoading();
                    var resourceHost = new Window { Content = loading };
                    Assert.AreEqual("Loading", loading.Text);
                    Assert.AreEqual("Failed", loading.TextError);
                    application.Resources["Application.Control.Loading.Default"] = "加载中";
                    application.Resources["Application.Control.Loading.Failed"] = "加载失败";
                    Assert.AreEqual("加载中", loading.Text);
                    Assert.AreEqual("加载失败", loading.TextError);
                    loading.Text = "Custom";
                    application.Resources["Application.Control.Loading.Default"] = "Changed";
                    Assert.AreEqual("Custom", loading.Text);
                    resourceHost.Content = null;
                    resourceHost.Close();

                    var combo = new KeyboardComboBox();
                    combo.Items.Add("First");
                    combo.Items.Add("Second");
                    combo.Items.Add("Third");
                    var userChanges = new List<bool>();
                    combo.SelectionChanged += (_, _) => userChanges.Add(combo.IsUserSelectionChange);
                    combo.SelectedIndex = 0;
                    Assert.IsFalse(userChanges[^1]);
                    combo.SendKey(Key.Down);
                    Assert.AreEqual(1, combo.SelectedIndex);
                    Assert.IsTrue(userChanges[^1], "Closed dropdown arrow selection must count as user input.");
                    Assert.IsFalse(combo.IsUserSelectionChange, "The input scope must end with the key event.");
                    combo.SelectedIndex = 2;
                    Assert.IsFalse(userChanges[^1], "Programmatic reloads must not trigger confirmation dialogs.");

                    VerifyNestedKeyboardSelection(combo);
                    VerifyTextSearchSelection(combo);
                    VerifyMouseWheelSelection(combo);
                    VerifyCancelledSliderDrag(application);
                    VerifyStorageStopDoesNotWaitForUi();
                }
                finally { application.Shutdown(); }
            }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyNestedKeyboardSelection(KeyboardComboBox combo)
    {
        combo.SelectedIndex = 0;
        var nested = false;
        SelectionChangedEventHandler handler = (_, _) =>
        {
            if (nested || combo.SelectedIndex != 1) return;
            nested = true;
            combo.SendKey(Key.Up);
            Assert.IsTrue(combo.IsUserSelectionChange,
                "Finishing a nested key event must preserve the outer input scope.");
        };
        combo.SelectionChanged += handler;
        try
        {
            combo.SendKey(Key.Down);
            Assert.IsTrue(nested);
            Assert.AreEqual(0, combo.SelectedIndex);
            Assert.IsFalse(combo.IsUserSelectionChange);
        }
        finally { combo.SelectionChanged -= handler; }
    }

    private static void VerifyTextSearchSelection(KeyboardComboBox combo)
    {
        combo.SelectedIndex = 0;
        var userSelection = false;
        SelectionChangedEventHandler handler = (_, _) => userSelection = combo.IsUserSelectionChange;
        combo.SelectionChanged += handler;
        try
        {
            combo.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                new TextComposition(InputManager.Current, combo, "Third"))
            {
                RoutedEvent = TextCompositionManager.TextInputEvent
            });
            Assert.AreEqual(2, combo.SelectedIndex);
            Assert.IsTrue(userSelection, "Closed dropdown text search must count as user input.");
            Assert.IsFalse(combo.IsUserSelectionChange);
        }
        finally { combo.SelectionChanged -= handler; }
    }

    private static void VerifyMouseWheelSelection(KeyboardComboBox combo)
    {
        // Set the focus precondition without creating or activating a native window.
        var writeFlag = typeof(UIElement).GetMethod("WriteFlag", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var focusFlag = Enum.Parse(writeFlag.GetParameters()[0].ParameterType, "IsKeyboardFocusWithinCache");
        writeFlag.Invoke(combo, [focusFlag, true]);
        Assert.IsTrue(combo.IsKeyboardFocusWithin);
        combo.SelectedIndex = 0;
        var userSelection = false;
        SelectionChangedEventHandler handler = (_, _) => userSelection = combo.IsUserSelectionChange;
        combo.SelectionChanged += handler;
        try
        {
            var timestamp = Environment.TickCount;
            combo.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, timestamp, -120)
            {
                RoutedEvent = Mouse.PreviewMouseWheelEvent
            });
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, timestamp, -120)
            {
                RoutedEvent = Mouse.MouseWheelEvent
            };
            combo.RaiseEvent(wheel);
            Assert.AreEqual(1, combo.SelectedIndex, "The real WPF wheel class handler must change selection.");
            Assert.IsTrue(wheel.Handled);
            Assert.IsTrue(userSelection);
            Assert.IsFalse(combo.IsUserSelectionChange, "Handled bubbling must end the wheel input scope.");

            combo.SelectedIndex = 2;
            Assert.IsFalse(userSelection, "A subsequent programmatic selection must stay outside the input scope.");

            MouseWheelEventHandler suppressBubble = (_, e) => e.Handled = true;
            combo.PreviewMouseWheel += suppressBubble;
            try
            {
                combo.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = Mouse.PreviewMouseWheelEvent
                });
            }
            finally { combo.PreviewMouseWheel -= suppressBubble; }
            var frame = new DispatcherFrame();
            combo.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Assert.IsFalse(combo.IsUserSelectionChange, "A preview event without bubbling must also release its scope.");
        }
        finally
        {
            combo.SelectionChanged -= handler;
            writeFlag.Invoke(combo, [focusFlag, false]);
        }
    }

    private static void VerifyCancelledSliderDrag(Application application)
    {
        application.Resources["ColorBrushSemiTransparent"] = Brushes.Transparent;
        application.Resources["ColorBrushBg0"] = Brushes.Gray;
        var slider = new global::PCL.MySlider();
        slider.PreviewChange += (_, args) => args.handled = true;
        var previousDrag = global::PCL.ModMain.dragControl;
        try
        {
            global::PCL.ModMain.dragControl = slider;
            slider.Value = 50;
            Assert.AreEqual(0, slider.Value, "A cancelled preview must restore the previous value.");
            Assert.IsNull(global::PCL.ModMain.dragControl,
                "Cancelling a slider preview must clear the active drag before returning to DragDoing.");
            Assert.IsFalse(((Popup)slider.FindName("Popup")).IsOpen);
        }
        finally { global::PCL.ModMain.dragControl = previousDrag; }
    }

    private static void VerifyStorageStopDoesNotWaitForUi()
    {
        var reports = 0;
        void OnError(string message, string caption, ICollection<MsgBoxButtonInfo> buttons,
            MsgBoxTheme theme, bool block, ref int result) => reports++;
        MsgBoxWrapper.OnShow += OnError;
        try
        {
            var storage = new FileConfigStorage(new FailingFileProvider());
            storage.SetValue("key", "value");
            storage.Stop();
            Assert.AreEqual(0, reports, "Stop must finish before the UI error dialog is dispatched.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Assert.IsGreaterThan(0, reports);
        }
        finally { MsgBoxWrapper.OnShow -= OnError; }
    }

    private sealed class FailingFileProvider : IKeyValueFileProvider
    {
        public string FilePath => "test://config";
        public T Get<T>(string key) => default!;
        public bool Exists(string key) => false;
        public void Set<T>(string key, T value) { }
        public void Remove(string key) { }
        public void Sync() => throw new IOException("Expected write failure");
    }

    private sealed class KeyboardComboBox : global::PCL.MyComboBox
    {
        internal void SendKey(Key key) => OnKeyDown(new KeyEventArgs(
            Keyboard.PrimaryDevice, new HeadlessPresentationSource(this), Environment.TickCount, key)
            { RoutedEvent = Keyboard.KeyDownEvent });
    }
}

internal sealed class HeadlessPresentationSource(Visual root) : PresentationSource
{
    public override Visual RootVisual { get; set; } = root;
    public override bool IsDisposed => false;
    protected override CompositionTarget GetCompositionTargetCore() => null!;
}
