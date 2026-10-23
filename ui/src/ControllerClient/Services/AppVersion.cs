using System.Reflection;

namespace ControllerClient.Services;

/// <summary>
/// 版本号与 build 号，编译时由 ControllerClient.csproj 写进程序集。
/// 正式包（CI 用 CAN_VERSION 打的）报它被打成的标签；其余都是测试包，报下一个补丁号加
/// (Dev Build N)，在排序上永远新于它基于的正式版，用户拿版本号对话时不会把测试包认成旧版。
/// 判断看程序集里的 CanRelease 标记而不是某个要人记得翻的常量。
/// </summary>
public static class AppVersion
{
    private static readonly Assembly Self = typeof(AppVersion).Assembly;

    /// <summary>最新正式版号，即 csproj 的 CanVersion 或 CI 传入的 CAN_VERSION。</summary>
    public static string ReleaseVersion { get; } =
        Self.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static bool IsRelease { get; } = Metadata("CanRelease") == "true";
    public static string DevBuild { get; } = Metadata("CanDevBuild") ?? "1";
    /// <summary>提交数.短哈希，工作区有改动时带 +dirty；拿不到时为 dev。</summary>
    public static string Build { get; } = Metadata("CanBuild") ?? "dev";

    /// <summary>对外报告的版本号（不带 v）。</summary>
    public static string Version => IsRelease ? ReleaseVersion : BumpPatch(ReleaseVersion);

    /// <summary>标题栏用：正式版 "1.0.3"，测试版 "1.0.4 (Dev Build 1)"。</summary>
    public static string Display => Format(ReleaseVersion, IsRelease, DevBuild, null);

    /// <summary>日志和排查用：两种号都带上。</summary>
    public static string Full => Format(ReleaseVersion, IsRelease, DevBuild, Build);

    internal static string Format(string releaseVersion, bool release, string devBuild, string? build)
    {
        if (release) return build is null ? releaseVersion : $"v{releaseVersion} (build {build})";
        var next = BumpPatch(releaseVersion);
        return build is null ? $"{next} (Dev Build {devBuild})" : $"v{next} (Dev Build {devBuild}; build {build})";
    }

    /// <summary>1.0.1 → 1.0.2：从后往前第一段纯数字加一。</summary>
    internal static string BumpPatch(string version)
    {
        var pieces = version.Split('.');
        for (var i = pieces.Length - 1; i >= 0; i--)
        {
            if (pieces[i].Length == 0 || !pieces[i].All(char.IsAsciiDigit)) continue;
            pieces[i] = (long.Parse(pieces[i]) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            break;
        }
        return string.Join('.', pieces);
    }

    private static string? Metadata(string key) =>
        Self.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } value
            ? value : null;
}
