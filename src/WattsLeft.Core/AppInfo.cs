using System.Reflection;

namespace WattsLeft;

/// <summary>About the running app.</summary>
internal static class AppInfo
{
    /// <summary>The version, e.g. 0.6.2, as the release tag set it (without the build's commit suffix).</summary>
    public static string Version { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0").Split('+')[0];
}
