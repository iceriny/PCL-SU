using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace PCL.UI.Controls;

/// <summary>
///     首次可见时创建内容。启用回收的行保留布局位置，在视口附近复用已创建的控件。
/// </summary>
public class MyVirtualizingElement : Decorator
{
    private readonly Func<Control> _initializer;
    private Control? _realControl;
    private RecyclingViewport? _viewport;
    private Size? _measuredSize;

    public MyVirtualizingElement(Func<Control> initializer)
    {
        _initializer = initializer;
    }

    /// <summary>是否在离屏后回收真实控件。默认关闭，保持原有懒加载行为。</summary>
    public bool RecycleOffscreen { get; set; }

    /// <summary>变高条目首次测量前的高度；回收后保留最近一次实测尺寸。</summary>
    public double EstimatedHeight { get; init; }

    /// <summary>真实控件被回收时触发，用于释放图片等资源。</summary>
    public event Action<Control>? Recycled;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (RecycleOffscreen && Parent is Panel panel)
        {
            _viewport = RecyclingViewport.For(panel);
            _viewport.Register(this);
        }
        else
        {
            EffectiveViewportChanged += OnViewportChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EffectiveViewportChanged -= OnViewportChanged;
        _viewport?.Unregister(this);
        _viewport = null;
        if (RecycleOffscreen) Recycle();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (e.EffectiveViewport.Intersects(new Rect(Bounds.Size))) Init();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null) return _measuredSize ?? new Size(0, EstimatedHeight);
        var size = base.MeasureOverride(availableSize);
        _measuredSize = size;
        return size;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) _viewport?.InvalidateGeometry();
    }

    /// <summary>获取真实控件。普通懒加载项会直接替换占位节点。</summary>
    public virtual Control Init()
    {
        var element = _realControl ??= _initializer();
        if (RecycleOffscreen)
        {
            if (Child is null)
            {
                Child = element;
                _viewport?.TrackRealized(this);
            }
            return element;
        }
        if (Parent is null)
            return element;
        if (Parent is not Panel panel)
            throw new Exception("MyVirtualizingElement 的父级必须是一个 Panel");

        var index = panel.Children.IndexOf(this);
        if (index >= 0)
        {
            panel.Children.RemoveAt(index);
            panel.Children.Insert(index, element);
        }

        return element;
    }

    internal void Recycle()
    {
        var real = _realControl;
        _realControl = null;
        if (real is null) return;

        Recycled?.Invoke(real);
        Child = null;
    }

    /// <summary>
    ///     获取实例化后的控件；若尚未实例化则立即实例化。
    /// </summary>
    public static Control TryInit(Control element)
    {
        return element is MyVirtualizingElement virtualizing ? virtualizing.Init() : element;
    }
}

/// <summary>懒加载占位控件的泛型形式。</summary>
public class MyVirtualizingElement<T>(
    Func<T> initializer)
    : MyVirtualizingElement(initializer) where T : Control
{
    public override T Init()
    {
        return (T)base.Init();
    }

    public static implicit operator T(MyVirtualizingElement<T> virtualized)
    {
        return virtualized.Init();
    }
}
