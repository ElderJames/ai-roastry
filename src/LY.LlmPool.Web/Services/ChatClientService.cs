using Microsoft.AspNetCore.Http;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using LY.LlmPool.Web.Services.Agents;
using System.Net.Http.Json;
using System.Text.Json;
using System.ClientModel;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using AIResponse = Microsoft.Extensions.AI.ChatResponse;
using ParameterUtils = LY.LlmPool.Web.Components.ChatHelpers.ParameterUtils;

namespace LY.LlmPool.Web.Services;

public class ChatClientService : IChatClientService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ChatClientService> _logger;
    private readonly ILogger<LoggingHttpHandler> _httpLogger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ChatClientFactory _chatClientFactory;

    public ChatClientService(
        IHttpContextAccessor httpContextAccessor,
        ILogger<ChatClientService> logger,
        IHttpClientFactory httpClientFactory,
        ILogger<LoggingHttpHandler> httpLogger,
        ChatClientFactory chatClientFactory)
    {
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _httpLogger = httpLogger;
        _chatClientFactory = chatClientFactory;
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

    private static ChatHistory BuildChatHistory(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        var chatHistory = new ChatHistory();

        foreach (var message in messages)
        {
            var role = new AuthorRole(message.Role.Value);
            var text = message.Text ?? string.Empty;
            chatHistory.Add(new ChatMessageContent(role, text));
        }

        return chatHistory;
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



    /// <summary>
    /// 创建带有 KernelFunction 工具的 Kernel
    /// </summary>
    private Kernel CreateKernelWithFunctions(string apiKey, string baseUrl, string model, IEnumerable<KernelFunction> functions)
    {
        var httpClient = _httpClientFactory.CreateClient("LlmPoolApi");
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            httpClient.BaseAddress = new Uri(baseUrl);
        }

        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(model, apiKey, httpClient: httpClient);

        var kernel = builder.Build();

        // 将所有 KernelFunction 添加到一个插件中
        if (functions != null && functions.Any())
        {
            kernel.Plugins.AddFromFunctions("PromptTools", functions);
            _logger.LogInformation("Added {Count} KernelFunctions to kernel", functions.Count());
        }

        return kernel;
    }

    private OpenAIPromptExecutionSettings CreateExecutionSettingsForTools(Dictionary<string, object>? toolChoice, Dictionary<string, object>? customParameters = null)
    {
        var settings = new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
        };

        // 应用自定义参数
        ApplyCustomParameters(settings, customParameters);

        return settings;
    }


    private OpenAIPromptExecutionSettings CreateExecutionSettings(List<OpenAITool>? tools, Dictionary<string, object>? toolChoice, Dictionary<string, object>? customParameters = null)
    {
        var settings = new OpenAIPromptExecutionSettings();

        // 应用自定义参数
        ApplyCustomParameters(settings, customParameters);

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


    private void ApplyCustomParameters(OpenAIPromptExecutionSettings settings, Dictionary<string, object>? customParameters)
    {
        ModelParameterHelper.ApplyToExecutionSettings(settings, customParameters);
    }
    
    public async Task<ChatResponse> SendMessageAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
    {
        _logger.LogInformation("开始发送消息，配置: {Model}@{BaseUrl}, 消息数量: {MessageCount}", 
            config.Model, config.BaseUrl, messages.Count);
        
        // 解析配置中的额外参数
        var configParameters = ModelParameterHelper.ParseFromJson(config.AdditionalParameters);
        if (configParameters == null && !string.IsNullOrWhiteSpace(config.AdditionalParameters))
        {
            _logger.LogWarning("解析 LlmConfig.AdditionalParameters 失败: {Json}", config.AdditionalParameters);
        }
        
        try
        {
            // 使用 ChatClientFactory 创建 IChatClient，启用自动工具调用
            var chatClient = _chatClientFactory.CreateClient(config, enableFunctionInvocation: true);

            var chatOptions = new Microsoft.Extensions.AI.ChatOptions();
            
            // 添加工具
            if (tools != null && tools.Any())
            {
                _logger.LogInformation("检测到 {ToolCount} 个工具", tools.Count());
                chatOptions.Tools = new List<Microsoft.Extensions.AI.AITool>(tools);
            }
            else
            {
                _logger.LogInformation("无工具配置");
            }

            _logger.LogInformation("开始调用聊天完成服务");
            AIResponse aiResponse = await chatClient.GetResponseAsync(messages, chatOptions);
            _logger.LogInformation("聊天完成服务调用成功");

            var response = new ChatResponse
            {
                Message = aiResponse.Text ?? string.Empty,
                Status = "success"
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

    public async Task<ChatResponse> SendMessageAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages, Dictionary<string, object>? parameters, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
    {
        _logger.LogInformation("开始发送消息(带参数),配置: {Model}@{BaseUrl}, 消息数量: {MessageCount}", 
            config.Model, config.BaseUrl, messages.Count);
        
        try
        {
            // 使用 ChatClientFactory 创建 IChatClient,启用自动工具调用
            var chatClient = _chatClientFactory.CreateClient(config, enableFunctionInvocation: true);

            var chatOptions = new Microsoft.Extensions.AI.ChatOptions();
            
            // 添加工具
            if (tools != null && tools.Any())
            {
                _logger.LogInformation("检测到 {ToolCount} 个工具", tools.Count());
                chatOptions.Tools = new List<Microsoft.Extensions.AI.AITool>(tools);
            }
            else
            {
                _logger.LogInformation("无工具配置");
            }

            // 应用参数到 ChatOptions (temperature, max_tokens 等)
            if (parameters != null && parameters.Count > 0)
            {
                if (parameters.TryGetValue("temperature", out var temp) || parameters.TryGetValue("temp", out temp))
                {
                    chatOptions.Temperature = Convert.ToSingle(temp);
                    _logger.LogInformation("设置 Temperature: {Temperature}", chatOptions.Temperature);
                }
                
                if (parameters.TryGetValue("max_tokens", out var tokens) || parameters.TryGetValue("tokens", out tokens))
                {
                    chatOptions.MaxOutputTokens = Convert.ToInt32(tokens);
                    _logger.LogInformation("设置 MaxOutputTokens: {MaxTokens}", chatOptions.MaxOutputTokens);
                }
                
                if (parameters.TryGetValue("top_p", out var topP) || parameters.TryGetValue("topp", out topP))
                {
                    chatOptions.TopP = Convert.ToSingle(topP);
                    _logger.LogInformation("设置 TopP: {TopP}", chatOptions.TopP);
                }
            }
            
            _logger.LogInformation("开始调用聊天完成服务");
            AIResponse aiResponse = await chatClient.GetResponseAsync(messages, chatOptions);
            _logger.LogInformation("聊天完成服务调用成功");

            var response = new ChatResponse
            {
                Message = aiResponse.Text ?? string.Empty,
                Status = "success"
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

    public async Task<ChatResponse> SendMessageAsync(LlmEndpoint config, List<Microsoft.Extensions.AI.ChatMessage> messages, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
    {
        return await SendMessageAsync(new LlmConfig
        {
            ApiKey = config.Id,
            BaseUrl = string.Empty,
            Model = config.Name
        }, messages, tools);
    }

    public async Task<ChatResponse> SendMessageAsyncByAppName(string appName, List<Microsoft.Extensions.AI.ChatMessage> messages, Dictionary<string, object>? parameters = null, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
    {
        var config = new LlmConfig 
        { 
            ApiKey = "app-temp-key", 
            BaseUrl = string.Empty, 
            Model = appName 
        };

        // 使用 ChatClientFactory 创建 IChatClient，启用自动工具调用
        var chatClient = _chatClientFactory.CreateClient(config, enableFunctionInvocation: true);

        var chatOptions = new Microsoft.Extensions.AI.ChatOptions();
        
        // 添加工具
        if (tools != null && tools.Any())
        {
            chatOptions.Tools = new List<Microsoft.Extensions.AI.AITool>(tools);
        }

        // 添加参数到消息
        if (parameters != null && parameters.Count > 0)
        {
            var json = JsonSerializer.Serialize(parameters);
            var messagesWithParams = new List<Microsoft.Extensions.AI.ChatMessage>(messages)
            {
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, $"[app_parameters]{json}")
            };
            messages = messagesWithParams;
        }

        try
        {
            AIResponse aiResponse = await chatClient.GetResponseAsync(messages, chatOptions);

            return new ChatResponse
            {
                Message = aiResponse.Text ?? string.Empty,
                Status = "success"
            };
        }
        catch (Exception ex)
        {
            return new ChatResponse { Message = ex.Message, Status = "error" };
        }
    }

    public IAsyncEnumerable<string> SendStreamingMessageAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
    {
        return SendStreamingMessageInternalAsync(config.ApiKey, config.BaseUrl, config.Model, messages, null, tools);
    }

    public IAsyncEnumerable<string> SendStreamingMessageAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages, Dictionary<string, object>? parameters, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
    {
        return SendStreamingMessageInternalAsync(config.ApiKey, config.BaseUrl, config.Model, messages, parameters, tools);
    }

    public IAsyncEnumerable<string> SendStreamingMessageAsync(LlmEndpoint config, List<Microsoft.Extensions.AI.ChatMessage> messages, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
    {
        return SendStreamingMessageInternalAsync(config.Id, string.Empty, config.Name, messages, null, tools);
    }

    public IAsyncEnumerable<string> SendStreamingMessageAsyncByAppName(string appName, List<Microsoft.Extensions.AI.ChatMessage> messages, Dictionary<string, object>? parameters = null, IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
    {
        return SendStreamingMessageInternalAsync("app-temp-key", string.Empty, appName, messages, parameters, tools);
    }

    private async IAsyncEnumerable<string> SendStreamingMessageViaHttpAsync(
        string baseUrl, 
        string model, 
        List<Microsoft.Extensions.AI.ChatMessage> messages, 
        Dictionary<string, object>? parameters,
        IEnumerable<Microsoft.Extensions.AI.AITool>? tools)
    {
        var apiKey = "app-temp-key";

        // 创建临时配置
        var config = new LlmConfig 
        { 
            ApiKey = apiKey, 
            BaseUrl = string.IsNullOrEmpty(baseUrl) ? "http://localhost" : baseUrl, 
            Model = model 
        };

        // 使用 ChatClientFactory 创建 IChatClient，启用自动工具调用
        var chatClient = _chatClientFactory.CreateClient(config, enableFunctionInvocation: true);

        var chatOptions = new Microsoft.Extensions.AI.ChatOptions();
        
        // 添加工具
        if (tools != null && tools.Any())
        {
            chatOptions.Tools = new List<Microsoft.Extensions.AI.AITool>(tools);
        }
        
        // 添加参数到消息
        if (parameters != null && parameters.Count > 0)
        {
            var json = JsonSerializer.Serialize(parameters);
            var messagesWithParams = new List<Microsoft.Extensions.AI.ChatMessage>(messages)
            {
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, $"[app_parameters]{json}")
            };
            messages = messagesWithParams;
        }

        // 直接使用流式 API，FunctionInvokingChatClient 会自动处理工具调用
        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, chatOptions))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }

  

    private async IAsyncEnumerable<string> SendStreamingMessageInternalAsync(
        string apiKey,
        string baseUrl,
        string model,
        List<Microsoft.Extensions.AI.ChatMessage> messages,
        Dictionary<string, object>? parameters,
        IEnumerable<Microsoft.Extensions.AI.AITool>? tools)
    {
        _logger.LogInformation("开始发送流式消息，模型: {Model}@{BaseUrl}, 消息数量: {MessageCount}", 
            model, baseUrl, messages.Count);

        // 如果是App调用（带参数），通过专用方法处理
        if (parameters != null && apiKey == "app-temp-key")
        {
            await foreach (var chunk in SendStreamingMessageViaHttpAsync(baseUrl, model, messages, parameters, tools))
            {
                yield return chunk;
            }
            yield break;
        }

        // 创建配置
        var config = new LlmConfig 
        { 
            ApiKey = apiKey, 
            BaseUrl = string.IsNullOrEmpty(baseUrl) ? "http://localhost" : baseUrl, 
            Model = model 
        };

        // 使用 ChatClientFactory 创建 IChatClient，启用自动工具调用
        var chatClient = _chatClientFactory.CreateClient(config, enableFunctionInvocation: true);

        var chatOptions = new Microsoft.Extensions.AI.ChatOptions();
        
        // 添加工具
        if (tools != null && tools.Any())
        {
            _logger.LogInformation("检测到 {ToolCount} 个工具，启用自动调用", tools.Count());
            chatOptions.Tools = new List<Microsoft.Extensions.AI.AITool>(tools);
        }
        else
        {
            _logger.LogInformation("无工具配置");
        }

        // 应用参数到 ChatOptions (temperature, max_tokens 等)
        ParameterUtils.ApplyParametersToChatOptions(chatOptions, parameters);

        _logger.LogInformation("开始调用流式聊天完成服务");
        
        var totalChunks = 0;
        var totalLength = 0;

        // 直接使用流式 API，FunctionInvokingChatClient 会自动处理工具调用
        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, chatOptions))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                totalChunks++;
                totalLength += update.Text.Length;
                yield return update.Text;
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
    private List<object> ConvertToAnthropicMessages(List<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        var anthropicMessages = new List<object>();
        
        foreach (var message in messages)
        {
            // 跳过 system 消息，Anthropic API 对 system 消息有特殊处理
            if (message.Role.Value == "system") continue;
            
            // 使用 Text 属性获取消息内容
            var text = message.Text ?? string.Empty;
            if (!string.IsNullOrEmpty(text))
            {
                anthropicMessages.Add(new { role = message.Role.Value, content = text });
            }
        }

        return anthropicMessages;
    }

    public async Task<ChatResponse> SendAnthropicMessageAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages)
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

    public async Task<ChatResponse> SendAnthropicMessageAsync(LlmEndpoint config, List<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        return await SendAnthropicMessageAsync(new LlmConfig
        {
            ApiKey = config.Id,
            Model = config.Name
        }, messages);
    }

    public IAsyncEnumerable<string> SendAnthropicStreamingMessageAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        return SendAnthropicStreamingMessageInternalAsync(config, messages);
    }

    public IAsyncEnumerable<string> SendAnthropicStreamingMessageAsync(LlmEndpoint config, List<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        var llmConfig = new LlmConfig
        {
            ApiKey = config.Id,
            Model = config.Name
        };
        return SendAnthropicStreamingMessageInternalAsync(llmConfig, messages);
    }

    private async IAsyncEnumerable<string> SendAnthropicStreamingMessageInternalAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages)
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

    /// <summary>
    /// 发送流式消息并返回详细更新信息(包含文本和工具调用)
    /// </summary>
    public IAsyncEnumerable<ChatStreamingUpdate> SendStreamingMessageWithDetailsAsync(
        LlmConfig config, 
        List<Microsoft.Extensions.AI.ChatMessage> messages, 
        Dictionary<string, object>? parameters = null, 
        IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
    {
        return SendStreamingMessageWithDetailsInternalAsync(
            config.ApiKey, 
            config.BaseUrl, 
            config.Model, 
            messages, 
            parameters, 
            tools);
    }

    /// <summary>
    /// 发送流式消息并返回详细更新信息(Endpoint 版本)
    /// </summary>
    public IAsyncEnumerable<ChatStreamingUpdate> SendStreamingMessageWithDetailsAsync(
        LlmEndpoint config, 
        List<Microsoft.Extensions.AI.ChatMessage> messages, 
        IEnumerable<Microsoft.Extensions.AI.AITool>? tools = null)
    {
        return SendStreamingMessageWithDetailsInternalAsync(
            config.Id, 
            string.Empty, 
            config.Name, 
            messages, 
            null, 
            tools);
    }

    /// <summary>
    /// 内部实现:发送流式消息并返回详细更新信息
    /// </summary>
    private async IAsyncEnumerable<ChatStreamingUpdate> SendStreamingMessageWithDetailsInternalAsync(
        string apiKey,
        string baseUrl,
        string model,
        List<Microsoft.Extensions.AI.ChatMessage> messages,
        Dictionary<string, object>? parameters,
        IEnumerable<Microsoft.Extensions.AI.AITool>? tools)
    {
        _logger.LogInformation("开始发送流式消息(详细模式)，模型: {Model}@{BaseUrl}, 消息数量: {MessageCount}", 
            model, baseUrl, messages.Count);

        // 记录调用前的消息数量
        var initialMessageCount = messages.Count;

        // 创建配置
        var config = new LlmConfig 
        { 
            ApiKey = apiKey, 
            BaseUrl = string.IsNullOrEmpty(baseUrl) ? "http://localhost" : baseUrl, 
            Model = model 
        };

        // 使用 ChatClientFactory 创建 IChatClient，启用自动工具调用
        var chatClient = _chatClientFactory.CreateClient(config, enableFunctionInvocation: true);

        var chatOptions = new Microsoft.Extensions.AI.ChatOptions();
        
        // 添加工具
        if (tools != null && tools.Any())
        {
            _logger.LogInformation("检测到 {ToolCount} 个工具，启用自动调用", tools.Count());
            chatOptions.Tools = new List<Microsoft.Extensions.AI.AITool>(tools);
        }

        // 应用参数到 ChatOptions
        ParameterUtils.ApplyParametersToChatOptions(chatOptions, parameters);

        _logger.LogInformation("开始调用流式聊天完成服务(详细模式)");

        // 用于临时收集当前批次的工具调用信息
        var currentToolCallBatch = new Dictionary<string, ToolCallInfo>();
        var yieldedCallIds = new HashSet<string>(); // 🔑 跟踪已经yield过的工具调用

        // 流式接收响应
        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, chatOptions))
        {
            // 返回文本更新
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return new ChatStreamingUpdate
                {
                    Text = update.Text
                };
            }

            // 检查流式更新中的工具调用内容
            if (update.Contents != null)
            {
                foreach (var content in update.Contents)
                {
                    // 提取工具调用
                    if (content is Microsoft.Extensions.AI.FunctionCallContent functionCall)
                    {
                        var callId = functionCall.CallId ?? $"call_{Guid.NewGuid():N}";
                        
                        // 正确处理 Arguments：直接使用 IDictionary 或序列化为格式化的 JSON
                        string argumentsJson;
                        if (functionCall.Arguments != null)
                        {
                            // 使用格式化选项让 JSON 更易读
                            argumentsJson = JsonSerializer.Serialize(
                                functionCall.Arguments,
                                new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                        }
                        else
                        {
                            argumentsJson = "{}";
                        }
                        
                        var toolCallInfo = new ToolCallInfo
                        {
                            CallId = callId,
                            ToolName = functionCall.Name,
                            ToolType = "function",
                            Arguments = argumentsJson,
                            StartTime = DateTime.UtcNow,
                            IsSuccess = true
                        };
                        
                        currentToolCallBatch[callId] = toolCallInfo;
                        _logger.LogInformation("流式响应中发现工具调用: {ToolName}, CallId: {CallId}, Args: {Args}", 
                            functionCall.Name, callId, argumentsJson);
                    }
                    // 提取工具结果
                    else if (content is Microsoft.Extensions.AI.FunctionResultContent functionResult)
                    {
                        var callId = functionResult.CallId ?? string.Empty;
                        if (currentToolCallBatch.TryGetValue(callId, out var toolCallInfo))
                        {
                            // 记录原始结果对象
                            var resultObj = functionResult.Result;
                            _logger.LogInformation("工具结果对象类型: {Type}, 值: {Value}", 
                                resultObj?.GetType().Name ?? "null", 
                                resultObj?.ToString() ?? "null");
                            
                            toolCallInfo.Result = resultObj?.ToString() ?? string.Empty;
                            toolCallInfo.EndTime = DateTime.UtcNow;
                            toolCallInfo.IsSuccess = functionResult.Exception == null;
                            
                            if (functionResult.Exception != null)
                            {
                                toolCallInfo.ErrorMessage = functionResult.Exception.Message;
                            }
                            
                            _logger.LogInformation("流式响应中发现工具结果: CallId: {CallId}, Success: {Success}, Result: {Result}", 
                                callId, toolCallInfo.IsSuccess, toolCallInfo.Result);
                            
                            // 🔑 立即yield结果更新，让前端能实时看到Result
                            _logger.LogInformation("立即返回工具结果更新: CallId: {CallId}", callId);
                            yield return new ChatStreamingUpdate
                            {
                                ToolCalls = new List<ToolCallInfo> { toolCallInfo },
                                FinishReason = "tool_result" // 新的FinishReason，表示单个工具的结果更新
                            };
                        }
                        else
                        {
                            _logger.LogWarning("收到工具结果但未找到对应的工具调用: CallId: {CallId}", callId);
                        }
                    }
                }
            }

            // 🔑 关键：检查 FinishReason，如果是 tool_calls，检查是否有新的工具调用需要返回
            // 这样可以在文本流中插入工具调用，保持时间顺序
            if (update.FinishReason?.ToString() == "tool_calls" && currentToolCallBatch.Count > 0)
            {
                // 🔑 找出尚未yield的新工具调用
                var newToolCalls = currentToolCallBatch
                    .Where(kv => !yieldedCallIds.Contains(kv.Key))
                    .Select(kv => kv.Value)
                    .ToList();
                
                if (newToolCalls.Any())
                {
                    _logger.LogInformation("检测到 FinishReason=tool_calls，立即返回 {Count} 个新工具调用", 
                        newToolCalls.Count);
                    
                    // ⏱️ 延迟一小段时间，等待可能的 FunctionResultContent
                    await Task.Delay(100);
                    
                    yield return new ChatStreamingUpdate
                    {
                        ToolCalls = newToolCalls,
                        FinishReason = "tool_calls"
                    };
                    
                    // 标记这些工具调用已返回
                    foreach (var toolCall in newToolCalls)
                    {
                        if (!string.IsNullOrEmpty(toolCall.CallId))
                        {
                            yieldedCallIds.Add(toolCall.CallId);
                        }
                    }
                }
            }
        }

        // 流式调用完成后，如果有工具调用，再次返回（包含可能延迟到达的结果）
        _logger.LogInformation("流式响应完成，消息数量从 {Initial} 增加到 {Final}", 
            initialMessageCount, messages.Count);

        if (currentToolCallBatch.Count > 0)
        {
            _logger.LogInformation("流式结束时返回 {Count} 个工具调用记录（包含所有结果）", 
                currentToolCallBatch.Count);
            
            yield return new ChatStreamingUpdate
            {
                ToolCalls = currentToolCallBatch.Values.ToList(),
                FinishReason = "tool_calls_updated"
            };
        }

        _logger.LogInformation("流式响应(详细模式)完成");
    }
    
    /// <summary>
    /// 将流式更新转换为响应片段流（用于统一处理文本和工具调用的时序）
    /// 每次有更新时 yield 当前的完整片段列表
    /// </summary>
    /// <param name="streamingUpdates">流式更新枚举</param>
    /// <returns>响应片段列表的流式更新</returns>
    public static async IAsyncEnumerable<List<ResponseSegment>> ConvertToSegmentsStreamAsync(
        IAsyncEnumerable<ChatStreamingUpdate> streamingUpdates)
    {
        var segments = new List<ResponseSegment>();
        ResponseSegment? currentTextSegment = null;
        ResponseSegment? currentToolCallSegment = null;
        var toolCallsById = new Dictionary<string, ToolCallRecord>();
        
        await foreach (var update in streamingUpdates)
        {
            var hasUpdate = false;
            
            // 处理文本内容
            if (!string.IsNullOrEmpty(update.Text))
            {
                // 如果有活跃的工具调用片段，先封闭它
                if (currentToolCallSegment != null)
                {
                    currentToolCallSegment = null;
                }
                
                // 实时更新或创建文本片段
                if (currentTextSegment == null)
                {
                    currentTextSegment = new ResponseSegment
                    {
                        Type = ResponseSegmentType.Text,
                        Text = update.Text
                    };
                    segments.Add(currentTextSegment);
                }
                else
                {
                    currentTextSegment.Text += update.Text;
                }
                
                hasUpdate = true;
            }
            
            // 处理工具调用记录
            if (update.IsToolCall && update.ToolCalls != null)
            {
                var incomingToolCalls = update.ToolCalls.Select(ToolCallRecord.FromToolCallInfo).ToList();
                
                foreach (var toolCall in incomingToolCalls)
                {
                    if (!string.IsNullOrEmpty(toolCall.CallId))
                    {
                        // 检查是否已经存在这个CallId的工具调用
                        if (toolCallsById.TryGetValue(toolCall.CallId, out var existing))
                        {
                            // 更新现有工具调用
                            existing.Result = toolCall.Result;
                            existing.Success = toolCall.Success;
                            existing.Error = toolCall.Error;
                            existing.EndTime = toolCall.EndTime;
                            
                            // 立即更新片段（tool_result 或 tool_calls_updated）
                            if (update.FinishReason == "tool_result" || update.FinishReason == "tool_calls_updated")
                            {
                                var segment = segments
                                    .FirstOrDefault(s => s.Type == ResponseSegmentType.ToolCalls && 
                                                        s.ToolCalls?.Any(t => t.CallId == toolCall.CallId) == true);
                                if (segment != null)
                                {
                                    var toolInSegment = segment.ToolCalls?.FirstOrDefault(t => t.CallId == toolCall.CallId);
                                    if (toolInSegment != null)
                                    {
                                        toolInSegment.Result = toolCall.Result;
                                        toolInSegment.Success = toolCall.Success;
                                        toolInSegment.Error = toolCall.Error;
                                        toolInSegment.EndTime = toolCall.EndTime;
                                    }
                                }
                                
                                hasUpdate = true;
                            }
                        }
                        else
                        {
                            // 新的工具调用
                            toolCallsById[toolCall.CallId] = toolCall;
                            
                            // 只在首次出现时创建片段 (FinishReason 是 tool_calls)
                            if (update.FinishReason == "tool_calls")
                            {
                                // 封闭文本片段
                                if (currentTextSegment != null)
                                {
                                    currentTextSegment = null;
                                }
                                
                                // 封闭上一个工具片段
                                if (currentToolCallSegment != null)
                                {
                                    currentToolCallSegment = null;
                                }
                                
                                currentToolCallSegment = new ResponseSegment
                                {
                                    Type = ResponseSegmentType.ToolCalls,
                                    ToolCalls = new List<ToolCallRecord> { toolCall }
                                };
                                segments.Add(currentToolCallSegment);
                                
                                hasUpdate = true;
                            }
                        }
                    }
                }
            }
            
            // 🔑 只在有更新时 yield
            if (hasUpdate)
            {
                yield return segments;
            }
        }
        
        // 🔑 流结束时最后 yield 一次（确保最终状态被返回）
        yield return segments;
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
