using System.Text.Json;
using System.Text.RegularExpressions;

namespace LY.LlmPool.Web.Services.Tools;

/// <summary>
/// 从 Prompt 模板中提取参数并生成 JSON Schema
/// </summary>
public partial class PromptParameterExtractor
{
    // 正则表达式: 匹配 {{参数名}} 或 {{嵌套.参数}}
    [GeneratedRegex(@"\{\{([a-zA-Z0-9_\.]+)\}\}", RegexOptions.Compiled)]
    private static partial Regex ParameterPattern();

    /// <summary>
    /// 从 Prompt 模板中提取所有参数名称 (去重时不区分大小写,返回首次出现的形式)
    /// </summary>
    /// <param name="template">Prompt 模板字符串</param>
    /// <returns>去重后的参数名称列表</returns>
    public List<string> ExtractParameters(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return new List<string>();
        }

        var matches = ParameterPattern().Matches(template);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parameters = new List<string>();

        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
            {
                var paramName = match.Groups[1].Value;
                if (seen.Add(paramName))
                {
                    parameters.Add(paramName);
                }
            }
        }

        return parameters;
    }

    /// <summary>
    /// 为参数列表生成 JSON Schema (OpenAPI 风格)
    /// </summary>
    /// <param name="parameters">参数名称列表</param>
    /// <param name="parameterOverrides">参数覆盖配置 (从 ConfigJson 读取)</param>
    /// <returns>JSON Schema 字符串</returns>
    public string GenerateParameterSchema(List<string> parameters, Dictionary<string, object>? parameterOverrides = null)
    {
        if (parameters == null || parameters.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            });
        }

        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var param in parameters)
        {
            // 默认参数配置
            var paramConfig = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = $"Parameter: {param}"
            };

            // 应用覆盖配置
            if (parameterOverrides != null && parameterOverrides.TryGetValue(param, out var overrideValue))
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

            properties[param] = paramConfig;

            // 检查是否必需 (默认所有参数都是必需的,除非 override 明确设置 required: false)
            if (!paramConfig.ContainsKey("required") || (bool)paramConfig["required"] != false)
            {
                required.Add(param);
            }
        }

        var schema = new
        {
            type = "object",
            properties,
            required = required.ToArray()
        };

        return JsonSerializer.Serialize(schema, new JsonSerializerOptions
        {
            WriteIndented = false
        });
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
        var requiredParams = ExtractParameters(template);

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

        // 替换参数
        var result = template;
        foreach (var param in requiredParams)
        {
            var pattern = $"{{{{{param}}}}}";
            var value = caseInsensitiveParams[param];
            result = result.Replace(pattern, value, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}
