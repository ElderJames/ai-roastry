using System.Text.RegularExpressions;

namespace LY.LlmPool.Web.Services;

public class PromptParameterService
{
    private static readonly Regex ParameterPattern = new(@"\{\{([a-zA-Z_][a-zA-Z0-9_]*)\}\}", RegexOptions.Compiled);
    
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