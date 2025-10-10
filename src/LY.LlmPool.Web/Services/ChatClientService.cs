using Microsoft.AspNetCore.Http;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.ClientModel;
using System.Reflection;
using System.Text;

namespace LY.LlmPool.Web.Services;

public class ChatClientService : IChatClientService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ChatClientService> _logger;
    private readonly ILogger<LoggingHttpHandler> _httpLogger;
    private readonly IHttpClientFactory _httpClientFactory;

    public ChatClientService(
        IHttpContextAccessor httpContextAccessor,
        ILogger<ChatClientService> logger,
        IHttpClientFactory httpClientFactory,
        ILogger<LoggingHttpHandler> httpLogger)
    {
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _httpLogger = httpLogger;
    }

    // HttpClient BaseAddress 来自命名客户端 LlmPoolApi；无需再从 HttpContext 手动拼接

    private Kernel CreateKernel(string apiKey, string baseUrl, string model)
    {
        var httpClient = _httpClientFactory.CreateClient("LlmPoolApi");
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            httpClient.BaseAddress = new Uri(baseUrl);
        }

        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(model, apiKey, httpClient: httpClient);

        return builder.Build();
    }

    private ChatHistory BuildChatHistory(List<ChatMessage> messages)
    {
        var chatHistory = new ChatHistory();

        _logger.LogInformation("开始构建聊天历史，消息数量: {MessageCount}", messages.Count);

        foreach (var message in messages)
        {
            var authorRole = message.Role.ToLower() switch
            {
                "system" => AuthorRole.System,
                "assistant" => AuthorRole.Assistant,
                "user" => AuthorRole.User,
                _ => AuthorRole.User
            };

            _logger.LogInformation("处理消息 - 角色: {Role}, 有ContentItems: {HasContentItems}, Content长度: {ContentLength}", 
                message.Role, message.ContentItems != null && message.ContentItems.Count > 0, message.Content?.Length ?? 0);

            if (message.ContentItems != null && message.ContentItems.Count > 0)
            {
                _logger.LogInformation("添加多模态消息，ContentItems数量: {Count}", message.ContentItems.Count);
                foreach (var item in message.ContentItems)
                {
                    if (item is TextContent textContent)
                    {
                        _logger.LogInformation("- 文本内容: {Length} 字符", textContent.Text?.Length ?? 0);
                    }
                    else if (item is ImageContent imageContent)
                    {
                        _logger.LogInformation("- 图片内容: {MimeType}, {Size} bytes", 
                            imageContent.MimeType, imageContent.Data?.Length ?? 0);
                    }
                    else
                    {
                        _logger.LogInformation("- 未知内容类型: {Type}", item.GetType().Name);
                    }
                }
                chatHistory.AddMessage(authorRole, message.ContentItems);
            }
            else
            {
                _logger.LogInformation("添加文本消息: {Content}", message.Content);
                chatHistory.AddMessage(authorRole, message.Content ?? string.Empty);
            }
        }

        _logger.LogInformation("聊天历史构建完成，总消息数: {Count}", chatHistory.Count);
        return chatHistory;
    }

    private Kernel CreateKernelWithTools(string apiKey, string baseUrl, string model, List<OpenAITool>? tools)
    {
        var httpClient = _httpClientFactory.CreateClient("LlmPoolApi");
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            httpClient.BaseAddress = new Uri(baseUrl);
        }

        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(model, apiKey, httpClient: httpClient);

        var kernel = builder.Build();

        // 添加tools作为kernel functions
        if (tools != null && tools.Count > 0)
        {
            var functions = new Dictionary<string, KernelFunction>();

            foreach (var tool in tools)
            {
                // 保留参数名/类型/必填与描述：根据 OpenAI JSON Schema 提取
                var parameters = new List<KernelParameterMetadata>();
                var schema = tool.Function.Parameters;
                if (schema != null && schema.TryGetValue("properties", out var propertiesObj))
                {
                    if (propertiesObj is JsonElement propertiesElement && propertiesElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in propertiesElement.EnumerateObject())
                        {
                            var paramName = property.Name;
                            var paramInfo = property.Value;

                            var paramType = typeof(string);
                            var paramDescription = string.Empty;
                            var isRequired = false;

                            if (paramInfo.ValueKind == JsonValueKind.Object)
                            {
                                if (paramInfo.TryGetProperty("type", out var typeElement))
                                {
                                    var typeStr = typeElement.GetString();
                                    paramType = typeStr switch
                                    {
                                        "integer" => typeof(int),
                                        "number" => typeof(double),
                                        "boolean" => typeof(bool),
                                        _ => typeof(string)
                                    };
                                }

                                if (paramInfo.TryGetProperty("description", out var descElement))
                                {
                                    paramDescription = descElement.GetString() ?? string.Empty;
                                }
                            }

                            if (schema.TryGetValue("required", out var requiredObj) &&
                                requiredObj is JsonElement requiredElement &&
                                requiredElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var reqItem in requiredElement.EnumerateArray())
                                {
                                    if (reqItem.GetString() == paramName)
                                    {
                                        isRequired = true;
                                        break;
                                    }
                                }
                            }

                            parameters.Add(new KernelParameterMetadata(paramName)
                            {
                                Description = paramDescription,
                                ParameterType = paramType,
                                IsRequired = isRequired
                            });
                        }
                    }
                    else if (propertiesObj is Dictionary<string, object> propertiesDict)
                    {
                        foreach (var kvp in propertiesDict)
                        {
                            parameters.Add(new KernelParameterMetadata(kvp.Key)
                            {
                                Description = $"Parameter {kvp.Key}",
                                ParameterType = typeof(string),
                                IsRequired = false
                            });
                        }
                    }
                }

                // 追加返回值信息（若 schema 中提供 returns/result 自定义字段）
                string description = tool.Function.Description ?? string.Empty;
                if (schema != null &&
                    (schema.TryGetValue("returns", out var returnsObj) || schema.TryGetValue("result", out returnsObj)) &&
                    returnsObj is JsonElement returnsElement && returnsElement.ValueKind == JsonValueKind.Object)
                {
                    string? retType = null;
                    string? retDesc = null;
                    if (returnsElement.TryGetProperty("type", out var rt)) retType = rt.GetString();
                    if (returnsElement.TryGetProperty("description", out var rd)) retDesc = rd.GetString();
                    var suffix = $" Return: {retType ?? "unknown"}{(string.IsNullOrWhiteSpace(retDesc) ? string.Empty : $" - {retDesc}")}";
                    description = string.IsNullOrWhiteSpace(description) ? suffix.Trim() : ($"{description}\n{suffix}");
                }

                // 创建函数定义：用于工具声明（不在此处执行实际逻辑）
                var funcName = string.IsNullOrWhiteSpace(tool.Function.Name) ? "func" : tool.Function.Name;
                var function = KernelFunctionFactory.CreateFromMethod(
                    method: () => $"Tool {tool.Function.Name} would be called here",
                    functionName: funcName,
                    description: description,
                    parameters: parameters
                );

                functions[funcName] = function;
            }

            if (functions.Count > 0)
            {
                kernel.Plugins.AddFromFunctions("DynamicTools", functions.Values);
            }
        }

        return kernel;
    }

    private static string SanitizeFunctionName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "func";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }
        var s = sb.ToString();
        if (!(char.IsLetter(s[0]) || s[0] == '_')) s = "_" + s;
        return s;
    }

    private Kernel CreateKernelWithObjects(string apiKey, string baseUrl, string model, IEnumerable<object> toolObjects)
    {
        var httpClient = _httpClientFactory.CreateClient("LlmPoolApi");
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            httpClient.BaseAddress = new Uri(baseUrl);
        }

        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(model, apiKey, httpClient: httpClient);

        var kernel = builder.Build();

        foreach (var obj in toolObjects)
        {
            if (obj != null)
            {
                kernel.Plugins.AddFromObject(obj, obj.GetType().Name);
            }
        }

        return kernel;
    }

    private OpenAIPromptExecutionSettings CreateExecutionSettings(List<OpenAITool>? tools, Dictionary<string, object>? toolChoice)
    {
        var settings = new OpenAIPromptExecutionSettings();

        // 如果有工具但底层服务不支持 auto tool choice，我们采用保守策略
        if (tools != null && tools.Count > 0)
        {
            // 检查 tool choice 类型来决定是否启用工具
            if (toolChoice != null && toolChoice.TryGetValue("type", out var choiceType))
            {
                switch (choiceType.ToString())
                {
                    case "none":
                        // 明确指定不使用工具，不设置任何工具行为
                        break;
                    case "auto":
                        // 对于 auto，我们尝试启用工具但不强制
                        // 如果底层不支持，至少函数已经注册到 kernel 中
                        settings.ToolCallBehavior = ToolCallBehavior.EnableKernelFunctions;
                        break;
                    case "required":
                        // 对于 required，我们启用工具
                        settings.ToolCallBehavior = ToolCallBehavior.EnableKernelFunctions;
                        break;
                    case "tool":
                        // 对于指定工具，我们启用工具
                        settings.ToolCallBehavior = ToolCallBehavior.EnableKernelFunctions;
                        break;
                    default:
                        // 默认情况，启用工具
                        settings.ToolCallBehavior = ToolCallBehavior.EnableKernelFunctions;
                        break;
                }
            }
            else
            {
                // 如果没有明确的 tool choice，我们默认启用工具
                settings.ToolCallBehavior = ToolCallBehavior.EnableKernelFunctions;
            }
        }

        return settings;
    }

    private OpenAIPromptExecutionSettings CreateExecutionSettingsForObjects(Dictionary<string, object>? toolChoice)
    {
        var settings = new OpenAIPromptExecutionSettings();
        settings.ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions;
        return settings;
    }

    public async Task<ChatResponse> SendMessageAsync(LlmConfig config, List<ChatMessage> messages, IEnumerable<object>? toolObjects = null)
    {
        _logger.LogInformation("开始发送消息，配置: {Model}@{BaseUrl}, 消息数量: {MessageCount}", 
            config.Model, config.BaseUrl, messages.Count);
        
        try
        {
            // 获取第一个消息的tools配置（假设所有消息共享相同的tools配置）
            var firstMessage = messages.FirstOrDefault();

            // 如果有工具，尝试使用带工具的 kernel，否则使用普通 kernel
            var hasLocalTools = toolObjects != null && toolObjects.Any();
            Kernel kernel;
            OpenAIPromptExecutionSettings settings;

            if (hasLocalTools)
            {
                _logger.LogInformation("使用本地 KernelFunction 对象注册工具");
                kernel = CreateKernelWithObjects(config.ApiKey, config.BaseUrl, config.Model, toolObjects!);
                settings = CreateExecutionSettingsForObjects(firstMessage?.ToolChoice);
            }
            else if (firstMessage?.Tools != null && firstMessage.Tools.Count > 0)
            {
                _logger.LogInformation("检测到工具配置，工具数量: {ToolCount}", firstMessage.Tools.Count);
                try
                {
                    kernel = CreateKernelWithTools(config.ApiKey, config.BaseUrl, config.Model, firstMessage.Tools);
                    settings = CreateExecutionSettings(firstMessage.Tools, firstMessage.ToolChoice);
                    _logger.LogInformation("成功创建带工具的 kernel");
                }
                catch (Exception ex) when (ex.Message.Contains("tool choice") || ex.Message.Contains("auto"))
                {
                    _logger.LogWarning("工具调用失败，回退到无工具模式: {Error}", ex.Message);
                    kernel = CreateKernel(config.ApiKey, config.BaseUrl, config.Model);
                    settings = new OpenAIPromptExecutionSettings();
                }
            }
            else
            {
                _logger.LogInformation("无工具配置，使用普通 kernel");
                kernel = CreateKernel(config.ApiKey, config.BaseUrl, config.Model);
                settings = new OpenAIPromptExecutionSettings();
            }

            var chatHistory = BuildChatHistory(messages);
            var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();

            _logger.LogInformation("开始调用聊天完成服务");
            var result = await chatCompletionService.GetChatMessageContentsAsync(chatHistory, settings, kernel);
            _logger.LogInformation("聊天完成服务调用成功，结果数量: {ResultCount}", result.Count);

            var primary = result.FirstOrDefault();
            if (primary == null)
            {
                _logger.LogWarning("模型返回为空响应");
                return new ChatResponse { Message = string.Empty, Status = "success" };
            }

            var response = new ChatResponse
            {
                Message = primary.Content ?? string.Empty,
                Status = "success",
                ToolCalls = ExtractToolCalls(primary)
            };

            _logger.LogInformation("响应内容长度: {Length}", response.Message.Length);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送消息时发生异常: {Message}", ex.Message);
            return new ChatResponse
            {
                Message = ex.Message,
                Status = "error"
            };
        }
    }

    public async Task<ChatResponse> SendMessageAsync(LlmEndpoint config, List<ChatMessage> messages, IEnumerable<object>? toolObjects = null)
    {
        return await SendMessageAsync(new LlmConfig
        {
            ApiKey = config.Id,
            BaseUrl = string.Empty,
            Model = config.Name
        }, messages, toolObjects);
    }

    public async Task<ChatResponse> SendMessageAsyncByAppName(string appName, List<ChatMessage> messages, Dictionary<string, object>? parameters = null, IEnumerable<object>? toolObjects = null)
    {
        var apiKey = "app-temp-key";

        // 选择合适的 Kernel 与设置（支持本地 KernelFunction 对象或 OpenAI 风格 tools）
        var firstMessage = messages.FirstOrDefault();
        Kernel kernel;
        OpenAIPromptExecutionSettings settings;
        if (toolObjects != null && toolObjects.Any())
        {
            kernel = CreateKernelWithObjects(apiKey, string.Empty, appName, toolObjects);
            settings = CreateExecutionSettingsForObjects(firstMessage?.ToolChoice);
        }
        else if (firstMessage?.Tools != null && firstMessage.Tools.Count > 0)
        {
            kernel = CreateKernelWithTools(apiKey, string.Empty, appName, firstMessage.Tools);
            settings = CreateExecutionSettings(firstMessage.Tools, firstMessage.ToolChoice);
        }
        else
        {
            kernel = CreateKernel(apiKey, string.Empty, appName);
            settings = new OpenAIPromptExecutionSettings();
        }

        var chatHistory = BuildChatHistory(messages);
        if (parameters != null && parameters.Count > 0)
        {
            // 将 app 参数作为系统消息注入，便于模型/插件感知
            var json = JsonSerializer.Serialize(parameters);
            chatHistory.AddSystemMessage($"[app_parameters]{json}");
        }

        var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();
        try
        {
            var result = await chatCompletionService.GetChatMessageContentAsync(chatHistory, settings, kernel);

            return new ChatResponse
            {
                Message = result.Content ?? string.Empty,
                Status = "success",
                ToolCalls = ExtractToolCalls(result)
            };
        }
        catch (Exception ex)
        {
            return new ChatResponse { Message = ex.Message, Status = "error" };
        }
    }

    public IAsyncEnumerable<string> SendStreamingMessageAsync(LlmConfig config, List<ChatMessage> messages, IEnumerable<object>? toolObjects = null)
    {
        return SendStreamingMessageInternalAsync(config.ApiKey, config.BaseUrl, config.Model, messages, null, toolObjects);
    }

    public IAsyncEnumerable<string> SendStreamingMessageAsync(LlmEndpoint config, List<ChatMessage> messages, IEnumerable<object>? toolObjects = null)
    {
        return SendStreamingMessageInternalAsync(config.Id, string.Empty, config.Name, messages, null, toolObjects);
    }

    public IAsyncEnumerable<string> SendStreamingMessageAsyncByAppName(string appName, List<ChatMessage> messages, Dictionary<string, object>? parameters = null, IEnumerable<object>? toolObjects = null)
    {
        return SendStreamingMessageInternalAsync("app-temp-key", string.Empty, appName, messages, parameters, toolObjects);
    }

    private async IAsyncEnumerable<string> SendStreamingMessageViaHttpAsync(
        string baseUrl, 
        string model, 
        List<ChatMessage> messages, 
        Dictionary<string, object>? parameters,
        IEnumerable<object>? toolObjects)
    {
        // 尽管方法名保留为 ViaHttp，这里切换为使用 Semantic Kernel 的流式能力
        var apiKey = "app-temp-key";

        var firstMessage = messages.FirstOrDefault();
        Kernel kernel;
        OpenAIPromptExecutionSettings settings;
        if (toolObjects != null && toolObjects.Any())
        {
            kernel = CreateKernelWithObjects(apiKey, string.Empty, model, toolObjects);
            settings = CreateExecutionSettingsForObjects(firstMessage?.ToolChoice);
        }
        else if (firstMessage?.Tools != null && firstMessage.Tools.Count > 0)
        {
            kernel = CreateKernelWithTools(apiKey, string.Empty, model, firstMessage.Tools);
            settings = CreateExecutionSettings(firstMessage.Tools, firstMessage.ToolChoice);
        }
        else
        {
            kernel = CreateKernel(apiKey, string.Empty, model);
            settings = new OpenAIPromptExecutionSettings();
        }

        var chatHistory = BuildChatHistory(messages);
        if (parameters != null && parameters.Count > 0)
        {
            var json = JsonSerializer.Serialize(parameters);
            chatHistory.AddSystemMessage($"[app_parameters]{json}");
        }

        var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();
        IAsyncEnumerable<StreamingChatMessageContent>? streamingResults = null;
        string? error = null;
        try
        {
            streamingResults = chatCompletionService.GetStreamingChatMessageContentsAsync(chatHistory, settings, kernel);
        }
        catch (Exception ex)
        {
            error = $"Error: {ex.Message}";
        }
        if (error != null)
        {
            yield return error;
            yield break;
        }

        if (streamingResults != null)
        {
            await foreach (var update in streamingResults)
            {
                if (!string.IsNullOrEmpty(update.Content))
                {
                    yield return update.Content;
                }
            }
        }
    }

  

    private static object BuildOpenAiToolsFromObjects(IEnumerable<object> toolObjects)
    {
        var list = new List<object>();
        foreach (var obj in toolObjects)
        {
            if (obj == null) continue;
            var type = obj.GetType();
            var methods = type.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            foreach (var m in methods)
            {
                var kf = m.GetCustomAttributes(typeof(KernelFunctionAttribute), inherit: true).FirstOrDefault() as KernelFunctionAttribute;
                if (kf == null) continue;
                var descAttr = m.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: true).FirstOrDefault() as System.ComponentModel.DescriptionAttribute;
                var fnName = string.IsNullOrWhiteSpace(kf.Name) ? m.Name : kf.Name;

                var properties = new Dictionary<string, object>();
                var required = new List<string>();
                foreach (var p in m.GetParameters())
                {
                    var pDesc = p.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: true).FirstOrDefault() as System.ComponentModel.DescriptionAttribute;
                    var pName = p.Name ?? string.Empty;
                    if (string.IsNullOrEmpty(pName)) continue;
                    var typeStr = p.ParameterType == typeof(int) || p.ParameterType == typeof(int?) ? "integer"
                               : p.ParameterType == typeof(double) || p.ParameterType == typeof(double?) || p.ParameterType == typeof(float) || p.ParameterType == typeof(float?) ? "number"
                               : p.ParameterType == typeof(bool) || p.ParameterType == typeof(bool?) ? "boolean"
                               : "string";
                    properties[pName] = new Dictionary<string, object>
                    {
                        ["type"] = typeStr,
                        ["description"] = pDesc?.Description ?? $"Parameter {pName}"
                    };
                    if (!p.HasDefaultValue) required.Add(pName);
                }

                var inputSchema = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = properties
                };
                if (required.Count > 0) inputSchema["required"] = required.ToArray();

                list.Add(new
                {
                    type = "function",
                    function = new
                    {
                        name = fnName,
                        description = descAttr?.Description ?? string.Empty,
                        parameters = inputSchema
                    }
                });
            }
        }
        return list.ToArray();
    }

    private async IAsyncEnumerable<string> SendStreamingMessageInternalAsync(
        string apiKey,
        string baseUrl,
        string model,
        List<ChatMessage> messages,
        Dictionary<string, object>? parameters,
        IEnumerable<object>? toolObjects)
    {
        _logger.LogInformation("开始发送流式消息，模型: {Model}@{BaseUrl}, 消息数量: {MessageCount}", 
            model, baseUrl, messages.Count);

        // 如果是App调用（带参数），通过HTTP请求传递参数
        if (parameters != null && apiKey == "app-temp-key")
        {
            await foreach (var chunk in SendStreamingMessageViaHttpAsync(baseUrl, model, messages, parameters, toolObjects))
            {
                yield return chunk;
            }
            yield break;
        }

        // 原有的kernel逻辑用于非App调用
        Kernel kernel;
        OpenAIPromptExecutionSettings settings;
        
        // 获取第一个消息的tools配置
        var firstMessage = messages.FirstOrDefault();

        if (toolObjects != null && toolObjects.Any())
        {
            _logger.LogInformation("检测到本地工具对象，启用自动调用");
            kernel = CreateKernelWithObjects(apiKey, baseUrl, model, toolObjects);
            settings = CreateExecutionSettingsForObjects(firstMessage?.ToolChoice);
        }
        else if (firstMessage?.Tools != null && firstMessage.Tools.Count > 0)
        {
            _logger.LogInformation("检测到工具配置，工具数量: {ToolCount}", firstMessage.Tools.Count);
            try
            {
                kernel = CreateKernelWithTools(apiKey, baseUrl, model, firstMessage.Tools);
                settings = CreateExecutionSettings(firstMessage.Tools, firstMessage.ToolChoice);
                _logger.LogInformation("成功创建带工具的 kernel");
            }
            catch (Exception ex) when (ex.Message.Contains("tool choice") || ex.Message.Contains("auto"))
            {
                _logger.LogWarning("工具调用失败，回退到无工具模式: {Error}", ex.Message);
                kernel = CreateKernel(apiKey, baseUrl, model);
                settings = new OpenAIPromptExecutionSettings();
            }
        }
        else
        {
            _logger.LogInformation("无工具配置，使用普通 kernel");
            kernel = CreateKernel(apiKey, baseUrl, model);
            settings = new OpenAIPromptExecutionSettings();
        }

        var chatHistory = BuildChatHistory(messages);
        var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();

        _logger.LogInformation("开始调用流式聊天完成服务");
        
        var totalChunks = 0;
        var totalLength = 0;
        
        IAsyncEnumerable<StreamingChatMessageContent> streamingResults;
        
        try
        {
            streamingResults = chatCompletionService.GetStreamingChatMessageContentsAsync(chatHistory, settings, kernel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建流式请求时发生异常: {Message}", ex.Message);
            
            // 如果是 ClientResultException，记录更多详细信息
            if (ex is ClientResultException clientEx)
            {
                _logger.LogError("ClientResultException - 状态码: {Status}", clientEx.Status);
            }
            
            yield break;
        }

        await foreach (var update in streamingResults)
        {
            if (!string.IsNullOrEmpty(update.Content))
            {
                totalChunks++;
                totalLength += update.Content.Length;
                yield return update.Content;
            }
        }
        
        _logger.LogInformation("流式响应完成，总块数: {Chunks}, 总长度: {Length}", totalChunks, totalLength);
    }

    private static List<ToolCall>? ExtractToolCalls(ChatMessageContent? message)
    {
        if (message?.Items == null || message.Items.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<string, ToolCall>(StringComparer.Ordinal);

        foreach (var item in message.Items)
        {
            if (item is FunctionCallContent call)
            {
                var callId = string.IsNullOrEmpty(call.Id) ? $"call_{Guid.NewGuid():N}" : call.Id!;
                var argumentsJson = call.Arguments != null && call.Arguments.Count > 0
                    ? JsonSerializer.Serialize(call.Arguments)
                    : "{}";

                if (string.IsNullOrWhiteSpace(argumentsJson))
                {
                    argumentsJson = "{}";
                }

                map[callId] = new ToolCall
                {
                    Id = callId,
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = call.FunctionName,
                        Arguments = argumentsJson
                    }
                };
            }
        }

        return map.Count > 0 ? map.Values.ToList() : null;
    }

    // Anthropic API 相关方法
    private List<object> ConvertToAnthropicMessages(List<ChatMessage> messages)
    {
        var anthropicMessages = new List<object>();
        
        foreach (var message in messages)
        {
            // 跳过 system 消息，Anthropic API 对 system 消息有特殊处理
            if (message.Role == "system") continue;
            
            if (message.ContentItems != null && message.ContentItems.Count > 0)
            {
                var contentArray = new List<object>();
                
                foreach (var item in message.ContentItems)
                {
                    if (item is TextContent textContent)
                    {
                        contentArray.Add(new { type = "text", text = textContent.Text });
                    }
                    else if (item is ImageContent imageContent)
                    {
                        var base64Data = Convert.ToBase64String(imageContent.Data?.ToArray() ?? Array.Empty<byte>());
                        contentArray.Add(new
                        {
                            type = "image",
                            source = new
                            {
                                type = "base64",
                                media_type = imageContent.MimeType ?? "image/jpeg",
                                data = base64Data
                            }
                        });
                    }
                }
                
                // Anthropic API 要求多模态内容始终使用数组格式
                anthropicMessages.Add(new { role = message.Role, content = contentArray });
            }
            else if (!string.IsNullOrEmpty(message.Content))
            {
                // 纯文本消息使用字符串格式
                anthropicMessages.Add(new { role = message.Role, content = message.Content });
            }
        }

        return anthropicMessages;
    }

    public async Task<ChatResponse> SendAnthropicMessageAsync(LlmConfig config, List<ChatMessage> messages)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient("LlmPoolApi");
            
            var anthropicMessages = ConvertToAnthropicMessages(messages);
            
            var requestBody = new
            {
                model = config.Model,
                max_tokens = 1024,
                messages = anthropicMessages,
                stream = false
            };

            var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower 
            });

            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, "messages") { Content = httpContent };
            request.Headers.TryAddWithoutValidation("x-api-key", config.ApiKey);
            var response = await httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                return new ChatResponse
                {
                    Message = $"API 调用失败: {responseContent}",
                    Status = "error"
                };
            }

            var anthropicResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
            
            if (anthropicResponse.TryGetProperty("content", out var contentArray) && 
                contentArray.ValueKind == JsonValueKind.Array)
            {
                var textContent = "";
                foreach (var content in contentArray.EnumerateArray())
                {
                    if (content.TryGetProperty("text", out var textProp))
                    {
                        textContent += textProp.GetString();
                    }
                }
                
                return new ChatResponse
                {
                    Message = textContent,
                    Status = "success"
                };
            }

            return new ChatResponse
            {
                Message = "未能解析响应内容",
                Status = "error"
            };
        }
        catch (Exception ex)
        {
            return new ChatResponse
            {
                Message = ex.Message,
                Status = "error"
            };
        }
    }

    public async Task<ChatResponse> SendAnthropicMessageAsync(LlmEndpoint config, List<ChatMessage> messages)
    {
        return await SendAnthropicMessageAsync(new LlmConfig
        {
            ApiKey = config.Id,
            Model = config.Name
        }, messages);
    }

    public IAsyncEnumerable<string> SendAnthropicStreamingMessageAsync(LlmConfig config, List<ChatMessage> messages)
    {
        return SendAnthropicStreamingMessageInternalAsync(config, messages);
    }

    public IAsyncEnumerable<string> SendAnthropicStreamingMessageAsync(LlmEndpoint config, List<ChatMessage> messages)
    {
        var llmConfig = new LlmConfig
        {
            ApiKey = config.Id,
            Model = config.Name
        };
        return SendAnthropicStreamingMessageInternalAsync(llmConfig, messages);
    }

    private async IAsyncEnumerable<string> SendAnthropicStreamingMessageInternalAsync(LlmConfig config, List<ChatMessage> messages)
    {
        var httpClient = _httpClientFactory.CreateClient("LlmPoolApi");
        
        var anthropicMessages = ConvertToAnthropicMessages(messages);
        
        var requestBody = new
        {
            model = config.Model,
            max_tokens = 1024,
            messages = anthropicMessages,
            stream = true
        };

        var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions 
        { 
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower 
        });

        var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage? response = null;
        Stream? stream = null;
        StreamReader? reader = null;
        
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "messages") { Content = httpContent };
            request.Headers.TryAddWithoutValidation("x-api-key", config.ApiKey);
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                yield return $"API 调用失败: {errorContent}";
                yield break;
            }

            stream = await response.Content.ReadAsStreamAsync();
            reader = new StreamReader(stream);
            
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.StartsWith("data: ") && !line.Contains("[DONE]"))
                {
                    var jsonData = line.Substring(6); // 移除 "data: " 前缀
                    
                    // 尝试解析 JSON，如果失败就忽略这一行
                    JsonElement eventData;
                    if (TryParseJson(jsonData, out eventData))
                    {
                        if (eventData.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("text", out var text))
                        {
                            var textValue = text.GetString();
                            if (!string.IsNullOrEmpty(textValue))
                            {
                                yield return textValue;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            reader?.Dispose();
            stream?.Dispose();
            response?.Dispose();
            httpClient.Dispose();
        }
    }

    private static bool TryParseJson(string json, out JsonElement element)
    {
        try
        {
            element = JsonSerializer.Deserialize<JsonElement>(json);
            return true;
        }
        catch
        {
            element = default;
            return false;
        }
    }
}

public class ChatResponse
{
    public string Message { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<ToolCall>? ToolCalls { get; set; }
}

public class ToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "function";
    public ToolFunction Function { get; set; } = new();
}

public class ToolFunction
{
    public string Name { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}