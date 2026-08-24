using System.Text.RegularExpressions;

namespace Vigil.Core;

public static partial class ActivityClassifier
{
    public static (ActivityCategory Category, double Confidence) ApplyRules(
        ActivityWatchSnapshot activity,
        IReadOnlyList<ClassificationRule> rules)
    {
        var app = Normalize(activity.Application);
        var domain = Normalize(activity.Domain);
        var title = Normalize(string.IsNullOrWhiteSpace(activity.BrowserTitle) ? activity.WindowTitle : activity.BrowserTitle);

        var match = rules.Where(r => r.IsEnabled && Matches(r, app, domain, title))
            .OrderBy(r => r.CreatedAt == DateTimeOffset.MinValue ? 1 : 0)
            .ThenBy(r => r.Scope)
            .ThenByDescending(r => r.CreatedAt)
            .FirstOrDefault();
        if (match is not null)
        {
            if (IsNeutralCommunicationActivity(app, domain) && match.Category == ActivityCategory.Entertainment)
                return (ActivityCategory.Other, 1);
            return (match.Category, 1);
        }

        if (IsNeutralCommunicationActivity(app, domain)) return (ActivityCategory.Other, .92);

        if (ContainsAny(app, "devenv", "code", "rider", "idea", "pycharm", "word", "excel", "powerpnt", "onenote", "matlab", "texstudio"))
            return (ActivityCategory.WorkAndStudy, .86);
        if (ContainsAny(app, "steam", "epicgames", "wegame") || ContainsAny(domain, "douyin.com", "tiktok.com"))
            return (ActivityCategory.Entertainment, .9);
        if (ContainsAny(domain, "github.com", "stackoverflow.com", "learn.microsoft.com", "docs.", "arxiv.org"))
            return (ActivityCategory.WorkAndStudy, .78);
        return (ActivityCategory.Other, .35);
    }

    public static string BuildDisplayName(ActivityWatchSnapshot activity)
    {
        var title = string.IsNullOrWhiteSpace(activity.BrowserTitle) ? activity.WindowTitle : activity.BrowserTitle;
        title = DynamicSuffixRegex().Replace(title.Trim(), "").Trim();
        if (title.Length > 160) title = title[..160];
        if (!string.IsNullOrWhiteSpace(title)) return title;
        return string.IsNullOrWhiteSpace(activity.Application) ? "其它活动" : activity.Application;
    }

    public static bool IsNeutralCommunicationActivity(string application, string domain = "")
    {
        var executable = Path.GetFileNameWithoutExtension(application.Trim()).ToLowerInvariant();
        if (executable is "wechat" or "weixin" or "qq" or "qqnt" or "tim" or "wxwork" or "wecom") return true;
        var host = Normalize(domain).Trim('.');
        return host is "wx.qq.com" or "im.qq.com" or "web.wechat.com" or "web.weixin.qq.com"
            || host.EndsWith(".wx.qq.com", StringComparison.Ordinal)
            || host.EndsWith(".web.wechat.com", StringComparison.Ordinal);
    }

    private static bool Matches(ClassificationRule rule, string app, string domain, string title)
    {
        if (!string.IsNullOrWhiteSpace(rule.Application) && !app.Equals(Normalize(rule.Application), StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(rule.Domain) && !domain.Equals(Normalize(rule.Domain), StringComparison.Ordinal)) return false;
        if (rule.Scope == RuleScope.ApplicationOrDomain) return app.Length > 0 || domain.Length > 0;
        var keywords = Normalize(rule.TitleKeywords).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keywords.Length == 0) return rule.Scope == RuleScope.Exact;
        var matched = keywords.Count(title.Contains);
        return rule.Scope == RuleScope.Exact ? matched == keywords.Length : matched >= Math.Max(1, (int)Math.Ceiling(keywords.Length * .6));
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
    private static bool ContainsAny(string value, params string[] candidates) => candidates.Any(value.Contains);

    [GeneratedRegex(@"\s*[-–—|]\s*(Google Chrome|Microsoft Edge|Mozilla Firefox|Visual Studio Code|Visual Studio)$", RegexOptions.IgnoreCase)]
    private static partial Regex DynamicSuffixRegex();
}
