using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VT2ModUpdater.Services;

public static class SteamPaths
{
    private const string Vt2AppId = "552500";

    public static string? FindSteamRoot()
    {
        var candidates = new[]
        {
            (@"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (@"SOFTWARE\Valve\Steam", "InstallPath"),
        };
        foreach (var (key, value) in candidates)
        {
            using var rk = Registry.LocalMachine.OpenSubKey(key);
            var path = rk?.GetValue(value) as string;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) return path;
        }
        var fallback = @"C:\Program Files (x86)\Steam";
        return Directory.Exists(fallback) ? fallback : null;
    }

    public static string? FindWorkshopContentRoot()
    {
        var steam = FindSteamRoot();
        if (steam is null) return null;

        var vdfPath = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
        {
            var direct = Path.Combine(steam, "steamapps", "workshop", "content", Vt2AppId);
            return Directory.Exists(direct) ? direct : null;
        }

        var libraryRoot = FindLibraryOwningApp(File.ReadAllText(vdfPath), Vt2AppId)
                          ?? steam;
        var workshop = Path.Combine(libraryRoot, "steamapps", "workshop", "content", Vt2AppId);
        return Directory.Exists(workshop) ? workshop : null;
    }

    public static string? FindLibraryOwningApp(string vdfText, string appId)
    {
        var blockPattern = new Regex(
            @"""\d+""\s*\{(?<body>(?:[^{}]|\{[^{}]*\})*)\}",
            RegexOptions.Singleline);
        var pathPattern = new Regex(@"""path""\s*""(?<p>[^""]+)""", RegexOptions.Singleline);
        var appsBlockPattern = new Regex(@"""apps""\s*\{(?<apps>[^}]*)\}", RegexOptions.Singleline);
        var appKeyPattern = new Regex(@"""(?<id>\d+)""", RegexOptions.Singleline);

        foreach (Match block in blockPattern.Matches(vdfText))
        {
            var body = block.Groups["body"].Value;
            var apps = appsBlockPattern.Match(body);
            if (!apps.Success) continue;
            var ids = appKeyPattern.Matches(apps.Groups["apps"].Value);
            var hasApp = ids.Cast<Match>().Any(m => m.Groups["id"].Value == appId);
            if (!hasApp) continue;
            var pathMatch = pathPattern.Match(body);
            if (!pathMatch.Success) continue;
            return pathMatch.Groups["p"].Value.Replace(@"\\", @"\");
        }
        return null;
    }
}
