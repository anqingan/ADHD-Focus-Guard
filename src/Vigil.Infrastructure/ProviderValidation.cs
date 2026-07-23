namespace Vigil.Infrastructure;

internal static class ProviderValidation
{
    public static string NormalizeBaseUrl(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length is < 1 or > 2048
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Base URL 必须是无用户名、查询参数和片段的 HTTPS 地址。", nameof(value));
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public static string NormalizeModel(string value)
    {
        var model = value.Trim();
        if (model.Length is < 1 or > 200 || model.Any(char.IsControl))
        {
            throw new ArgumentException("Model 长度必须为 1–200 个字符且不能包含控制字符。", nameof(value));
        }
        return model;
    }
}
