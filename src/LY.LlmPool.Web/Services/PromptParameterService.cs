using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace LY.LlmPool.Web.Services;

/// <summary>
/// 参数信息模型
/// </summary>
public class ParameterInfo
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsRequired { get; set; } = false;
}

/// <summary>
/// 参数验证结果
/// </summary>
public class ParameterValidationResult
{
    /// <summary>
    /// 参数名称
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// 参数描述
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// 是否必填
    /// </summary>
    public bool IsRequired { get; set; }
    
    /// <summary>
    /// 是否已提供
    /// </summary>
    public bool IsProvided { get; set; }
    
    /// <summary>
    /// 提供的值（如果有）
    /// </summary>
    public object? ProvidedValue { get; set; }
    
    /// <summary>
    /// 是否缺失（必填但未提供或值为空）
    /// </summary>
    public bool IsMissing => IsRequired && !IsProvided;
}

public class PromptParameterService
{
    // 支持两种格式: {{paramName}} 和 {{paramName|描述}}
    // 必传参数格式: {{*paramName}} 和 {{*paramName|描述}}
    // 参数名支持字母、数字、下划线和点号(用于嵌套参数如user.name)
    private static readonly Regex ParameterPattern = new(@"\{\{(\*?)([a-zA-Z_][a-zA-Z0-9_.]*?)(?:\|([^}]*))?\}\}", RegexOptions.Compiled);

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
            var paramName = match.Groups[2].Value; // Group 2 现在是参数名
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
        return matches.Select(m => m.Groups[2].Value).Distinct().ToList(); // Group 2 是参数名
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
            var isRequired = match.Groups[1].Value == "*"; // Group 1 是必传标识
            var paramName = match.Groups[2].Value; // Group 2 是参数名
            var description = match.Groups.Count > 3 && match.Groups[3].Success
                ? match.Groups[3].Value.Trim()
                : null;

