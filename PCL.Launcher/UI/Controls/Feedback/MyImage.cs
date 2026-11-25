using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PCL.Core.App;
using PCL.Core.IO.Net.Http;
using PCL.Core.Logging;
using PCL.Core.Utils;

namespace PCL.UI.Controls;

/// <summary>
///     带网络回退与本地缓存的图片控件。
///     <see cref="Source" /> 为字符串：本地路径 / <c>avares://</c> / <c>file://</c> 直接显示；
///     以 <c>http</c> 开头时先查缓存，再下载到 <see cref="Paths.Temp" /> 下的图片缓存目录，
///     下载期间显示 <see cref="LoadingSource" />，失败时尝试 <see cref="FallbackSource" />。
/// </summary>
public class MyImage : Image
{
    /// <summary>
    ///     与 <see cref="Image.Source" /> 类似，但以字符串形式描述图片地址。
    /// </summary>
    public new static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<MyImage, string?>(nameof(Source));

    public static readonly StyledProperty<bool> EnableCacheProperty =
        AvaloniaProperty.Register<MyImage, bool>(nameof(EnableCache), true);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<MyImage, CornerRadius>(
            nameof(CornerRadius),
            new CornerRadius(-1d));

    /// <summary>按目标显示宽度解码，避免高清原图长期占用内存。</summary>
    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<MyImage, int>(nameof(DecodeWidth));

    /// <summary>按目标显示高度解码；与 <see cref="DecodeWidth" /> 同时设置时优先宽度。</summary>
    public static readonly StyledProperty<int> DecodeHeightProperty =
        AvaloniaProperty.Register<MyImage, int>(nameof(DecodeHeight));

    /// <summary>缩略图槽的逻辑边长；按当前屏幕缩放限制解码后的长边。</summary>
    public static readonly StyledProperty<double> DecodeBoxSizeProperty =
        AvaloniaProperty.Register<MyImage, double>(nameof(DecodeBoxSize), validate: size => double.IsFinite(size) && size >= 0);

    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> DownloadTasks = new();

    private string? _actualSource;
    private string? _directSource;
    private bool _isAttached;
    private TopLevel? _scaleRoot;
    private int _sourceGeneration;
    private int _decodeGeneration;
    private ThumbnailCache.Lease? _lease;

    /// <summary>
    ///     网络图片缓存的有效期。
    /// </summary>
    public TimeSpan fileCacheExpiredTime = TimeSpan.FromDays(14d);

    public MyImage()
    {
        FeedbackTheme.AttachStyles(this);
    }

    /// <summary>
    ///     实际被呈现的图片地址（本地文件、<c>avares://</c> 或下载后的缓存路径）。
    /// </summary>
    public string? ActualSource
    {
        get => _actualSource ?? _directSource;
        set
        {
            _sourceGeneration++;
            _directSource = string.IsNullOrEmpty(value) ? null : value;
            Display(_isAttached ? _directSource : null);
        }
    }

    public bool EnableCache
    {
        get => GetValue(EnableCacheProperty);
        set => SetValue(EnableCacheProperty, value);
    }

