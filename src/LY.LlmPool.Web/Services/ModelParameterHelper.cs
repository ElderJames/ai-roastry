using System.Text.Json;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace LY.LlmPool.Web.Services;

/// <summary>
/// 模型参数辅助类,统一处理参数的解析和转换
/// </summary>
public static class ModelParameterHelper
{
    /// <summary>
    /// 从 JSON 字符串解析参数字典
    /// </summary>
    /// <param name="jsonString">JSON 格式的参数字符串,如 {"max_tokens": 2000, "thinking_enabled": true}</param>
    /// <returns>参数字典,如果解析失败或为空则返回 null</returns>
    public static Dictionary<string, object>? ParseFromJson(string? jsonString)
    {
        if (string.IsNullOrWhiteSpace(jsonString))
            return null;

        try
        {
            var jsonDoc = JsonDocument.Parse(jsonString);
            var result = new Dictionary<string, object>();

            foreach (var property in jsonDoc.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.Number => property.Value.TryGetInt32(out var intVal) ? (object)intVal : property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    _ => property.Value.ToString()
                };
                result[property.Name] = value;
            }

            return result.Count > 0 ? result : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从字符串键值对格式解析参数字典
    /// </summary>
    /// <param name="parametersString">键值对格式的参数字符串,如 "temperature=0.7,max_tokens=100"</param>
    /// <returns>参数字典,如果解析失败或为空则返回 null</returns>
    public static Dictionary<string, object>? ParseFromKeyValueString(string? parametersString)
    {
        if (string.IsNullOrWhiteSpace(parametersString))
            return null;

        var result = new Dictionary<string, object>();
        var pairs = parametersString.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var pair in pairs)
        {
            var parts = pair.Split(new[] { '=', ':' }, 2);
            if (parts.Length != 2) continue;

            var key = parts[0].Trim().ToLowerInvariant();
            var value = parts[1].Trim();

            // 根据常见的参数名转换为合适的类型
            if (key == "temperature" || key == "temp" || key == "top_p" || key == "topp")
            {
                if (double.TryParse(value, out var d))
                    result[key] = d;
            }
            else if (key == "max_tokens" || key == "tokens" || key == "maxtokens")
            {
                if (int.TryParse(value, out var i))
                    result[key] = i;
            }
            else if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                result[key] = true;
            }
            else if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                result[key] = false;
            }
            else if (int.TryParse(value, out var intVal))
            {
                result[key] = intVal;
            }
            else if (double.TryParse(value, out var doubleVal))
            {
                result[key] = doubleVal;
            }
            else
            {
                result[key] = value;
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// 合并多个参数字典,后面的字典会覆盖前面的同名参数
    /// </summary>
    /// <param name="parameterDicts">要合并的参数字典列表</param>
    /// <returns>合并后的参数字典,如果所有输入都为空则返回 null</returns>
    public static Dictionary<string, object>? Merge(params Dictionary<string, object>?[] parameterDicts)
    {
        Dictionary<string, object>? result = null;

        foreach (var dict in parameterDicts)
        {
            if (dict == null || dict.Count == 0) continue;

            if (result == null)
            {
                result = new Dictionary<string, object>(dict);
            }
            else
            {
                foreach (var kvp in dict)
                {
                    result[kvp.Key] = kvp.Value;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 验证 JSON 字符串格式是否正确
    /// </summary>
    /// <param name="jsonString">要验证的 JSON 字符串</param>
    /// <param name="errorMessage">如果验证失败,返回错误信息</param>
    /// <returns>验证是否成功</returns>
    public static bool ValidateJson(string? jsonString, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(jsonString))
        {
            errorMessage = null;
            return true; // 空字符串视为有效
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonString);
            
            // 检查是否为对象类型
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errorMessage = "Must be a JSON object";
                return false;
            }
            
            errorMessage = null;
            return true;
        }
        catch (JsonException ex)
        {
            errorMessage = $"Invalid JSON: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 将参数字典应用到 OpenAIPromptExecutionSettings
    /// </summary>
    /// <param name="settings">要应用参数的 ExecutionSettings 对象</param>
    /// <param name="parameters">参数字典</param>
    public static void ApplyToExecutionSettings(OpenAIPromptExecutionSettings settings, Dictionary<string, object>? parameters)
    {
        if (parameters == null || parameters.Count == 0) return;

        // Temperature
        if (parameters.TryGetValue("temperature", out var temp) || parameters.TryGetValue("temp", out temp))
        {
            if (temp is double tempDouble)
                settings.Temperature = tempDouble;
            else if (double.TryParse(temp?.ToString(), out var tempValue))
                settings.Temperature = tempValue;
        }

        // Max Tokens
        if (parameters.TryGetValue("max_tokens", out var tokens) || 
            parameters.TryGetValue("tokens", out tokens) || 
            parameters.TryGetValue("maxtokens", out tokens))
        {
            if (tokens is int tokensInt)
                settings.MaxTokens = tokensInt;
            else if (int.TryParse(tokens?.ToString(), out var tokensValue))
                settings.MaxTokens = tokensValue;
        }

        // Top P
        if (parameters.TryGetValue("top_p", out var topP) || parameters.TryGetValue("topp", out topP))
        {
            if (topP is double topPDouble)
                settings.TopP = topPDouble;
            else if (double.TryParse(topP?.ToString(), out var topPValue))
                settings.TopP = topPValue;
        }
    }
}
