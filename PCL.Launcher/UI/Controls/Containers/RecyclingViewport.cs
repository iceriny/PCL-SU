using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace PCL.UI.Controls;

/// <summary>同一面板只监听一次视口变化；滚动时按行的位置查找可见项。</summary>
internal sealed class RecyclingViewport
{
    private static readonly ConditionalWeakTable<Panel, RecyclingViewport> Viewports = new();
    private readonly HashSet<MyVirtualizingElement> _rows = [];
    private readonly List<MyVirtualizingElement> _realized = [];
    private readonly Panel _panel;
    private (MyVirtualizingElement Row, double MaxBottom)[] _positions = [];
    private Rect _viewport;
    private bool _geometryChanged;
    private bool _pending;

    private RecyclingViewport(Panel panel)
    {
        _panel = panel;
        panel.EffectiveViewportChanged += OnViewportChanged;
    }

    public static RecyclingViewport For(Panel panel) =>
        Viewports.GetValue(panel, static p => new RecyclingViewport(p));

    public void Register(MyVirtualizingElement row)
    {
        _rows.Add(row);
        if (row.Child is not null) TrackRealized(row);
        InvalidateGeometry();
    }

    public void Unregister(MyVirtualizingElement row)
    {
        _realized.Remove(row);
        _rows.Remove(row);
        if (_rows.Count == 0)
        {
            _panel.EffectiveViewportChanged -= OnViewportChanged;
            Viewports.Remove(_panel);
            _positions = [];
        }
        else
        {
            InvalidateGeometry();
        }
    }

    public void TrackRealized(MyVirtualizingElement row) => _realized.Add(row);

    public void InvalidateGeometry()
    {
        _geometryChanged = true;
        ScheduleUpdate();
    }

    private void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _viewport = e.EffectiveViewport;
        ScheduleUpdate();
    }

    private void ScheduleUpdate()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.UIThread.Post(Update, DispatcherPriority.Loaded);
    }

    private void Update()
    {
        _pending = false;
        if (_rows.Count == 0) return;
        if (_geometryChanged)
        {
            _geometryChanged = false;
            // 瀑布流中较早的长卡片可能跨过后面数行，记录截至各行的最大底边。
            var bottom = double.NegativeInfinity;
            var index = 0;
            _positions = new (MyVirtualizingElement, double)[_rows.Count];
            foreach (var row in _rows.OrderBy(row => row.Bounds.Top))
            {
                bottom = Math.Max(bottom, row.Bounds.Bottom);
                _positions[index++] = (row, bottom);
            }
        }

        var retained = _viewport.Inflate(new Thickness(0, _viewport.Height));
        for (var i = _realized.Count - 1; i >= 0; i--)
        {
            var row = _realized[i];
            if (retained.Intersects(row.Bounds)) continue;
            _realized.RemoveAt(i);
            row.Recycle();
        }

        var first = 0;
        var end = _positions.Length;
        while (first < end)
        {
            var mid = first + (end - first) / 2;
            if (_positions[mid].MaxBottom <= _viewport.Top) first = mid + 1;
            else end = mid;
        }

        for (var i = first; i < _positions.Length; i++)
        {
            var row = _positions[i].Row;
            if (row.Bounds.Top >= _viewport.Bottom) break;
            if (row.Child is null && _viewport.Intersects(row.Bounds)) row.Init();
        }
    }
}
