using System.Text.RegularExpressions;

namespace LY.LlmPool.Web.Services;

/// <summary>
/// Prompt 环境变量服务 - 处理 @variable 格式的环境变量替换
/// </summary>
public class PromptEnvironmentService
{
    // 匹配 @variable 格式的环境变量 (支持格式化参数,如 @datetime:yyyy-MM-dd)
    private static readonly Regex EnvironmentVariablePattern = new(@"@([a-zA-Z_][a-zA-Z0-9_]*)(?::([^@\s]+))?", RegexOptions.Compiled);

    /// <summary>
    /// 替换 Prompt 中的环境变量
    /// </summary>
    /// <param name="promptContent">Prompt 内容</param>
    /// <param name="userName">可选的用户名</param>
    /// <returns>替换后的 Prompt 内容</returns>
    public string ReplaceEnvironmentVariables(string promptContent, string? userName = null)
    {
        if (string.IsNullOrEmpty(promptContent))
        {
            return promptContent;
        }

        var now = DateTime.Now;
        var utcNow = DateTime.UtcNow;

        return EnvironmentVariablePattern.Replace(promptContent, match =>
        {
            var variableName = match.Groups[1].Value.ToLowerInvariant();
            var format = match.Groups.Count > 2 && match.Groups[2].Success 
                ? match.Groups[2].Value 
                : null;

            return variableName switch
            {
                // 日期时间相关
                "datetime" => format != null ? FormatDateTime(now, format) : GetFullDateTime(now),
                "date" => FormatDateTime(now, format ?? "yyyy-MM-dd"),
                "time" => FormatDateTime(now, format ?? "HH:mm:ss"),
                "year" => now.Year.ToString(),
                "month" => FormatDateTime(now, format ?? "MM"),
                "day" => FormatDateTime(now, format ?? "dd"),
                "weekday" => GetWeekdayName(now, format),
                "timezone" => GetTimezoneInfo(format),
                
                // Unix 时间戳
                "timestamp" => GetUnixTimestamp(format),
                "timestamp_ms" => GetUnixTimestampMs(),
                
                // UTC 时间
                "utc" => FormatDateTime(utcNow, format ?? "yyyy-MM-dd HH:mm:ss"),
                "utc_date" => FormatDateTime(utcNow, format ?? "yyyy-MM-dd"),
                "utc_time" => FormatDateTime(utcNow, format ?? "HH:mm:ss"),
                
                // 系统信息
                "user" => userName ?? Environment.UserName,
                "machine" => Environment.MachineName,
                "os" => Environment.OSVersion.ToString(),
                
                // 随机值
                "guid" => Guid.NewGuid().ToString(format ?? "D"),
                "random" => GetRandomValue(format),
                
                // 未知变量保持原样
                _ => match.Value
            };
        });
    }

    /// <summary>
    /// 获取所有支持的环境变量列表及说明
    /// </summary>
    public Dictionary<string, string> GetSupportedVariables()
    {
        return new Dictionary<string, string>
        {
            // 日期时间
            ["@datetime"] = "完整日期时间信息 (包含日期、时间、星期、时区,如: 2024-10-13 15:30:45 星期日 UTC+08:00)",
            ["@datetime:format"] = "自定义格式的日期时间 (如: @datetime:yyyy/MM/dd)",
            ["@date"] = "当前日期 (默认: yyyy-MM-dd)",
            ["@time"] = "当前时间 (默认: HH:mm:ss)",
            ["@year"] = "当前年份",
            ["@month"] = "当前月份 (默认: MM, 可用 :M 去掉前导零)",
            ["@day"] = "当前日期(天) (默认: dd)",
            ["@weekday"] = "星期几 (默认: 中文全称, :en 英文, :short 缩写)",
            ["@timezone"] = "时区信息 (默认: +08:00, :name 显示名称)",
            
            // Unix 时间戳
            ["@timestamp"] = "Unix 时间戳(秒) (默认: 当前时间, :utc 使用UTC)",
            ["@timestamp_ms"] = "Unix 时间戳(毫秒)",
            
            // UTC 时间
            ["@utc"] = "UTC 日期时间",
            ["@utc_date"] = "UTC 日期",
            ["@utc_time"] = "UTC 时间",
            
            // 系统信息
            ["@user"] = "当前用户名",
            ["@machine"] = "计算机名称",
            ["@os"] = "操作系统版本",
            
            // 随机值
            ["@guid"] = "生成新的 GUID (默认: D 格式, :N 无连字符)",
            ["@random"] = "随机数 (默认: 0-100, :1000 指定范围)"
        };
    }

    private string GetFullDateTime(DateTime dateTime)
    {
        // 生成完整的日期时间信息: 2024-10-13 15:30:45 星期日 UTC+08:00
        var weekdayZh = dateTime.ToString("dddd", new System.Globalization.CultureInfo("zh-CN"));
        var tz = TimeZoneInfo.Local;
        var offset = tz.BaseUtcOffset.ToString(@"\+hh\:mm");
        return $"{dateTime:yyyy-MM-dd HH:mm:ss} {weekdayZh} UTC{offset}";
    }

    private string FormatDateTime(DateTime dateTime, string format)
    {
        try
        {
            return dateTime.ToString(format);
        }
        catch
        {
            return dateTime.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    private string GetWeekdayName(DateTime dateTime, string? format)
    {
        return format?.ToLowerInvariant() switch
        {
            "en" => dateTime.ToString("dddd", new System.Globalization.CultureInfo("en-US")), // English full name
            "short" => dateTime.ToString("ddd", new System.Globalization.CultureInfo("en-US")), // Short name (English)
            "number" => ((int)dateTime.DayOfWeek).ToString(), // 0-6
            _ => dateTime.ToString("dddd", new System.Globalization.CultureInfo("zh-CN")) // 中文全称
        };
    }

    private string GetTimezoneInfo(string? format)
    {
        var tz = TimeZoneInfo.Local;
        return format?.ToLowerInvariant() switch
        {
            "name" => tz.DisplayName,
            "id" => tz.Id,
            "offset" => tz.BaseUtcOffset.ToString(@"hh\:mm"),
            _ => tz.BaseUtcOffset.ToString(@"\+hh\:mm") // +08:00 格式
        };
    }

    private string GetUnixTimestamp(string? format)
    {
        var useUtc = format?.ToLowerInvariant() == "utc";
        var dateTime = useUtc ? DateTime.UtcNow : DateTime.Now;
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var offset = useUtc ? dateTime : dateTime.ToUniversalTime();
        return ((long)(offset - epoch).TotalSeconds).ToString();
    }

    private string GetUnixTimestampMs()
    {
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return ((long)(DateTime.UtcNow - epoch).TotalMilliseconds).ToString();
    }

    private string GetRandomValue(string? format)
    {
        var random = new Random();
        if (string.IsNullOrEmpty(format))
        {
            return random.Next(0, 101).ToString(); // 默认 0-100
        }

        if (int.TryParse(format, out var max))
        {
            return random.Next(0, max + 1).ToString();
        }

        // 支持范围格式: "10-50"
        var parts = format.Split('-');
        if (parts.Length == 2 && int.TryParse(parts[0], out var min) && int.TryParse(parts[1], out var maxRange))
        {
            return random.Next(min, maxRange + 1).ToString();
        }

        return random.Next(0, 101).ToString();
    }
}
