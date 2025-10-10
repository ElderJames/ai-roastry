using System.Text.Json;

namespace LY.LlmPool.Web.Services.Agents;

/// <summary>
/// 工具统一接口：提供名称、参数校验与执行。
/// </summary>
public interface IAgentTool
{
    string Id { get; }
    string Name { get; }
    string? Description { get; }

    // OpenAI JSON Schema (object) for parameters; optional
    JsonElement? ParametersSchema { get; }

    /// <summary>
    /// 校验输入参数是否符合 ParametersSchema；不抛异常，返回 bool 与错误信息。
    /// </summary>
    (bool ok, string? error) Validate(JsonElement? args);

    /// <summary>
    /// 执行工具逻辑，返回文本结果；失败应抛异常由上层处理。
    /// </summary>
    Task<string> ExecuteAsync(JsonElement? args, CancellationToken ct = default);
}
