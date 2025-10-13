using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services.Agents;
using LY.LlmPool.Web.Models;
using LY.LlmPool.Web.Models.Tools;
using System.Text.Json;
using Microsoft.SemanticKernel;
using LY.LlmPool.Web.Services.Tools;
using Microsoft.Extensions.AI;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using LY.LlmPool.Web.Services.Aggregation;

namespace LY.LlmPool.Web.Services;

/// <summary>
/// 工具提供者服务 - 根据 PromptTool 列表创建可执行的 AITool 对象
/// </summary>
public class ToolProviderService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ToolProviderService> _logger;
    private readonly PromptParameterService _promptParameterService;
    private readonly PromptParameterExtractor _parameterExtractor;

    public ToolProviderService(
        IServiceProvider serviceProvider, 
        ILogger<ToolProviderService> logger,
        PromptParameterService promptParameterService,
        PromptParameterExtractor parameterExtractor)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _promptParameterService = promptParameterService;
        _parameterExtractor = parameterExtractor;
    }

    /// <summary>
    /// 从工具元数据列表创建可执行的 AITool 对象
    /// </summary>
    /// <param name="toolMetadataList">工具元数据列表</param>
    /// <returns>AITool 列表</returns>
    public async Task<List<AITool>> GetToolsFromMetadataAsync(IEnumerable<ToolMetadata> toolMetadataList)
    {
        var tools = new List<AITool>();
        
        _logger.LogInformation("=== GetToolsFromMetadataAsync ===");
        _logger.LogInformation("Received {Count} tool metadata items", toolMetadataList.Count());
        
        foreach (var metadata in toolMetadataList)
        {
            _logger.LogInformation("Processing tool metadata: {Name} ({Source}:{SourceId})", 
                metadata.Name, metadata.Source, metadata.SourceId);
            
            try
            {
                AITool? tool = null;
                
                if (metadata.Source == ToolSource.App)
                {
                    _logger.LogInformation("  Calling CreateAppToolAsync for SourceId: {SourceId}", metadata.SourceId);
                    tool = await CreateAppToolAsync(metadata.SourceId);
                    _logger.LogInformation("  CreateAppToolAsync returned: {IsNull}", tool == null ? "NULL" : "NOT NULL");
                }
                else if (metadata.Source == ToolSource.MCP)
                {
                    _logger.LogInformation("  Calling CreateMcpToolAsync for SourceId: {SourceId}", metadata.SourceId);
                    tool = await CreateMcpToolAsync(metadata.SourceId);
                    _logger.LogInformation("  CreateMcpToolAsync returned: {IsNull}", tool == null ? "NULL" : "NOT NULL");
                }
                
                if (tool != null)
                {
                    tools.Add(tool);
                    var toolName = tool is AIFunction func ? func.Name : "Unknown";
                    _logger.LogInformation("  ✓ Successfully created AITool: {ToolName}", toolName);
                }
                else
                {
                    _logger.LogWarning("  ✗ Failed to create AITool for {Name} - tool is null", metadata.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "  ✗ Exception creating AITool from metadata: {Name}", metadata.Name);
            }
        }
        
        _logger.LogInformation("Total AITools created: {Count}", tools.Count);
        return tools;
    }

    /// <summary>
    /// 从 Prompt 的 PromptTools 列表创建可执行的 AITool 对象
    /// </summary>
    public async Task<List<AITool>> GetToolsForPromptAsync(LlmPrompt? prompt)
    {
        var tools = new List<AITool>();
        
        if (prompt == null)
        {
            _logger.LogWarning("GetToolsForPromptAsync called with null prompt");
            return tools;
        }

        _logger.LogInformation("=== GetToolsForPromptAsync ===");
        _logger.LogInformation("Prompt ID: {PromptId}", prompt.Id);

        if (prompt.PromptTools == null)
        {
            _logger.LogWarning("Prompt {PromptId} has null PromptTools collection", prompt.Id);
            return tools;
        }

        if (!prompt.PromptTools.Any())
        {
            _logger.LogWarning("Prompt {PromptId} has no tools configured (PromptTools.Count = 0)", prompt.Id);
            return tools;
        }

        _logger.LogInformation("Loading {Count} tools for Prompt {PromptId}", 
            prompt.PromptTools.Count, prompt.Id);

        foreach (var promptTool in prompt.PromptTools)
        {
            _logger.LogInformation("Processing PromptTool:");
            _logger.LogInformation("  ToolId: {ToolId}", promptTool.ToolId);
            _logger.LogInformation("  ToolType: {ToolType}", promptTool.ToolType);
            
            try
            {
                var tool = await CreateAIToolAsync(promptTool);
                if (tool != null)
                {
                    tools.Add(tool);
                    var toolName = tool is AIFunction func ? func.Name : "Unknown";
                    _logger.LogInformation("  ✓ Successfully created AITool: {ToolName}", toolName);
                }
                else
                {
                    _logger.LogWarning(
                        "  ✗ Failed to create AITool - ToolId: {ToolId}, Type: {ToolType}. " +
                        "This tool may have been deleted or is misconfigured. " +
                        "Please check the Prompt's tool bindings.",
                        promptTool.ToolId, promptTool.ToolType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "  ✗ Exception creating AITool from PromptTool: {ToolId}, Type: {ToolType}", 
                    promptTool.ToolId, promptTool.ToolType);
            }
        }

        _logger.LogInformation("Total AITools created: {Count}", tools.Count);
        return tools;
    }

    /// <summary>
    /// 根据 PromptTool 创建对应的 AITool
    /// </summary>
    private async Task<AITool?> CreateAIToolAsync(PromptTool promptTool)
    {
        switch (promptTool.ToolType)
        {
            case ToolType.Internal:
                // App Tool - 创建一个调用 LlmApp 的 AITool
                return await CreateAppToolAsync(promptTool.ToolId);

            case ToolType.Mcp:
                // MCP Tool - 通过 MCP Server 执行工具
                return await CreateMcpToolAsync(promptTool.ToolId);

            default:
                _logger.LogWarning("Unknown ToolType: {ToolType} for ToolId: {ToolId}", 
                    promptTool.ToolType, promptTool.ToolId);
                return null;
        }
    }

    /// <summary>
    /// 为 App Tool 创建 AITool
    /// ToolId 应该是一个 Tool 类型的 LlmApp 的 ID
    /// </summary>
    private async Task<AITool?> CreateAppToolAsync(string appId)
    {
        using var scope = _serviceProvider.CreateScope();
        var appService = scope.ServiceProvider.GetRequiredService<AppService>();
        
        _logger.LogInformation("CreateAppToolAsync: Looking for App with ID {AppId}", appId);
        
        // 加载 App 信息
        var app = await appService.GetAppByIdAsync(appId);
        if (app == null)
        {
            _logger.LogError(
                "❌ Tool App not found: ID {AppId}. " +
                "Possible causes: " +
                "1) App was deleted but PromptTool binding still exists; " +
                "2) ToolMetadataService cache is outdated; " +
                "3) Database migration issue. " +
                "Solution: Edit the Prompt and remove/re-add the tool binding, or check database integrity.",
                appId);
            return null;
        }

        _logger.LogInformation("Found App: {AppName}, Type: {AppType}", app.Name, app.AppType);

        if (app.AppType != "Tool")
        {
            _logger.LogWarning("App {AppName} (ID: {AppId}) is not a Tool type, it's {AppType}", 
                app.Name, appId, app.AppType);
            return null;
        }

        // 获取 App 的 Prompt 和配置
        // Tool App 应该有一个绑定的 Prompt，从中获取配置
        if (string.IsNullOrEmpty(app.PromptId))
        {
            _logger.LogWarning("Tool App {AppName} has no Prompt binding", app.Name);
            return null;
        }

        var promptService = scope.ServiceProvider.GetRequiredService<PromptService>();
        var toolPrompt = await promptService.GetPromptByIdAsync(app.PromptId);
        
        if (toolPrompt == null)
        {
            _logger.LogWarning("Tool App {AppName} references non-existent Prompt {PromptId}", 
                app.Name, app.PromptId);
            return null;
        }

        // 获取 LlmConfig 或 Endpoint
        // 优先使用 App 直接绑定的配置，否则使用 AgentMember
        LlmConfig? llmConfig = null;
        LlmEndpoint? endpoint = null;
        
        if (!string.IsNullOrEmpty(app.LlmConfigId))
        {
            var configService = scope.ServiceProvider.GetRequiredService<ConfigService>();
            llmConfig = await configService.GetConfigByIdAsync(app.LlmConfigId);
            _logger.LogInformation("Tool App {AppName} using direct LlmConfig: {ConfigName}", 
                app.Name, llmConfig?.Name);
        }
        else if (!string.IsNullOrEmpty(app.EndpointId))
        {
            var llmPoolService = scope.ServiceProvider.GetRequiredService<LlmPoolService>();
            endpoint = await llmPoolService.GetEndpointByIdAsync(app.EndpointId);
            _logger.LogInformation("Tool App {AppName} using direct Endpoint: {EndpointName}", 
                app.Name, endpoint?.Name);
        }
        else
        {
            // 尝试从 AgentMember 获取配置
            var members = await appService.GetAgentMembersByAppIdAsync(app.Id!);
            var member = members.FirstOrDefault();
            
            if (member != null && member.LlmConfig != null)
            {
                llmConfig = member.LlmConfig;
                _logger.LogInformation("Tool App {AppName} using AgentMember LlmConfig: {ConfigName}", 
                    app.Name, llmConfig.Name);
            }
            else
            {
                _logger.LogError(
                    "Tool App {AppName} has no LlmConfig, Endpoint, or valid AgentMember. " +
                    "Please configure at least one of: " +
                    "1) Set LlmConfigId on the App; " +
                    "2) Set EndpointId on the App; " +
                    "3) Create an AgentMember with LlmConfig",
                    app.Name);
                return null;
            }
        }

        _logger.LogInformation("Tool App {AppName} using Prompt: {PromptName}", 
            app.Name, toolPrompt.Name);

        // 使用 PromptParameterExtractor 提取参数信息并生成 JSON Schema
        var paramNames = new List<string>();
        string parameterSchemaJson = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new { },
            required = Array.Empty<string>()
        });
        
        if (toolPrompt != null && !string.IsNullOrWhiteSpace(toolPrompt.Content))
        {
            paramNames = _parameterExtractor.ExtractParameters(toolPrompt.Content).ToList();
            
            if (paramNames.Any())
            {
                // 使用 PromptParameterExtractor 生成 OpenAPI 风格的 JSON Schema
                parameterSchemaJson = _parameterExtractor.GenerateParameterSchema(paramNames, null);
                _logger.LogInformation("Generated parameter schema for {AppName}: {Schema}", 
                    app.Name, parameterSchemaJson);
            }
        }

        // 创建 AIFunction - 构建执行函数
        var executeFunc = async (AIFunctionArguments arguments, CancellationToken ct) =>
        {
            using var execScope = _serviceProvider.CreateScope();
            var chatClientService = execScope.ServiceProvider.GetRequiredService<IChatClientService>();

            // 构建消息列表
            var messages = new List<AIChatMessage>();

            // 添加系统提示（来自 Prompt）
            if (toolPrompt != null && !string.IsNullOrWhiteSpace(toolPrompt.Content))
            {
                // 将 AIFunctionArguments 转换为 Dictionary<string, object>
                var toolParameters = new Dictionary<string, object>();
                foreach (var arg in arguments)
                {
                    if (arg.Value != null)
                    {
                        toolParameters[arg.Key] = arg.Value;
                    }
                }
                
                // 使用 PromptParameterService 替换参数
                var promptContent = _promptParameterService.ReplaceParameters(
                    toolPrompt.Content, 
                    toolParameters
                );
                
                messages.Add(new AIChatMessage(ChatRole.System, promptContent));
            }

            // 将参数转换为用户消息
            var userMessage = arguments.Any()
                ? string.Join(", ", arguments.Select(a => $"{a.Key}: {a.Value}"))
                : "Execute tool";

            messages.Add(new AIChatMessage(ChatRole.User, userMessage));

            // 使用 LlmConfig 调用 ChatClientService
            ChatResponse? response = null;
            
            if (llmConfig != null)
            {
                response = await chatClientService.SendMessageAsync(
                    llmConfig,
                    messages,
                    tools: null // Tool 本身不再嵌套调用其他工具
                );
            }
            else if (endpoint != null)
            {
                // 使用 Endpoint 的第一个可用配置
                var endpointConfig = endpoint.EndpointConfigs
                    .OrderBy(c => c.Priority)
                    .FirstOrDefault();
                    
                if (endpointConfig != null)
                {
                    var configService = execScope.ServiceProvider.GetRequiredService<ConfigService>();
                    var endpointLlmConfig = await configService.GetConfigByIdAsync(endpointConfig.LlmConfigId);
                    if (endpointLlmConfig != null)
                    {
                        response = await chatClientService.SendMessageAsync(
                            endpointLlmConfig,
                            messages,
                            tools: null
                        );
                    }
                }
            }

            if (response == null || !string.Equals(response.Status, "success", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Tool execution failed: {response?.Message ?? "Unknown error"}"
                );
            }

            return response.Message ?? string.Empty;
        };

        // 创建基础 AIFunction
        var baseFunction = AIFunctionFactory.Create(
            executeFunc,
            app.Name,
            app.Description ?? $"Execute tool: {app.Name}"
        );

        // 使用自定义包装类覆盖 JsonSchema,包含从 Prompt 提取的参数
        var aiFunction = new AppToolAIFunction(baseFunction, parameterSchemaJson);

        _logger.LogInformation("Created AIFunction for App Tool: {AppName} with {ParamCount} parameters", 
            app.Name, paramNames.Count);

        return aiFunction;
    }

    /// <summary>
    /// 为 MCP Tool 创建 AITool
    /// mcpToolId 可能是 ToolMetadata.SourceId (Guid) 或其他格式
    /// </summary>
    private async Task<AITool?> CreateMcpToolAsync(string mcpToolId)
    {
        using var scope = _serviceProvider.CreateScope();
        var toolMetadataService = scope.ServiceProvider.GetRequiredService<ToolMetadataService>();
        
        _logger.LogInformation("CreateMcpToolAsync: Looking for MCP Tool with ID {McpToolId}", mcpToolId);
        
        // 尝试从 ToolMetadataService 获取所有工具并查找匹配的 MCP Tool
        var allTools = await toolMetadataService.GetAllToolsAsync();
        var mcpTool = allTools.FirstOrDefault(t => 
            t.Source == ToolSource.MCP && 
            t.SourceId.ToString() == mcpToolId);
        
        if (mcpTool == null)
        {
            _logger.LogWarning(
                "⚠️  MCP Tool not found in cache: ID {McpToolId}. " +
                "Possible causes: " +
                "1) MCP Server is not configured or disabled; " +
                "2) Tool was removed from MCP Server; " +
                "3) ToolMetadataService cache needs refresh. ",
                mcpToolId);
            
            return null; // 跳过找不到的工具
        }
        
        // 解析 MCP Server ID 和 Tool Name
        var serverId = mcpTool.SourceId;
        var toolName = mcpTool.Name;
        var toolDescription = mcpTool.Description ?? $"MCP Tool: {toolName}";
        
        _logger.LogInformation("Creating MCP Tool: {ToolName} from Server: {ServerId}", toolName, serverId);
        
        // ========== 优化 3: 获取 McpClientsFactory（利用其内置缓存） ==========
        var mcpClientsFactory = scope.ServiceProvider.GetRequiredService<McpClientsFactory>();
        
        // 创建执行委托
        var executeFunc = async (IReadOnlyList<KeyValuePair<string, object?>> arguments, CancellationToken ct) =>
        {
            _logger.LogInformation("Executing MCP Tool {ToolName} (Server: {ServerId}) with arguments: {Args}", 
                toolName, serverId, string.Join(", ", arguments.Select(a => $"{a.Key}={a.Value}")));
            
            try
            {
                // 获取 MCP Client（McpClientsFactory 内部已有 ConcurrentDictionary 缓存）
                var client = await mcpClientsFactory.GetMcpClientAsync(serverId, ct);
                if (client == null)
                {
                    var errorMsg = $"[MCP Error] Cannot connect to MCP Server '{serverId}'. Server may be offline or not configured.";
                    _logger.LogError(errorMsg);
                    return errorMsg;
                }
                
                // 转换参数为 Dictionary
                Dictionary<string, object?>? mcpArguments = null;
                if (arguments.Any())
                {
                    mcpArguments = new Dictionary<string, object?>();
                    foreach (var arg in arguments)
                    {
                        mcpArguments[arg.Key] = arg.Value;
                    }
                }
                
                // 调用 MCP Tool
                var result = await client.CallToolAsync(toolName, mcpArguments);
                
                // ========== 优化 2: 智能结果解析 ==========
                if (result != null)
                {
                    string parsedResult = ParseMcpToolResult(result, toolName);
                    
                    _logger.LogInformation("MCP Tool {ToolName} executed successfully. Result length: {Length}", 
                        toolName, parsedResult.Length);
                    
                    return parsedResult;
                }
                
                _logger.LogWarning("MCP Tool {ToolName} returned null result", toolName);
                return "[MCP Tool executed but returned null]";
            }
            catch (Exception ex)
            {
                var errorMsg = $"[MCP Error] Failed to execute tool '{toolName}': {ex.Message}";
                _logger.LogError(ex, "Failed to execute MCP Tool {ToolName} on Server {ServerId}", toolName, serverId);
                return errorMsg;
            }
        };

        // ========== 优化 1: 参数 Schema 支持 ==========
        // Microsoft.Extensions.AI 会从委托签名自动推断参数
        // MCP Tool 的参数 Schema 已在 ToolMetadata.ParametersSchema 中存储
        // 注意: AIFunctionFactory.Create 的参数推断足够智能，无需显式传递 JSON Schema
        
        var aiFunction = AIFunctionFactory.Create(
            executeFunc,
            name: toolName,
            description: toolDescription
        );

        _logger.LogInformation("Created AIFunction for MCP Tool: {ToolName} (Server: {ServerId})", 
            toolName, serverId);

        return aiFunction;
    }
    
    /// <summary>
    /// 智能解析 MCP Tool 结果
    /// 优先提取文本内容，如果无法解析则返回格式化的 JSON
    /// </summary>
    private string ParseMcpToolResult(object result, string toolName)
    {
        try
        {
            // 尝试反射获取 Content 属性
            var resultType = result.GetType();
            var contentProperty = resultType.GetProperty("Content");
            
            if (contentProperty != null)
            {
                var contentValue = contentProperty.GetValue(result);
                
                // Content 可能是数组
                if (contentValue is System.Collections.IEnumerable contentArray and not string)
                {
                    var textParts = new List<string>();
                    
                    foreach (var item in contentArray)
                    {
                        if (item == null) continue;
                        
                        var itemType = item.GetType();
                        
                        // 尝试获取 Text 属性（TextContent）
                        var textProp = itemType.GetProperty("Text");
                        if (textProp != null)
                        {
                            var textValue = textProp.GetValue(item)?.ToString();
                            if (!string.IsNullOrEmpty(textValue))
                            {
                                textParts.Add(textValue);
                            }
                        }
                        // 尝试获取 Uri 属性（ImageContent/ResourceContent）
                        else
                        {
                            var uriProp = itemType.GetProperty("Uri");
                            var mimeTypeProp = itemType.GetProperty("MimeType");
                            
                            if (uriProp != null)
                            {
                                var uri = uriProp.GetValue(item)?.ToString();
                                var mimeType = mimeTypeProp?.GetValue(item)?.ToString();
                                
                                textParts.Add($"[Resource: {uri}{(mimeType != null ? $" ({mimeType})" : "")}]");
                            }
                        }
                    }
                    
                    // 如果成功提取了文本，返回合并结果
                    if (textParts.Any())
                    {
                        var combined = string.Join("\n\n", textParts);
                        _logger.LogDebug("Parsed {Count} content blocks from MCP result", textParts.Count);
                        return combined;
                    }
                }
            }
            
            // Fallback: 返回格式化的 JSON
            _logger.LogDebug("Could not parse MCP result structure for {ToolName}, falling back to JSON", toolName);
            return JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse MCP result for {ToolName}, returning JSON", toolName);
            
            // 发生任何异常，返回 JSON
            return JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
    }
}