            // 如果同一个参数出现多次,优先使用有描述的版本,或必传的版本
            if (!parameters.ContainsKey(paramName) ||
                (parameters.ContainsKey(paramName) && string.IsNullOrEmpty(parameters[paramName].Description) && !string.IsNullOrEmpty(description)) ||
                (parameters.ContainsKey(paramName) && !parameters[paramName].IsRequired && isRequired))
            {
                parameters[paramName] = new ParameterInfo
                {
                    Name = paramName,
                    Description = description,
                    IsRequired = isRequired || (parameters.ContainsKey(paramName) && parameters[paramName].IsRequired) // 保留已有的必传标识
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

            // 检查是否必需:
            // 1. 如果参数本身标记为必传 (IsRequired = true),则必定添加到 required
            // 2. 如果参数不是必传,但 override 明确设置了 required: true,也添加
            // 3. 默认情况下,非必传参数不添加到 required 数组
            bool shouldBeRequired = param.IsRequired;
            
            // 检查 override 是否有明确的 required 设置
            if (paramConfig.ContainsKey("required"))
            {
                shouldBeRequired = (bool)paramConfig["required"];
            }
            
            if (shouldBeRequired)
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

        // 创建不区分大小写的参数字典
        var caseInsensitiveParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in parameters)
        {
            caseInsensitiveParams[kvp.Key] = kvp.Value;
        }

        // 检查缺失的必传参数
        var missingRequiredParams = requiredParamInfos
            .Where(p => p.IsRequired && (!caseInsensitiveParams.ContainsKey(p.Name) || string.IsNullOrWhiteSpace(caseInsensitiveParams[p.Name])))
            .Select(p => p.Name)
            .ToList();

        if (missingRequiredParams.Any())
        {
            throw new ArgumentException(
                $"Missing required parameters: {string.Join(", ", missingRequiredParams)}",
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
        var paramInfos = ExtractParameters(promptTemplate);
        var providedParams = parameters?.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 只返回缺失的必传参数
        return paramInfos
            .Where(p => p.IsRequired && (!providedParams.Contains(p.Name) || 
                (parameters != null && parameters.TryGetValue(p.Name, out var val) && string.IsNullOrWhiteSpace(val?.ToString()))))
            .Select(p => p.Name)
            .ToList();
    }

    /// <summary>
    /// 验证所有必需的参数是否都已提供（AIFunctionArguments 版本）
    /// </summary>
    /// <param name="promptTemplate">Prompt模板</param>
    /// <param name="arguments">AIFunctionArguments 参数</param>
    /// <returns>缺失的参数名称列表</returns>
    public List<string> ValidateParameters(string promptTemplate, AIFunctionArguments arguments)
    {
        // 将 AIFunctionArguments 转换为 Dictionary<string, object>
        var parameters = new Dictionary<string, object>();
        if (arguments != null)
        {
            foreach (var arg in arguments)
            {
                if (arg.Value != null)
                {
                    parameters[arg.Key] = arg.Value;
                }
            }
        }
        
        return ValidateParameters(promptTemplate, parameters);
    }

    /// <summary>
    /// 从 JSON Schema 验证必填参数（用于 MCP Tool）
    /// </summary>
    /// <param name="jsonSchema">JSON Schema 字符串</param>
    /// <param name="arguments">AIFunctionArguments 参数</param>
    /// <returns>缺失的参数名称列表</returns>
    public List<string> ValidateParametersFromSchema(string jsonSchema, AIFunctionArguments arguments)
    {
        var missingParams = new List<string>();
        
        if (string.IsNullOrWhiteSpace(jsonSchema))
        {
            return missingParams;
        }
        
        try
        {
            var schema = JsonSerializer.Deserialize<JsonElement>(jsonSchema);
            
            // 提取 required 数组
            if (schema.TryGetProperty("required", out var requiredElement) && 
                requiredElement.ValueKind == JsonValueKind.Array)
            {
                var requiredParams = new List<string>();
                foreach (var item in requiredElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        requiredParams.Add(item.GetString()!);
                    }
                }
                
                // 检查缺失的必填参数
                var providedParams = arguments.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                missingParams = requiredParams
                    .Where(p => !providedParams.Contains(p) || 
                        (arguments.TryGetValue(p, out var val) && (val == null || string.IsNullOrWhiteSpace(val?.ToString()))))
                    .ToList();
            }
        }
        catch (Exception)
        {
            // 解析失败，返回空列表
        }
        
        return missingParams;
    }

    /// <summary>
    /// 详细验证参数并生成友好的错误提示（用于 App Tool）
    /// </summary>
    /// <param name="promptTemplate">Prompt模板</param>
    /// <param name="arguments">AIFunctionArguments 参数</param>
    /// <returns>验证失败时的详细错误消息，验证成功时返回 null</returns>
    public string? ValidateParametersDetailed(string promptTemplate, AIFunctionArguments arguments)
    {
        var paramInfos = ExtractParameters(promptTemplate);
        if (!paramInfos.Any())
        {
            return null; // 没有参数需要验证
        }
        
        // 创建不区分大小写的参数字典
        var argumentsDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in arguments)
        {
            argumentsDict[arg.Key] = arg.Value;
        }
        
        var results = new List<ParameterValidationResult>();
        
        foreach (var param in paramInfos)
        {
            var isProvided = argumentsDict.TryGetValue(param.Name, out var value) && 
                            value != null && 
                            !string.IsNullOrWhiteSpace(value.ToString());
            
            results.Add(new ParameterValidationResult
            {
                Name = param.Name,
                Description = param.Description,
                IsRequired = param.IsRequired,
                IsProvided = isProvided,
                ProvidedValue = isProvided ? value : null
            });
        }
        
        var missingRequired = results.Where(r => r.IsMissing).ToList();
        
        if (!missingRequired.Any())
        {
            return null; // 验证通过
        }
        
        // 构建详细的错误消息
        var errorMessage = "❌ **Parameter Validation Failed**\n\n";
        errorMessage += "**Missing Required Parameters:**\n";
        
        foreach (var missing in missingRequired)
        {
            errorMessage += $"  • **{missing.Name}** (REQUIRED)";
            if (!string.IsNullOrWhiteSpace(missing.Description))
            {
                errorMessage += $" - {missing.Description}";
            }
            errorMessage += "\n";
        }
        
        // 显示所有参数状态
        errorMessage += "\n**All Parameters:**\n";
        foreach (var result in results)
        {
            var status = result.IsMissing ? "❌ MISSING" : 
                        result.IsProvided ? "✅ PROVIDED" : 
                        "⚪ OPTIONAL";
            
            errorMessage += $"  {status} {result.Name}";
            
            if (result.IsRequired)
            {
                errorMessage += " (REQUIRED)";
            }
            
            if (!string.IsNullOrWhiteSpace(result.Description))
            {
                errorMessage += $" - {result.Description}";
            }
            
            if (result.IsProvided && result.ProvidedValue != null)
            {
                var valueStr = result.ProvidedValue.ToString();
                if (!string.IsNullOrEmpty(valueStr) && valueStr.Length > 50)
                {
                    valueStr = valueStr.Substring(0, 47) + "...";
                }
                errorMessage += $" = \"{valueStr}\"";
            }
            
            errorMessage += "\n";
        }
        
        errorMessage += "\n**Action Required:**\n";
        errorMessage += "Please call this tool again with all required parameters filled in.\n";
        
        return errorMessage;
    }

