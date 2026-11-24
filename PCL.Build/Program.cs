namespace PCL.Build;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException("Expected prepare, allocate or render-splash.");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 1; index < args.Length; index += 2)
            {
                if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 == args.Length
                    || !options.TryAdd(args[index], args[index + 1]))
                    throw new ArgumentException("Expected unique --name value options.");
            }
            string Required(string key) => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new ArgumentException($"Missing {key}.");

            switch (args[0])
            {
                case "render-splash":
                    StartupSplashFrame.Write(Required("--output"));
                    return 0;
                case "allocate":
                    Console.WriteLine(BuildPackaging.AllocateReleaseBuild(
                        File.ReadAllText(Required("--history")),
                        options.GetValueOrDefault("--date") is { } date ? BuildStamp.ParseDate(date) : null));
                    return 0;
                case "prepare":
                    var prepared = BuildPackaging.Prepare(
                        Required("--metadata"), Required("--notes"), Required("--output"), Required("--state"),
                        options.GetValueOrDefault("--build"), options.GetValueOrDefault("--history"));
                    Console.WriteLine($"PCL build {prepared.FullVersion}; embedded release notes: {prepared.EmbeddedVersions.Count}");
                    return 0;
                default:
                    throw new ArgumentException($"Unknown command: {args[0]}");
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
