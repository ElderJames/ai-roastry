using Microsoft.AspNetCore.Components;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LY.LlmPool.Web.Common;

/// <summary>
/// JSON 序列化辅助类 - 提供全局统一的序列化选项
/// <para>用法1: JsonHelper.Serialize(obj) - 使用辅助方法</para>
/// <para>用法2: JsonSerializer.Serialize(obj, JsonHelper.Options) - 使用全局配置</para>
/// </summary>
public static class JsonHelper
{
    /// <summary>
    /// 全局 JSON 序列化选项: 支持中文、换行格式化
    /// <para>可用于: JsonSerializer.Serialize(obj, JsonHelper.Options)</para>
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, // 换行格式化
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // 支持中文不转义
    };

    /// <summary>
    /// 序列化对象为 JSON 字符串(使用全局选项)
    /// </summary>
    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, Options);
    }

    /// <summary>
    /// 反序列化 JSON 字符串为对象(使用全局选项)
    /// </summary>
    public static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, Options);
    }

    // ==================== JSON 格式化与高亮 ====================

    /// <summary>
    /// 格式化 JSON 字符串并添加语法高亮
    /// Formats JSON string with syntax highlighting for better readability
    /// </summary>
    /// <param name="jsonString">原始 JSON 字符串</param>
    /// <returns>格式化后的 HTML 标记</returns>
    /// <remarks>
    /// 使用 UnsafeRelaxedJsonEscaping 以保留中文、日文等 Unicode 字符的原样显示
    /// Uses UnsafeRelaxedJsonEscaping to preserve Chinese, Japanese and other Unicode characters
    /// </remarks>
    public static MarkupString FormatJsonWithHighlight(string? jsonString)
    {
        if (string.IsNullOrWhiteSpace(jsonString))
        {
            return new MarkupString("<span style='color: #999;'>无内容</span>");
        }

        try
        {
            // 尝试解析并格式化 JSON
            // Parse and format JSON while preserving Unicode characters (e.g., Chinese)
            var jsonElement = JsonDocument.Parse(jsonString);
            var formattedJson = JsonSerializer.Serialize(jsonElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                // 保留中文等 Unicode 字符，不转义为 \uXXXX 格式
                // Preserve Unicode characters like Chinese instead of escaping to \uXXXX
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            // 应用语法高亮
            var highlightedJson = ApplySyntaxHighlighting(formattedJson);
            return new MarkupString(highlightedJson);
        }
        catch (JsonException)
        {
            // 如果不是有效的 JSON，返回原始文本
            return new MarkupString($"<pre style='margin: 0; white-space: pre-wrap; word-break: break-word;'>{System.Net.WebUtility.HtmlEncode(jsonString)}</pre>");
        }
    }

    /// <summary>
    /// 应用 JSON 语法高亮
    /// Applies syntax highlighting to JSON string using CSS classes
    /// </summary>
    public static string ApplySyntaxHighlighting(string json)
    {
        var sb = new StringBuilder();
        sb.Append("<pre class='json-highlight' style='margin: 0; font-family: Consolas, Monaco, monospace; font-size: 13px; line-height: 1.5;'>");

        var inString = false;
        var escaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            if (escaped)
            {
                sb.Append(System.Net.WebUtility.HtmlEncode(c.ToString()));
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                sb.Append(System.Net.WebUtility.HtmlEncode(c.ToString()));
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                if (!inString)
                {
                    // 检查是否为键名（后面跟着冒号）
                    var isKey = false;
                    for (int j = i + 1; j < json.Length; j++)
                    {
                        if (json[j] == ':')
                        {
                            isKey = true;
                            break;
                        }
                        if (json[j] != ' ' && json[j] != '"') break;
                    }

                    sb.Append(isKey
                        ? "<span style='color: #0451a5; font-weight: 600;'>\""
                        : "<span style='color: #098658;'>\"");
                    inString = true;
                }
                else
                {
                    sb.Append("\"</span>");
                    inString = false;
                }
                continue;
            }

            if (!inString)
            {
                // 数字高亮
                if (char.IsDigit(c) || (c == '-' && i + 1 < json.Length && char.IsDigit(json[i + 1])))
                {
                    var numStart = i;
                    while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '.' || json[i] == '-' || json[i] == 'e' || json[i] == 'E' || json[i] == '+'))
                    {
                        i++;
                    }
                    var number = json.Substring(numStart, i - numStart);
                    sb.Append($"<span style='color: #098658;'>{number}</span>");
                    i--;
                    continue;
                }

                // 布尔值和 null 高亮
                if (c == 't' && json.Substring(i, Math.Min(4, json.Length - i)) == "true")
                {
                    sb.Append("<span style='color: #0000ff; font-weight: 600;'>true</span>");
                    i += 3;
                    continue;
                }
                if (c == 'f' && json.Substring(i, Math.Min(5, json.Length - i)) == "false")
                {
                    sb.Append("<span style='color: #0000ff; font-weight: 600;'>false</span>");
                    i += 4;
                    continue;
                }
                if (c == 'n' && json.Substring(i, Math.Min(4, json.Length - i)) == "null")
                {
                    sb.Append("<span style='color: #0000ff; font-weight: 600;'>null</span>");
                    i += 3;
                    continue;
                }

                // 特殊字符 (括号、逗号等)
                if (c == '{' || c == '}' || c == '[' || c == ']')
                {
                    sb.Append($"<span style='color: #000000; font-weight: bold;'>{c}</span>");
                    continue;
                }
                if (c == ':')
                {
                    sb.Append($"<span style='color: #000000;'>{c}</span>");
                    continue;
                }
            }

            sb.Append(System.Net.WebUtility.HtmlEncode(c.ToString()));
        }

        sb.Append("</pre>");
        return sb.ToString();
    }
}
