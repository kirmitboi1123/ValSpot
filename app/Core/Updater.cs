using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ValSpot.Core;

/// <summary>Asks GitHub for the latest ValSpot release. One small request; nothing else is sent.</summary>
public static class Updater
{
    public const string Repo = "kirmitboi1123/ValSpot";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";

    public sealed record Result(bool Newer, string Latest, string Url);

    public static async Task<Result?> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ValSpot/{Program.Version}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            string url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
            return new Result(IsNewer(tag, Program.Version), tag.TrimStart('v', 'V'), url);
        }
        catch (Exception e)
        {
            Log.Write($"update check failed ({e.GetType().Name}); will try again later");
            return null;
        }
    }

    public static bool IsNewer(string tag, string current)
    {
        static Version Parse(string s)
        {
            s = s.Trim().TrimStart('v', 'V');
            int dash = s.IndexOfAny(new[] { '-', '+' });
            if (dash >= 0) s = s[..dash];
            return Version.TryParse(s.Contains('.') ? s : s + ".0", out var v) ? v : new Version(0, 0);
        }
        Version t = Parse(tag), c = Parse(current);
        if (t != c) return t > c;
        // same number: a release is newer than its beta (3.0.4 > 3.0.4-beta.1)
        static bool Pre(string s) => s.Split('+')[0].Contains('-');
        return Pre(current) && !Pre(tag);
    }
}