    public int DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, Math.Max(0, value));
    }

    public int DecodeHeight
    {
        get => GetValue(DecodeHeightProperty);
        set => SetValue(DecodeHeightProperty, Math.Max(0, value));
    }

    public double DecodeBoxSize
    {
        get => GetValue(DecodeBoxSizeProperty);
        set => SetValue(DecodeBoxSizeProperty, value);
    }

    /// <summary>
    ///     与 Image 的 Source 类似。
    ///     若输入以 http 开头的字符串，则会尝试下载图片然后显示，图片会保存为本地缓存。
    /// </summary>
    public new string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>
    ///     当 Source 首次下载失败时，会从该备用地址加载图片。
    /// </summary>
    public string FallbackSource { get; set; } = string.Empty;

    /// <summary>
    ///     正在下载网络图片时显示的本地占位图，默认使用内置的 <c>NoIcon.png</c>。
    /// </summary>
    public string LoadingSource { get; set; } =
        "avares://PCL.Launcher/Assets/Images/Icons/NoIcon.png";

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty || change.Property == EnableCacheProperty)
        {
            _directSource = null;
            if (_isAttached) Load();
            else if (_actualSource is not null) Unload();
        }
        else if (change.Property == CornerRadiusProperty || change.Property == BoundsProperty)
            UpdateClip();
        else if (change.Property == DecodeWidthProperty || change.Property == DecodeHeightProperty
                 || change.Property == DecodeBoxSizeProperty)
        {
            UpdateScaleSubscription();
            if (!_isAttached) Display(null);
            else if (_actualSource is not null) Display(_actualSource);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        UpdateScaleSubscription();
        if (_directSource is not null) Display(_directSource);
        else Load();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        UpdateScaleSubscription();
        _sourceGeneration++;
        // 导航会暂时摘下整页；原尺寸图跟随页面保留，下次挂载重新核对文件版本。
        if (_lease?.Original is null) Display(null);
        else _decodeGeneration++;
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateScaleSubscription()
    {
        var root = _isAttached && DecodeBoxSize > 0 ? TopLevel.GetTopLevel(this) : null;
        if (ReferenceEquals(root, _scaleRoot)) return;
        if (_scaleRoot is not null) _scaleRoot.ScalingChanged -= OnScalingChanged;
        _scaleRoot = root;
        if (root is not null) root.ScalingChanged += OnScalingChanged;
    }

    private void OnScalingChanged(object? sender, EventArgs args)
    {
        if (_actualSource is not null) Display(_actualSource);
    }

    private void Load()
    {
        var generation = ++_sourceGeneration;
        var source = Source;
        if (string.IsNullOrEmpty(source))
        {
            Display(null);
            return;
        }

        if (!source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            Display(source);
            return;
        }

        var tempPath = GetTempPath(source);
        var tempFile = new FileInfo(tempPath);
        var enableCache = EnableCache;
        var hasCachedFile = enableCache && tempFile.Exists;
        if (hasCachedFile)
        {
            var fresh = DateTime.Now - tempFile.LastWriteTime < fileCacheExpiredTime;
            Display(tempPath, retryDownload: fresh);
            if (fresh)
                return; // 无需刷新缓存
        }
        else
            Display(LoadingSource);
        _ = LoadFromNetworkAsync(source, generation, hasCachedFile);
    }

    private async Task LoadFromNetworkAsync(string url, int generation, bool hasCachedFile)
    {
        var fallback = FallbackSource;
        try
        {
            var result = await DownloadImageAsync(url).ConfigureAwait(false);
            if (string.IsNullOrEmpty(result) && !hasCachedFile && !string.IsNullOrEmpty(fallback))
                result = fallback.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? await DownloadImageAsync(fallback).ConfigureAwait(false) : fallback;
            if (!string.IsNullOrEmpty(result))
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation == _sourceGeneration) Display(result);
                });
        }
        catch (Exception ex)
        {
            LogWrapper.Debug(
                ex,
                "MyImage",
                $"在线图片获取失败（source = {url}, fallback = {FallbackSource}）");
        }
    }

    private void Display(string? source, bool retryDownload = false)
    {
        var generation = ++_decodeGeneration;
        if (string.IsNullOrEmpty(source))
        {
            _actualSource = null;
            ReplaceImage(null);
            return;
        }

        if (_actualSource != source) ReplaceImage(null);
        _actualSource = source;
        var box = (int)Math.Ceiling(DecodeBoxSize * (_scaleRoot?.RenderScaling ?? 1d));
        _ = LoadActualSourceAsync(source, generation, DecodeWidth, DecodeHeight, box, retryDownload);
    }

    private async Task LoadActualSourceAsync(string source, int generation, int width, int height, int box,
        bool retryDownload)
    {
        var previous = width == 0 && height == 0 && box == 0 ? _lease?.Original : null;
        ThumbnailCache.Lease? lease = null;
        try
        {
            lease = await Task
                .Run(() => generation != Volatile.Read(ref _decodeGeneration) || previous?.IsCurrent() == true
                    ? null : ThumbnailCache.Shared.Acquire(source, width, height, box))
                .ConfigureAwait(false);
            if (lease is null) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation == _decodeGeneration)
                {
                    ReplaceImage(lease);
                    lease = null;
                }
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _decodeGeneration) return;
                LogWrapper.Debug(ex, "MyImage", $"加载图片失败（{source}）");
                ReplaceImage(null);
                if (retryDownload && Source is { } url)
                    _ = LoadFromNetworkAsync(url, _sourceGeneration, false);
            });
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>释放控件对位图的引用，并使已排队的下载和解码结果失效。</summary>
    public void Unload()
    {
        _sourceGeneration++;
        _directSource = null;
        Display(null);
    }

    private void ReplaceImage(ThumbnailCache.Lease? lease)
    {
        var previous = _lease;
        base.Source = lease?.Bitmap;
        _lease = lease;
        previous?.Dispose();
    }

    private void UpdateClip()
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var cornerRadius = CornerRadius;
        if (width > 0d
            && height > 0d
            && cornerRadius is { TopLeft: >= 0d, TopRight: >= 0d })
            Clip = SuperellipseGeometry.Create(new Rect(0d, 0d, width, height), cornerRadius);
        else
            Clip = null;
    }

    // =======================================
    // 下载
    // =======================================

    public static async Task<string> DownloadImageAsync(string url)
    {
        var download = DownloadTasks.GetOrAdd(url,
            key => new Lazy<Task<string>>(() => DownloadImageInternalAsync(key)));
        try
        {
            return await download.Value.ConfigureAwait(false);
        }
        finally
        {
            DownloadTasks.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(url, download));
        }
    }

    public static string GetTempPath(string url)
    {
        return Path.Combine(Paths.Temp, "Cache", "Images", $"{GetStringMd5(url)}.png");
    }

    private static async Task<string> DownloadImageInternalAsync(string url)
    {
        var tempPath = GetTempPath(url);
        var tempDownloadingPath = tempPath + RandomUtils.NextInt(0, 1000000);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!); // 重新实现下载，以避免携带 Header
            await using (var fileStream = new FileStream(
                             tempDownloadingPath,
                             FileMode.Create,
                             FileAccess.ReadWrite,
                             FileShare.Read))
            {
                using var response = await HttpRequest
                    .Create(url)
                    .SendAsync(addMetedata: false)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var networkStream =
                    await response.AsStreamAsync().ConfigureAwait(false);
                fileStream.SetLength(0L);
                await networkStream.CopyToAsync(fileStream).ConfigureAwait(false);
            }

            File.Move(tempDownloadingPath, tempPath, true);
            return tempPath;
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(tempDownloadingPath)) File.Delete(tempDownloadingPath);
            }
            catch
            {
                // 清理失败不影响后续流程
            }

            LogWrapper.Debug(ex, "MyImage", $"获取在线图片失败（url = {url}, dest = {tempPath}）");
            return string.Empty;
        }
    }

    private static string GetStringMd5(string text)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
