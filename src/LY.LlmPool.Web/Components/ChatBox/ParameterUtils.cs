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