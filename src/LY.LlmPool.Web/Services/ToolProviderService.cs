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

    public ToolProviderService(
        IServiceProvider serviceProvider, 
        ILogger<ToolProviderService> logger,
        PromptParameterService promptParameterService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _promptParameterService = promptParameterService;
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
    /// 从 App 关联的工具获取 AITool 列表
    /// App 通过其 Prompt 的 PromptTools 关联多个 App Tool 和 MCP Tool
    /// </summary>
    public async Task<List<AITool>> GetToolsForAppAsync(LlmApp? app)
    {
        var tools = new List<AITool>();
        
        if (app == null)
        {
            _logger.LogWarning("GetToolsForAppAsync called with null app");
            return tools;
        }

        _logger.LogInformation("=== GetToolsForAppAsync ===");
        _logger.LogInformation("App ID: {AppId}, Name: {AppName}, Type: {AppType}", app.Id, app.Name, app.AppType);

        // 如果 App 有关联的 Prompt，从 Prompt 的 PromptTools 加载工具
        if (!string.IsNullOrEmpty(app.PromptId))
        {
            using var scope = _serviceProvider.CreateScope();
            var llmPoolService = scope.ServiceProvider.GetRequiredService<LlmPoolService>();
            
            var prompt = await llmPoolService.GetPromptByIdAsync(app.PromptId);
            if (prompt != null)
            {
                _logger.LogInformation("App {AppName} has associated Prompt {PromptId}, loading tools from PromptTools", 
                    app.Name, prompt.Id);
                
                // 直接使用 GetToolsForPromptAsync，它会处理所有 PromptTool（包括 App Tool 和 MCP Tool）
                tools = await GetToolsForPromptAsync(prompt);
                
                if (tools.Any())
                {
                    _logger.LogInformation("Successfully loaded {Count} tools from Prompt {PromptId} for App {AppName}", 
                        tools.Count, prompt.Id, app.Name);
                }
                else
                {
                    _logger.LogInformation("Prompt {PromptId} has no tools configured for App {AppName}", 
                        prompt.Id, app.Name);
                }
            }
            else
            {
                _logger.LogWarning("App {AppId} references Prompt {PromptId} but Prompt not found", 
                    app.Id, app.PromptId);
            }
        }
        else
        {
            _logger.LogInformation("App {AppId} ({AppName}) has no associated Prompt, no tools to load", 
                app.Id, app.Name);
        }

        _logger.LogInformation("Total AITools loaded for App {AppName}: {Count}", app.Name, tools.Count);
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
    /// <param name="appId">App 的 ID</param>
    /// <param name="maxRecursionDepth">最大递归深度，防止无限递归</param>
    /// <param name="currentDepth">当前递归深度</param>
    private async Task<AITool?> CreateAppToolAsync(string appId, int maxRecursionDepth = 5, int currentDepth = 0)
    {
        // 防止无限递归
        if (currentDepth >= maxRecursionDepth)
        {
            _logger.LogWarning(
                "⚠️  Max recursion depth ({MaxDepth}) reached for App Tool {AppId}. " +
                "Tool chain may be too deep or contain circular references.",
                maxRecursionDepth, appId);
            return null;
        }
        
        using var scope = _serviceProvider.CreateScope();
        
        _logger.LogInformation("CreateAppToolAsync: Looking for App with ID {AppId}", appId);
        
        // 🔧 优化：先从 ToolMetadataService 缓存查找，验证工具是否存在
        var toolMetadataService = scope.ServiceProvider.GetRequiredService<ToolMetadataService>();
        var allTools = await toolMetadataService.GetAllToolsAsync();
        var toolMetadata = allTools.FirstOrDefault(t => 
            t.Source == ToolSource.App && 
            t.SourceId.ToString() == appId);
        
        if (toolMetadata == null)
        {
            _logger.LogWarning(
                "⚠️  App Tool not found in cache: ID {AppId}. " +
                "Possible causes: " +
                "1) App was deleted but PromptTool binding still exists; " +
                "2) ToolMetadataService cache is outdated; " +
                "3) App is not of type 'Tool'. ",
                appId);
            return null;
        }
        
        _logger.LogInformation("Found App Tool in cache: {ToolName}", toolMetadata.Name);
        
        // 加载完整的 App 信息（需要 Prompt, Config 等）
        var appService = scope.ServiceProvider.GetRequiredService<AppService>();
        var app = await appService.GetAppByIdAsync(appId);
        
        if (app == null)
        {
            _logger.LogError(
                "❌ Tool App not found in database: ID {AppId}. " +
                "Cache is out of sync with database. " +
                "Please refresh tool metadata cache.",
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

        // 使用 PromptParameterService 提取参数信息(包含描述)并生成 JSON Schema
        var paramInfos = new List<ParameterInfo>();
        string parameterSchemaJson = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new { },
            required = Array.Empty<string>()
        });
        
        if (toolPrompt != null && !string.IsNullOrWhiteSpace(toolPrompt.Content))
        {
            // 使用 PromptParameterService 提取参数(支持描述: {{param|desc}})
            paramInfos = _promptParameterService.ExtractParameters(toolPrompt.Content);
            
            if (paramInfos.Any())
            {
                // 使用 PromptParameterService 生成 OpenAPI 风格的 JSON Schema (包含参数描述)
                parameterSchemaJson = _promptParameterService.GenerateParameterSchema(paramInfos, null);
                _logger.LogInformation("Generated parameter schema for {AppName}: {Schema}", 
                    app.Name, parameterSchemaJson);
            }
        }

        // 创建 AIFunction - 构建执行函数（使用流式调用）
        var executeFunc = async (AIFunctionArguments arguments, CancellationToken ct) =>
        {
            using var execScope = _serviceProvider.CreateScope();
            var chatClientService = execScope.ServiceProvider.GetRequiredService<IChatClientService>();
            var loggerFactory = execScope.ServiceProvider.GetRequiredService<ILoggerFactory>();

            _logger.LogInformation(
                "📞 Executing App Tool '{ToolName}' at depth {Depth}/{MaxDepth} (Streaming Mode)",
                app.Name, currentDepth, maxRecursionDepth);

            // 🔑 验证必填参数
            if (toolPrompt != null && !string.IsNullOrWhiteSpace(toolPrompt.Content))
            {
                var validationError = _promptParameterService.ValidateParametersDetailed(
                    toolPrompt.Content,
                    arguments
                );
                
                if (validationError != null)
                {
                    _logger.LogWarning("Tool {ToolName} validation failed", app.Name);
                    return validationError;
                }
            }

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

            // 🔧 加载 Prompt 关联的工具（支持递归调用和并行执行）
            List<AITool>? nestedTools = null;
            if (toolPrompt != null && toolPrompt.PromptTools != null && toolPrompt.PromptTools.Any())
            {
                _logger.LogInformation(
                    "🔗 App Tool '{ToolName}' has {Count} nested tools, loading recursively (depth: {Depth})",
                    app.Name, toolPrompt.PromptTools.Count, currentDepth + 1);

                nestedTools = new List<AITool>();
                foreach (var promptTool in toolPrompt.PromptTools)
                {
                    try
                    {
                        AITool? nestedTool = null;
                        
                        if (promptTool.ToolType == ToolType.Internal)
                        {
                            // 递归创建 App Tool（传递深度限制）
                            nestedTool = await CreateAppToolAsync(
                                promptTool.ToolId, 
                                maxRecursionDepth, 
                                currentDepth + 1
                            );
                        }
                        else if (promptTool.ToolType == ToolType.Mcp)
                        {
                            // MCP Tool 不需要递归
                            nestedTool = await CreateMcpToolAsync(promptTool.ToolId);
                        }
                        
                        if (nestedTool != null)
                        {
                            nestedTools.Add(nestedTool);
                            var toolName = nestedTool is AIFunction func ? func.Name : "Unknown";
                            _logger.LogInformation("  ✓ Loaded nested tool: {ToolName}", toolName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, 
                            "  ✗ Failed to load nested tool {ToolId} ({ToolType}) for App '{AppName}'",
                            promptTool.ToolId, promptTool.ToolType, app.Name);
                    }
                }
                
                _logger.LogInformation(
                    "✅ Loaded {LoadedCount}/{TotalCount} nested tools for App '{ToolName}'",
                    nestedTools.Count, toolPrompt.PromptTools.Count, app.Name);
            }

            // 使用流式调用收集完整响应
            var collector = new StreamingResponseCollector(
                loggerFactory.CreateLogger<StreamingResponseCollector>());
            
            try
            {
                IAsyncEnumerable<ChatStreamingUpdate> streamingUpdates;
                
                if (llmConfig != null)
                {
                    _logger.LogInformation("Starting streaming call with LlmConfig for App Tool '{ToolName}'", app.Name);
                    streamingUpdates = chatClientService.SendStreamingMessageWithDetailsAsync(
                        llmConfig, messages, parameters: null, tools: nestedTools);
                }
                else if (endpoint != null)
                {
                    var endpointConfig = endpoint.EndpointConfigs.OrderBy(c => c.Priority).FirstOrDefault();
                    if (endpointConfig == null)
                        throw new InvalidOperationException($"No endpoint config available for App Tool '{app.Name}'");
                    
                    var configService = execScope.ServiceProvider.GetRequiredService<ConfigService>();
                    var endpointLlmConfig = await configService.GetConfigByIdAsync(endpointConfig.LlmConfigId);
                    if (endpointLlmConfig == null)
                        throw new InvalidOperationException($"Endpoint config not found for App Tool '{app.Name}'");
                    
                    _logger.LogInformation("Starting streaming call with Endpoint for App Tool '{ToolName}'", app.Name);
                    streamingUpdates = chatClientService.SendStreamingMessageWithDetailsAsync(
                        endpointLlmConfig, messages, parameters: null, tools: nestedTools);
                }
                else
                {
                    throw new InvalidOperationException($"No LlmConfig or Endpoint configured for App Tool '{app.Name}'");
                }

                // 🔥 使用 ConvertToSegmentsStreamAsync 将流式更新转换为 Segments
                await foreach (var segments in ChatClientService.ConvertToSegmentsStreamAsync(streamingUpdates).WithCancellation(ct))
                {
                    collector.UpdateSegments(segments);
                }

                // 将收集到的响应格式化为 Markdown
                var markdownResult = collector.ToMarkdown();
                
                _logger.LogInformation(
                    "✅ App Tool '{ToolName}' executed successfully at depth {Depth}. " +
                    "Text length: {TextLength}, Tool calls: {ToolCallCount}",
                    app.Name, currentDepth, collector.GetText().Length, collector.GetToolCalls().Count);

                return markdownResult;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("App Tool '{ToolName}' execution was cancelled", app.Name);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "App Tool '{ToolName}' execution failed: {Message}", app.Name, ex.Message);
                throw new InvalidOperationException($"Tool execution failed: {ex.Message}", ex);
            }
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
            app.Name, paramInfos.Count);

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
        // SourceId 格式: "serverId:toolName"
        var parts = mcpTool.SourceId.Split(':', 2);
        if (parts.Length != 2)
        {
            _logger.LogError("Invalid MCP Tool SourceId format: {SourceId}. Expected format: 'serverId:toolName'", mcpTool.SourceId);
            return null;
        }
        
        var serverId = parts[0];
        var toolName = parts[1]; // 使用 SourceId 中的工具名称，而不是 mcpTool.Name
        var toolDescription = mcpTool.Description ?? $"MCP Tool: {toolName}";
        
        _logger.LogInformation("Creating MCP Tool: {ToolName} from Server: {ServerId}", toolName, serverId);
        
        // ========== 优化 3: 获取 McpClientsFactory（利用其内置缓存） ==========
        var mcpClientsFactory = scope.ServiceProvider.GetRequiredService<McpClientsFactory>();
        
        // 创建执行委托（使用 AIFunctionArguments，与 App Tool 保持一致）
        var executeFunc = async (AIFunctionArguments arguments, CancellationToken ct) =>
        {
            _logger.LogInformation("Executing MCP Tool {ToolName} (Server: {ServerId}) with arguments: {Args}", 
                toolName, serverId, string.Join(", ", arguments.Select(a => $"{a.Key}={a.Value}")));
            
            try
            {
                // 🔑 验证必填参数（如果有 Schema）
                if (!string.IsNullOrWhiteSpace(mcpTool.ParametersSchema))
                {
                    var validationError = _promptParameterService.ValidateParametersFromSchemaDetailed(
                        mcpTool.ParametersSchema,
                        arguments
                    );
                    
                    if (validationError != null)
                    {
                        _logger.LogWarning("MCP Tool {ToolName} validation failed", toolName);
                        return validationError;
                    }
                }
                
                // 获取 MCP Client（McpClientsFactory 内部已有 ConcurrentDictionary 缓存）
                var client = await mcpClientsFactory.GetMcpClientAsync(serverId, ct);
                if (client == null)
                {
                    var errorMsg = $"[MCP Error] Cannot connect to MCP Server '{serverId}'. Server may be offline or not configured.";
                    _logger.LogError(errorMsg);
                    return errorMsg;
                }
                
                // 转换 AIFunctionArguments 为 Dictionary（与 App Tool 相同的处理方式）
                Dictionary<string, object?>? mcpArguments = null;
                if (arguments.Any())
                {
                    mcpArguments = new Dictionary<string, object?>();
                    foreach (var arg in arguments)
                    {
                        if (arg.Value != null)
                        {
                            mcpArguments[arg.Key] = arg.Value;
                        }
                    }
                }
                
                _logger.LogInformation("Calling MCP Tool {ToolName} with arguments: {Args}", 
                    toolName, System.Text.Json.JsonSerializer.Serialize(mcpArguments));
                
                // 调用 MCP Tool（显式转换为 IReadOnlyDictionary，与 McpInspectorService 保持一致）
                var result = await client.CallToolAsync(toolName, mcpArguments as IReadOnlyDictionary<string, object?>);
                
                _logger.LogInformation("MCP Tool {ToolName} returned result type: {ResultType}", 
                    toolName, result?.GetType().Name ?? "null");
                
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
                _logger.LogError(ex, "Failed to execute MCP Tool {ToolName} on Server {ServerId}. Exception Details: {ExceptionDetails}", 
                    toolName, serverId, ex.ToString());
                return errorMsg;
            }
        };

        // ========== 优化 1: 参数 Schema 支持 ==========
        // 先创建基本的 AIFunction（没有参数 Schema）
        var baseFunction = AIFunctionFactory.Create(
            executeFunc,
            name: toolName,
            description: toolDescription
        );
        
        // 如果有参数 Schema，则用自定义包装类覆盖 JsonSchema
        AIFunction aiFunction;
        if (!string.IsNullOrWhiteSpace(mcpTool.ParametersSchema))
        {
            try
            {
                _logger.LogInformation("Tool {ToolName} has parameters schema: {Schema}", 
                    toolName, mcpTool.ParametersSchema);
                
                aiFunction = new McpToolAIFunction(baseFunction, mcpTool.ParametersSchema);
                
                _logger.LogInformation("Created AIFunction with custom schema for MCP Tool: {ToolName}", toolName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse parameters schema for tool {ToolName}, using default function", toolName);
                aiFunction = baseFunction;
            }
        }
        else
        {
            _logger.LogInformation("Tool {ToolName} has no parameters schema", toolName);
            aiFunction = baseFunction;
        }

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