using Microsoft.Extensions.AI;
using System.Text.Json;

namespace LY.LlmPool.Web.Services.Tools;

/// <summary>
/// 自定义 AIFunction 包装类 - 用于 MCP Tool
/// 覆盖 JsonSchema 属性,使其包含从 MCP Server 获取的参数 Schema
/// </summary>
internal class McpToolAIFunction : AIFunction
{
    private readonly AIFunction _innerFunction;
    private readonly JsonElement _customJsonSchema;

    public McpToolAIFunction(AIFunction innerFunction, string customJsonSchemaString)
    {
        _innerFunction = innerFunction;
        _customJsonSchema = JsonDocument.Parse(customJsonSchemaString).RootElement;
    }

    public override string Name => _innerFunction.Name;

    public override string Description => _innerFunction.Description ?? string.Empty;

    /// <summary>
    /// 覆盖 JsonSchema 属性,返回从 MCP Tool 定义的 Schema
    /// </summary>
    public override JsonElement JsonSchema => _customJsonSchema;

    public override JsonElement? ReturnJsonSchema => _innerFunction.ReturnJsonSchema;

    public override IReadOnlyDictionary<string, object?> AdditionalProperties => 
        _innerFunction.AdditionalProperties;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments? arguments, 
        CancellationToken cancellationToken)
    {
        return _innerFunction.InvokeAsync(arguments, cancellationToken);
    }
}
