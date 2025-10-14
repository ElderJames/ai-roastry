namespace LY.LlmPool.Web.Components.ChatHelpers;

/// <summary>
/// 参数处理工具类
/// </summary>
public static class ParameterUtils
{
    /// <summary>
    /// 应用参数
    /// </summary>
    public class AppParameter
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// 从参数数组创建应用参数列表
    /// </summary>
    public static List<AppParameter> CreateParametersFromArray(string[]? parameters)
    {
        if (parameters == null || parameters.Length == 0)
        {
            return new List<AppParameter>();
        }

        return parameters.Select(p => new AppParameter { Key = p, Value = string.Empty }).ToList();
    }

    /// <summary>
    /// 获取有效的参数字典
    /// </summary>
    public static Dictionary<string, object>? GetValidParameters(List<AppParameter>? parameters)
    {
        if (parameters == null || !parameters.Any(p => !string.IsNullOrEmpty(p.Value)))
        {
            return null;
        }

        return parameters
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .ToDictionary(p => p.Key, p => (object)p.Value);
    }

    /// <summary>
    /// 验证参数
    /// </summary>
    public static ParameterValidationResult ValidateParameter(AppParameter parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter.Key))
        {
            return ParameterValidationResult.Error("参数键不能为空");
        }

        if (parameter.Key.Contains(" "))
        {
            return ParameterValidationResult.Error("参数键不能包含空格");
        }

        return ParameterValidationResult.Success();
    }

    /// <summary>
    /// 解析参数字符串为字典 (支持 ModelParameters 格式)
    /// </summary>
    /// <param name="parametersString">参数字符串，格式: "temp=0.7,tokens=100" 或 "temperature:0.9;max_tokens:2000"</param>
    /// <returns>解析后的参数字典</returns>
    public static Dictionary<string, object> ParseParametersToDict(string parametersString)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(parametersString))
        {
            return result;
        }

        // 支持逗号或分号分隔
        var pairs = parametersString.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var pair in pairs)
        {
            var parts = pair.Split(new[] { '=', ':' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                var key = parts[0].Trim().ToLowerInvariant();
                var value = parts[1].Trim();
                
                // 尝试转换为适当的类型
                if (key == "temp" || key == "temperature" || key == "top_p" || key == "topp")
                {
                    if (double.TryParse(value, out var doubleValue))
                    {
                        result[key] = doubleValue;
                    }
                }
                else if (key == "tokens" || key == "max_tokens" || key == "maxtokens")
                {
                    if (int.TryParse(value, out var intValue))
                    {
                        result[key] = intValue;
                    }
                }
                else
                {
                    // 未知参数保持为字符串
                    result[key] = value;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 将参数字典应用到 ChatOptions
    /// </summary>
    /// <param name="chatOptions">ChatOptions 对象</param>
    /// <param name="parameters">参数字典</param>
    public static void ApplyParametersToChatOptions(
        Microsoft.Extensions.AI.ChatOptions chatOptions, 
        Dictionary<string, object>? parameters)
    {
        if (parameters == null || parameters.Count == 0)
        {
            return;
        }

        if (parameters.TryGetValue("temperature", out var temp) || parameters.TryGetValue("temp", out temp))
        {
            chatOptions.Temperature = Convert.ToSingle(temp);
        }
        
        if (parameters.TryGetValue("max_tokens", out var tokens) || parameters.TryGetValue("tokens", out tokens))
        {
            chatOptions.MaxOutputTokens = Convert.ToInt32(tokens);
        }
        
        if (parameters.TryGetValue("top_p", out var topP) || parameters.TryGetValue("topp", out topP))
        {
            chatOptions.TopP = Convert.ToSingle(topP);
        }
    }

    /// <summary>
    /// 参数验证结果
    /// </summary>
    public class ParameterValidationResult
    {
        public bool IsValid { get; set; }
        public string? ErrorMessage { get; set; }

        public static ParameterValidationResult Success() => new() { IsValid = true };
        public static ParameterValidationResult Error(string message) => new() { IsValid = false, ErrorMessage = message };
    }
}
