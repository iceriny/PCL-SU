namespace PCL;

/// <summary>
///     本地资源批量更新占用登记：资源页在更新期间登记目录，业务层据此等待更新完成。
///     这是业务状态，不属于 UI 平台适配层。
/// </summary>
public static class CompLocalUpdateRegistry
{
    private static readonly Dictionary<string, int> Updating = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Lock = new();

    public static void Begin(string compPath)
    {
        if (string.IsNullOrEmpty(compPath)) return;
        lock (Lock)
        {
            var path = Normalize(compPath);
            Updating[path] = Updating.GetValueOrDefault(path) + 1;
        }
    }

    public static void End(string compPath)
    {
        if (string.IsNullOrEmpty(compPath)) return;
        lock (Lock)
        {
            var path = Normalize(compPath);
            var count = Updating.GetValueOrDefault(path);
            if (count <= 1)
                Updating.Remove(path);
            else
                Updating[path] = count - 1;
        }
    }

    public static bool IsUpdating(string compPath)
    {
        if (string.IsNullOrEmpty(compPath)) return false;
        lock (Lock)
        {
            return Updating.ContainsKey(Normalize(compPath));
        }
    }

    private static string Normalize(string path)
    {
        return Path.TrimEndingDirectorySeparator(path);
    }
}
