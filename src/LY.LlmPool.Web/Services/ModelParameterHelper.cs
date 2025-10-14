using System.Text.Json;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace LY.LlmPool.Web.Services;

/// <summary>
/// 模型参数辅助类 - 用于处理 JSON 格式的模型参数
/// </summary>
public static class ModelParameterHelper
{
    /// <summary>
    /// 从 JSON 字符串解析参数
    /// </summary>
    public static Dictionary<string, object>? ParseFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (result == null)
            {
                return null;
            }

            var parameters = new Dictionary<string, object>();
            foreach (var kvp in result)
            {
                parameters[kvp.Key] = ConvertJsonElement(kvp.Value);
            }

            return parameters;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从键值对字符串解析参数 (如: "temperature=0.7,max_tokens=100")
    /// </summary>
    public static Dictionary<string, object>? ParseFromKeyValueString(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var pairs = input.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var pair in pairs)
        {
            var parts = pair.Split(new[] { '=', ':' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                var key = parts[0].Trim();
                var value = parts[1].Trim();

                // 尝试解析为合适的类型
                if (bool.TryParse(value, out var boolValue))
                {
                    result[key] = boolValue;
                }
                else if (int.TryParse(value, out var intValue))
                {
                    result[key] = intValue;
                }
                else if (double.TryParse(value, out var doubleValue))
                {
                    result[key] = doubleValue;
                }
                else
                {
                    result[key] = value;
                }
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// 合并多个参数字典 (后面的优先级更高)
    /// </summary>
    public static Dictionary<string, object>? Merge(params Dictionary<string, object>?[] dictionaries)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var dict in dictionaries)
        {
            if (dict != null)
            {
                foreach (var kvp in dict)
                {
                    result[kvp.Key] = kvp.Value;
                }
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// 验证 JSON 格式
    /// </summary>
    public static bool ValidateJson(string? json, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return true; // 空字符串被认为是有效的
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errorMessage = "Must be a JSON object";
                return false;
            }
            return true;
        }
        catch (JsonException ex)
        {
            errorMessage = "Invalid JSON: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 应用参数到 OpenAI 执行设置
    /// </summary>
    public static void ApplyToExecutionSettings(OpenAIPromptExecutionSettings settings, Dictionary<string, object>? parameters)
    {
        if (parameters == null || parameters.Count == 0)
        {
            return;
        }

        // max_tokens (支持替代名称: tokens)
        if (TryGetValue(parameters, out var maxTokens, "max_tokens", "tokens"))
        {
            settings.MaxTokens = Convert.ToInt32(maxTokens);
        }

        // temperature (支持替代名称: temp)
        if (TryGetValue(parameters, out var temperature, "temperature", "temp"))
        {
            settings.Temperature = Convert.ToDouble(temperature);
        }

        // top_p (支持替代名称: topp)
        if (TryGetValue(parameters, out var topP, "top_p", "topp"))
        {
            settings.TopP = Convert.ToDouble(topP);
        }

        // frequency_penalty (支持替代名称: freq_penalty, frequency)
        if (TryGetValue(parameters, out var frequencyPenalty, "frequency_penalty", "freq_penalty", "frequency"))
        {
            settings.FrequencyPenalty = Convert.ToDouble(frequencyPenalty);
        }

        // presence_penalty (支持替代名称: pres_penalty, presence)
        if (TryGetValue(parameters, out var presencePenalty, "presence_penalty", "pres_penalty", "presence"))
        {
            settings.PresencePenalty = Convert.ToDouble(presencePenalty);
        }
    }

    /// <summary>
    /// 尝试从字典中获取值(支持多个可能的键名,不区分大小写)
    /// </summary>
    private static bool TryGetValue(Dictionary<string, object> parameters, out object? value, params string[] keys)
    {
        value = null;
        foreach (var key in keys)
        {
            foreach (var kvp in parameters)
            {
                if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kvp.Value;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 转换 JsonElement 为合适的对象类型
    /// </summary>
    private static object ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => ConvertNumber(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null!,
            _ => element.ToString()
        };
    }

    /// <summary>
    /// 转换数字类型的 JsonElement
    /// </summary>
    private static object ConvertNumber(JsonElement element)
    {
        // 优先尝试转换为整数
        if (element.TryGetInt64(out var int64Value))
        {
            // 如果值在 int32 范围内,返回 int,否则返回 long
            if (int64Value >= int.MinValue && int64Value <= int.MaxValue)
            {
                return (int)int64Value;
            }
            return int64Value;
        }

        // 如果不是整数,返回 double
        return element.GetDouble();
    }
}
