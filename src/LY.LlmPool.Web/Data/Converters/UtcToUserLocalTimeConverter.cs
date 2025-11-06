using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Globalization;

namespace LY.LlmPool.Web.Data.Converters;

/// <summary>
/// UTC DateTime 到用户本地时间的转换器
/// 从 HttpContext 或 CurrentCulture 获取用户时区
/// 
/// 注意：这个 Converter 会在查询时自动将 UTC 时间转换为用户本地时间
/// </summary>
public class UtcToUserLocalTimeConverter : ValueConverter<DateTime, DateTime>
{
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public UtcToUserLocalTimeConverter(IHttpContextAccessor? httpContextAccessor = null)
        : base(
            // 写入数据库：保持 UTC（不转换）
            v => v,
            // 从数据库读取：UTC -> 用户本地时间
            v => ConvertToUserLocalTime(v, httpContextAccessor))
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private static DateTime ConvertToUserLocalTime(DateTime utcDateTime, IHttpContextAccessor? httpContextAccessor)
    {
        // 确保是 UTC
        if (utcDateTime.Kind != DateTimeKind.Utc)
        {
            utcDateTime = DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        }

        // 获取用户时区
        var userTimezone = GetUserTimezone(httpContextAccessor);

        // 转换为用户本地时间
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, userTimezone);

        // 返回 Unspecified 类型，避免再次转换
        return DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
    }

    private static TimeZoneInfo GetUserTimezone(IHttpContextAccessor? httpContextAccessor)
    {
        var httpContext = httpContextAccessor?.HttpContext;

        if (httpContext != null)
        {
            // 优先级 1: 从 Cookie 获取（由 JavaScript 设置）
            if (httpContext.Request.Cookies.TryGetValue("user_timezone", out var timezoneCookie))
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(timezoneCookie);
                }
                catch
                {
                    // 无效的时区 ID，继续
                }
            }

            // 优先级 2: 从 Accept-Language 推断
            var acceptLanguage = httpContext.Request.Headers.AcceptLanguage.ToString();
            if (!string.IsNullOrEmpty(acceptLanguage))
            {
                var timezone = TryGetTimezoneFromLanguage(acceptLanguage);
                if (timezone != null)
                    return timezone;
            }
        }

        // 优先级 3: 从 CurrentCulture 推断
        var culture = CultureInfo.CurrentCulture;
        var timezoneFromCulture = TryGetTimezoneFromCulture(culture);
        if (timezoneFromCulture != null)
            return timezoneFromCulture;

        // 降级到服务器本地时区
        return TimeZoneInfo.Local;
    }

    private static TimeZoneInfo? TryGetTimezoneFromLanguage(string acceptLanguage)
    {
        try
        {
            // Accept-Language: "zh-CN,zh;q=0.9,en;q=0.8"
            var languages = acceptLanguage.Split(',')
                .Select(l => l.Split(';')[0].Trim().ToLowerInvariant())
                .ToList();

            foreach (var lang in languages)
            {
                var timezone = lang switch
                {
                    "zh-cn" or "zh" => TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"),
                    "zh-tw" => TimeZoneInfo.FindSystemTimeZoneById("Taipei Standard Time"),
                    "ja" or "ja-jp" => TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time"),
                    "ko" or "ko-kr" => TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time"),
                    "en-us" => TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"),
                    "en-gb" => TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"),
                    _ => null
                };

                if (timezone != null)
                    return timezone;
            }
        }
        catch
        {
            // 忽略错误
        }

        return null;
    }

    private static TimeZoneInfo? TryGetTimezoneFromCulture(CultureInfo culture)
    {
        try
        {
            if (!culture.IsNeutralCulture)
            {
                var region = new RegionInfo(culture.Name);
                return region.TwoLetterISORegionName.ToUpperInvariant() switch
                {
                    "CN" => TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"),
                    "TW" => TimeZoneInfo.FindSystemTimeZoneById("Taipei Standard Time"),
                    "JP" => TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time"),
                    "KR" => TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time"),
                    "US" => TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"),
                    "GB" => TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"),
                    _ => null
                };
            }
        }
        catch
        {
            // 忽略错误
        }

        return null;
    }
}
