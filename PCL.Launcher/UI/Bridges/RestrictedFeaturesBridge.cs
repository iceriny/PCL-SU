namespace PCL.UI.Bridges;

/// <summary>沿用 SU 的游戏安装策略。</summary>
public sealed class RestrictedFeaturesBridge : IRestrictedFeatures
{
    public bool IsRestricted => false;

    public bool Check(string feature) => true;

    public void ThrowIfRestricted(string feature) { }
}
