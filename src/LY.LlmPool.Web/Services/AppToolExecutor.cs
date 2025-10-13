using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using Microsoft.Extensions.AI;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using LlmEndpoint = LY.LlmPool.Web.Data.Entities.LlmEndpoint;

namespace LY.LlmPool.Web.Services;

/// <summary>
/// App Tool 执行器 - 用于动态参数的工具执行
/// 这个类允许 AIFunctionFactory 从方法签名推断参数
/// </summary>
internal class AppToolExecutor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly PromptParameterService _promptParameterService;
    private readonly ILogger _logger;
    private readonly LlmPrompt? _toolPrompt;
    private readonly LlmConfig? _llmConfig;
    private readonly LlmEndpoint? _endpoint;

    public AppToolExecutor(
        IServiceProvider serviceProvider,
        PromptParameterService promptParameterService,
        ILogger logger,
        LlmPrompt? toolPrompt,
        LlmConfig? llmConfig,
        LlmEndpoint? endpoint)
    {
        _serviceProvider = serviceProvider;
        _promptParameterService = promptParameterService;
        _logger = logger;
        _toolPrompt = toolPrompt;
        _llmConfig = llmConfig;
        _endpoint = endpoint;
    }

    /// <summary>
    /// 执行 App Tool - 接受 AIFunctionArguments 以支持动态参数
    /// 注意: AIFunctionArguments 参数不会包含在 JSON Schema 中,
    /// 这是 MEAI 的特殊行为 - 它会自动绑定到传入的参数字典
    /// </summary>
    public async Task<string> ExecuteAsync(AIFunctionArguments arguments, CancellationToken cancellationToken = default)
    {
        using var execScope = _serviceProvider.CreateScope();
        var chatClientService = execScope.ServiceProvider.GetRequiredService<IChatClientService>();

        // 构建消息列表
        var messages = new List<AIChatMessage>();

        // 添加系统提示（来自 Prompt）
        if (_toolPrompt != null && !string.IsNullOrWhiteSpace(_toolPrompt.Content))
        {
            // 将 AIFunctionArguments 转换为 Dictionary<string, object>
            var parameters = new Dictionary<string, object>();
            foreach (var arg in arguments)
            {
                if (arg.Value != null)
                {
                    parameters[arg.Key] = arg.Value;
                }
            }
            
            // 使用 PromptParameterService 替换参数
            var promptContent = _promptParameterService.ReplaceParameters(
                _toolPrompt.Content, 
                parameters
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
        
        if (_llmConfig != null)
        {
            response = await chatClientService.SendMessageAsync(
                _llmConfig,
                messages,
                tools: null // Tool 本身不再嵌套调用其他工具
            );
        }
        else if (_endpoint != null)
        {
            // 使用 Endpoint 的第一个可用配置
            var endpointConfig = _endpoint.EndpointConfigs
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
    }
}
