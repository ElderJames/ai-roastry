using System.Text.Json;
using System.Text.RegularExpressions;

namespace LY.LlmPool.Web.Services;

/// <summary>
/// 参数信息模型
/// </summary>
public class ParameterInfo
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class PromptParameterService
{
    // 支持两种格式: {{paramName}} 和 {{paramName|描述}}
    // 参数名支持字母、数字、下划线和点号(用于嵌套参数如user.name)
    private static readonly Regex ParameterPattern = new(@"\{\{([a-zA-Z_][a-zA-Z0-9_.]*?)(?:\|([^}]*))?\}\}", RegexOptions.Compiled);

    // 旧格式的正则,用于向后兼容
    private static readonly Regex LegacyParameterPattern = new(@"\{\{([a-zA-Z_][a-zA-Z0-9_.]*?)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// 替换Prompt模板中的参数占位符
    /// </summary>
    /// <param name="promptTemplate">包含参数占位符的Prompt模板</param>
    /// <param name="parameters">参数字典</param>
    /// <returns>替换后的Prompt内容</returns>
    public string ReplaceParameters(string promptTemplate, Dictionary<string, object>? parameters)
    {
        if (string.IsNullOrEmpty(promptTemplate) || parameters == null || parameters.Count == 0)
        {
            return promptTemplate;
        }

        return ParameterPattern.Replace(promptTemplate, match =>
        {
            var paramName = match.Groups[1].Value;
            if (parameters.TryGetValue(paramName, out var value))
            {
                return value?.ToString() ?? string.Empty;
            }

            // 如果参数不存在，保留原始占位符
            return match.Value;
        });
    }

    /// <summary>
    /// 从Prompt模板中提取所有参数名称
    /// </summary>
    /// <param name="promptTemplate">Prompt模板</param>
    /// <returns>参数名称列表</returns>
    public List<string> ExtractParameterNames(string promptTemplate)
    {
        if (string.IsNullOrEmpty(promptTemplate))
        {
            return new List<string>();
        }

        var matches = ParameterPattern.Matches(promptTemplate);
        return matches.Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    /// <summary>
    /// 从Prompt模板中提取所有参数信息(包括名称和描述)
    /// </summary>
    /// <param name="promptTemplate">Prompt模板</param>
    /// <returns>参数信息列表</returns>
    public List<ParameterInfo> ExtractParameters(string promptTemplate)
    {
        if (string.IsNullOrEmpty(promptTemplate))
        {
            return new List<ParameterInfo>();
        }

        var matches = ParameterPattern.Matches(promptTemplate);
        var parameters = new Dictionary<string, ParameterInfo>();

        foreach (Match match in matches)
        {
            var paramName = match.Groups[1].Value;
            var description = match.Groups.Count > 2 && match.Groups[2].Success
                ? match.Groups[2].Value.Trim()
                : null;

            // 如果同一个参数出现多次,优先使用有描述的版本
            if (!parameters.ContainsKey(paramName) ||
                (parameters.ContainsKey(paramName) && string.IsNullOrEmpty(parameters[paramName].Description) && !string.IsNullOrEmpty(description)))
            {
                parameters[paramName] = new ParameterInfo
                {
                    Name = paramName,
                    Description = description
                };
            }
        }

        return parameters.Values.ToList();
    }

    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 为参数列表生成 JSON Schema (OpenAPI 风格)
    /// </summary>
    /// <param name="parameters">参数名称列表</param>
    /// <param name="parameterOverrides">参数覆盖配置 (从 ConfigJson 读取)</param>
    /// <returns>JSON Schema 字符串</returns>
    public string GenerateParameterSchema(List<string> parameters, Dictionary<string, object>? parameterOverrides = null)
    {
        // 转换为 ParameterInfo 列表 (不带描述)
        var parameterInfos = parameters?.Select(p => new ParameterInfo { Name = p }).ToList()
            ?? new List<ParameterInfo>();
        return GenerateParameterSchema(parameterInfos, parameterOverrides);
    }

    /// <summary>
    /// 为参数列表生成 JSON Schema (OpenAPI 风格) - 支持参数描述
    /// </summary>
    /// <param name="parameters">参数信息列表 (包含名称和描述)</param>
    /// <param name="parameterOverrides">参数覆盖配置 (从 ConfigJson 读取)</param>
    /// <returns>JSON Schema 字符串</returns>
    public string GenerateParameterSchema(List<ParameterInfo> parameters, Dictionary<string, object>? parameterOverrides = null)
    {
        if (parameters == null || parameters.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }, _jsonOptions);
        }

        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var param in parameters)
        {
            var paramName = param.Name;

            // 默认参数配置
            var paramConfig = new Dictionary<string, object>
            {
                ["type"] = "string",
                // 优先使用参数自带的描述,如果没有则使用默认描述
                ["description"] = !string.IsNullOrWhiteSpace(param.Description)
                    ? param.Description
                    : $"Parameter: {paramName}"
            };

            // 应用覆盖配置
            if (parameterOverrides != null && parameterOverrides.TryGetValue(paramName, out var overrideValue))
            {
                if (overrideValue is JsonElement jsonElement)
                {
                    // 处理 JsonElement 类型 (从 ConfigJson 反序列化来的)
                    if (jsonElement.ValueKind == JsonValueKind.Object)
                    {
                        var overrideDict = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonElement.GetRawText());
                        if (overrideDict != null)
                        {
                            foreach (var kvp in overrideDict)
                            {
                                paramConfig[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                }
                else if (overrideValue is Dictionary<string, object> overrideDict)
                {
                    foreach (var kvp in overrideDict)
                    {
                        paramConfig[kvp.Key] = kvp.Value;
                    }
                }
            }

            properties[paramName] = paramConfig;

            // 检查是否必需 (默认所有参数都是必需的,除非 override 明确设置 required: false)
            if (!paramConfig.ContainsKey("required") || (bool)paramConfig["required"] != false)
            {
                required.Add(paramName);
            }
        }

        var schema = new
        {
            type = "object",
            properties,
            required = required.ToArray()
        };

        return JsonSerializer.Serialize(schema, _jsonOptions);
    }

    /// <summary>
    /// 使用参数值渲染 Prompt 模板
    /// </summary>
    /// <param name="template">Prompt 模板字符串</param>
    /// <param name="parameters">参数键值对</param>
    /// <returns>渲染后的 Prompt</returns>
    /// <exception cref="ArgumentException">当模板为空或参数缺失时抛出</exception>
    public string RenderPrompt(string template, Dictionary<string, string> parameters)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new ArgumentException("Prompt template cannot be empty", nameof(template));
        }

        if (parameters == null)
        {
            parameters = new Dictionary<string, string>();
        }

        // 提取所有参数
        var requiredParamInfos = ExtractParameters(template);
        var requiredParams = requiredParamInfos.Select(p => p.Name).ToList();

        // 创建不区分大小写的参数字典
        var caseInsensitiveParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in parameters)
        {
            caseInsensitiveParams[kvp.Key] = kvp.Value;
        }

        // 检查缺失参数
        var missingParams = requiredParams
            .Where(p => !caseInsensitiveParams.ContainsKey(p))
            .ToList();

        if (missingParams.Any())
        {
            throw new ArgumentException(
                $"Missing required parameters: {string.Join(", ", missingParams)}",
                nameof(parameters)
            );
        }

        // 替换参数 - 使用不区分大小写的字典
        var paramsDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in caseInsensitiveParams)
        {
            paramsDict[kvp.Key] = kvp.Value;
        }
        
        return ReplaceParameters(template, paramsDict);
    }

    /// <summary>
    /// 验证所有必需的参数是否都已提供
    /// </summary>
    /// <param name="promptTemplate">Prompt模板</param>
    /// <param name="parameters">提供的参数</param>
    /// <returns>缺失的参数名称列表</returns>
    public List<string> ValidateParameters(string promptTemplate, Dictionary<string, object>? parameters)
    {
        var requiredParams = ExtractParameterNames(promptTemplate);
        var providedParams = parameters?.Keys.ToHashSet() ?? new HashSet<string>();

        return requiredParams.Where(param => !providedParams.Contains(param)).ToList();
    }
}
