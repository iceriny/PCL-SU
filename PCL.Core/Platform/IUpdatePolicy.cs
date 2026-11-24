namespace PCL.Core.Platform;

public interface IUpdatePolicy
{
    bool SupportsInAppUpdate { get; }
    string InstallButtonKey { get; }
}