    /// <summary>
    /// 详细验证参数并生成友好的错误提示（用于 MCP Tool，基于 JSON Schema）
    /// </summary>
    /// <param name="jsonSchema">JSON Schema 字符串</param>
    /// <param name="arguments">AIFunctionArguments 参数</param>
    /// <returns>验证失败时的详细错误消息，验证成功时返回 null</returns>
    public string? ValidateParametersFromSchemaDetailed(string jsonSchema, AIFunctionArguments arguments)
    {
        if (string.IsNullOrWhiteSpace(jsonSchema))
        {
            return null;
        }
        
        try
        {
            var schema = JsonSerializer.Deserialize<JsonElement>(jsonSchema);
            
            // 提取 required 数组
            var requiredParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (schema.TryGetProperty("required", out var requiredElement) && 
                requiredElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in requiredElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        requiredParams.Add(item.GetString()!);
                    }
                }
            }
            
            // 提取 properties 以获取描述
            var properties = new Dictionary<string, JsonElement>();
            if (schema.TryGetProperty("properties", out var propsElement) && 
                propsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in propsElement.EnumerateObject())
                {
                    properties[prop.Name] = prop.Value;
                }
            }
            
            // 构建验证结果
            var results = new List<ParameterValidationResult>();
            
            // 创建不区分大小写的参数字典
            var argumentsDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var arg in arguments)
            {
                argumentsDict[arg.Key] = arg.Value;
            }
            
            // 检查所有定义的参数
            foreach (var propName in properties.Keys)
            {
                var isRequired = requiredParams.Contains(propName);
                var isProvided = argumentsDict.TryGetValue(propName, out var value) && 
                                value != null && 
                                !string.IsNullOrWhiteSpace(value.ToString());
                
                string? description = null;
                if (properties[propName].TryGetProperty("description", out var descElement))
                {
                    description = descElement.GetString();
                }
                
                results.Add(new ParameterValidationResult
                {
                    Name = propName,
                    Description = description,
                    IsRequired = isRequired,
                    IsProvided = isProvided,
                    ProvidedValue = isProvided ? value : null
                });
            }
            
            var missingRequired = results.Where(r => r.IsMissing).ToList();
            
            if (!missingRequired.Any())
            {
                return null; // 验证通过
            }
            
            // 构建详细的错误消息
            var errorMessage = "❌ **Parameter Validation Failed**\n\n";
            errorMessage += "**Missing Required Parameters:**\n";
            
            foreach (var missing in missingRequired)
            {
                errorMessage += $"  • **{missing.Name}** (REQUIRED)";
                if (!string.IsNullOrWhiteSpace(missing.Description))
                {
                    errorMessage += $" - {missing.Description}";
                }
                errorMessage += "\n";
            }
            
            // 显示所有参数状态
            errorMessage += "\n**All Parameters:**\n";
            foreach (var result in results)
            {
                var status = result.IsMissing ? "❌ MISSING" : 
                            result.IsProvided ? "✅ PROVIDED" : 
                            "⚪ OPTIONAL";
                
                errorMessage += $"  {status} {result.Name}";
                
                if (result.IsRequired)
                {
                    errorMessage += " (REQUIRED)";
                }
                
                if (!string.IsNullOrWhiteSpace(result.Description))
                {
                    errorMessage += $" - {result.Description}";
                }
                
                if (result.IsProvided && result.ProvidedValue != null)
                {
                    var valueStr = result.ProvidedValue.ToString();
                    if (!string.IsNullOrEmpty(valueStr) && valueStr.Length > 50)
                    {
                        valueStr = valueStr.Substring(0, 47) + "...";
                    }
                    errorMessage += $" = \"{valueStr}\"";
                }
                
                errorMessage += "\n";
            }
            
            errorMessage += "\n**Action Required:**\n";
            errorMessage += "Please call this tool again with all required parameters filled in.\n";
            
            return errorMessage;
        }
        catch (Exception)
        {
            return null; // 解析失败，跳过验证
        }
    }
}
