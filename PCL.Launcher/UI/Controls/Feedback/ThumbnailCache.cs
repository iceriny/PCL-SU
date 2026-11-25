using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace PCL.UI.Controls;

/// <summary>显示中的位图由控件持有；最近释放的缩略图按像素字节数保留，以便回滚时复用。</summary>
internal sealed class ThumbnailCache(long idleByteLimit)
{
    public static ThumbnailCache Shared { get; } = new(16 * 1024 * 1024);

    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly LinkedList<Key> _idle = [];
    private long _idleBytes;

    internal readonly record struct SourceVersion(string Path, long WriteTime, long Length)
    {
        public bool IsCurrent()
        {
            if (Path.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)) return true;
            var file = new FileInfo(Path);
            return file.Exists && file.Length == Length && file.LastWriteTimeUtc.Ticks == WriteTime;
        }
    }

    private readonly record struct Key(SourceVersion Version, int Width, int Height, int Box);

    private sealed class Entry(Func<Bitmap> decode)
    {
        public readonly Lazy<Bitmap> Bitmap = new(decode);
        public int Users;
        public long Bytes;
        public LinkedListNode<Key>? IdleNode;
    }

    internal sealed class Lease(Bitmap bitmap, Action release, SourceVersion? original = null) : IDisposable
    {
        private Action? _release = release;
        public Bitmap Bitmap { get; } = bitmap;
        public SourceVersion? Original { get; } = original;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    // 在后台解码；锁只保护索引，不持锁访问磁盘或等待其他调用的解码。
    public Lease Acquire(string source, int width, int height, int box)
    {
        var asset = source.StartsWith("avares://", StringComparison.OrdinalIgnoreCase);
        if (!asset)
            source = Path.GetFullPath(source.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(source).LocalPath : source);
        using var stream = asset ? AssetLoader.Open(new Uri(source))
            : new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        // 从同一打开的文件句柄取得版本和像素，允许下载线程原子替换同名文件。
        var file = stream as FileStream;
        var version = new SourceVersion(source, file is null ? 0 : File.GetLastWriteTimeUtc(file.SafeFileHandle).Ticks,
            file?.Length ?? 0);
        if (width == 0 && height == 0 && box == 0)
        {
            var original = new Bitmap(stream);
            return new Lease(original, original.Dispose, version);
        }

        var key = new Key(version, width, height, box);
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry(() => Decode(stream, width, height, box));
                _entries.Add(key, entry);
            }
            if (entry.IdleNode is { } node)
            {
                _idle.Remove(node);
                entry.IdleNode = null;
                _idleBytes -= entry.Bytes;
            }
            entry.Users++;
        }

        try
        {
            return new Lease(entry.Bitmap.Value, () => Release(key, entry));
        }
        catch
        {
            Release(key, entry);
            throw;
        }
    }

    private static Bitmap Decode(Stream stream, int width, int height, int box)
    {
        if (box > 0)
        {
            // 正方形图标槽同时限制两边，小图保持原始像素，宽高比由解码器保留。
            using var header = new SKManagedStream(stream, disposeManagedStream: false);
            var info = SKBitmap.DecodeBounds(header);
            stream.Position = 0;
            if (Math.Max(info.Width, info.Height) <= box) return new Bitmap(stream);
            if (info.Width >= info.Height) return Bitmap.DecodeToWidth(stream, box);
            return Bitmap.DecodeToHeight(stream, box);
        }

        return width > 0 ? Bitmap.DecodeToWidth(stream, width) : Bitmap.DecodeToHeight(stream, height);
    }

    private void Release(Key key, Entry entry)
    {
        lock (_entries)
        {
            if (--entry.Users != 0) return;
            if (!entry.Bitmap.IsValueCreated)
            {
                _entries.Remove(key);
                return;
            }

            var bitmap = entry.Bitmap.Value;
            var size = bitmap.PixelSize;
            entry.Bytes = (long)size.Width * size.Height * 4;
            if (entry.Bytes > idleByteLimit)
            {
                _entries.Remove(key);
                bitmap.Dispose();
                return;
            }
            entry.IdleNode = _idle.AddLast(key);
            _idleBytes += entry.Bytes;
            while (_idleBytes > idleByteLimit && _idle.First is { } oldest)
            {
                var evicted = _entries[oldest.Value];
                _idle.RemoveFirst();
                _entries.Remove(oldest.Value);
                _idleBytes -= evicted.Bytes;
                evicted.Bitmap.Value.Dispose();
            }
        }
    }
}
