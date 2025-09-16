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

public class ChatClientService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<LoggingHttpHandler> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public ChatClientService(
        IHttpContextAccessor httpContextAccessor,
        ILogger<LoggingHttpHandler> logger,
        IHttpClientFactory httpClientFactory)
    {
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    private string GetCurrentBaseUrl()
    {
        var request = _httpContextAccessor.HttpContext?.Request;
        if (request == null) return string.Empty;

        return $"{request.Scheme}://{request.Host}/v1";
    }

    private Kernel CreateKernel(string apiKey, string baseUrl, string model)
    {
        var handler = new LoggingHttpHandler(_logger);
        handler.InnerHandler = new HttpClientHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(10) };

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

    private Kernel CreateKernelWithTools(string apiKey, string baseUrl, string model, List<Models.Tool>? tools)
    {
        var handler = new LoggingHttpHandler(_logger);
        handler.InnerHandler = new HttpClientHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(10) };

        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(model, apiKey, httpClient: httpClient);

        var kernel = builder.Build();

        // 添加tools作为kernel functions
        if (tools != null && tools.Count > 0)
        {
            var functions = new Dictionary<string, KernelFunction>();

            foreach (var tool in tools)
            {
                // 从InputSchema中提取参数信息
                var parameters = new List<KernelParameterMetadata>();

                if (tool.InputSchema != null && tool.InputSchema.TryGetValue("properties", out var propertiesObj))
                {
                    if (propertiesObj is JsonElement propertiesElement && propertiesElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in propertiesElement.EnumerateObject())
                        {
                            var paramName = property.Name;
                            var paramInfo = property.Value;

                            // 获取参数类型和描述
                            var paramType = typeof(string); // 默认为string类型
                            var paramDescription = "";
                            var isRequired = false;

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
                                paramDescription = descElement.GetString() ?? "";
                            }

                            // 检查是否为必需参数
                            if (tool.InputSchema.TryGetValue("required", out var requiredObj) &&
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

                // 创建函数定义，只用于工具声明，不执行实际逻辑
                var function = KernelFunctionFactory.CreateFromMethod(
                    method: () => $"Tool {tool.Name} would be called here",
                    functionName: tool.Name,
                    description: tool.Description ?? "",
                    parameters: parameters
                );

                functions[tool.Name] = function;
            }

            if (functions.Count > 0)
            {
                kernel.Plugins.AddFromFunctions("DynamicTools", functions.Values);
            }
        }

        return kernel;
    }

    private OpenAIPromptExecutionSettings CreateExecutionSettings(List<Models.Tool>? tools, Dictionary<string, object>? toolChoice)
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

    public async Task<ChatResponse> SendMessageAsync(LlmConfig config, List<ChatMessage> messages)
    {
        _logger.LogInformation("开始发送消息，配置: {Model}@{BaseUrl}, 消息数量: {MessageCount}", 
            config.Model, config.BaseUrl, messages.Count);
        
        try
        {
            // 获取第一个消息的tools配置（假设所有消息共享相同的tools配置）
            var firstMessage = messages.FirstOrDefault();

            // 如果有工具，尝试使用带工具的 kernel，否则使用普通 kernel
            Kernel kernel;
            OpenAIPromptExecutionSettings settings;

            if (firstMessage?.Tools != null && firstMessage.Tools.Count > 0)
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

            var response = new ChatResponse
            {
                Message = result[0].Content ?? string.Empty,
                Status = "success"
            };

            _logger.LogInformation("响应内容长度: {Length}", response.Message.Length);

            // 检查是否有工具调用
            if (result[0].Items != null)
            {
                var toolCalls = new List<ToolCall>();

                foreach (var item in result[0].Items)
                {
                    if (item is Microsoft.SemanticKernel.FunctionCallContent functionCall)
                    {
                        _logger.LogInformation("检测到工具调用: {FunctionName}", functionCall.FunctionName);
                        toolCalls.Add(new ToolCall
                        {
                            Id = functionCall.Id ?? Guid.NewGuid().ToString(),
                            Type = "function",
                            Function = new ToolFunction
                            {
                                Name = functionCall.FunctionName,
                                Arguments = JsonSerializer.Serialize(functionCall.Arguments)
                            }
                        });
                    }
                }

                if (toolCalls.Count > 0)
                {
                    _logger.LogInformation("总工具调用数量: {Count}", toolCalls.Count);
                    response.ToolCalls = toolCalls;
                }
            }

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

    public async Task<ChatResponse> SendMessageAsync(LlmEndpoint config, List<ChatMessage> messages)
    {
        var baseUrl = GetCurrentBaseUrl();
        return await SendMessageAsync(new LlmConfig
        {
            ApiKey = config.Id,
            BaseUrl = baseUrl,
            Model = config.Name
        }, messages);
    }

    public IAsyncEnumerable<string> SendStreamingMessageAsync(LlmConfig config, List<ChatMessage> messages)
    {
        return SendStreamingMessageInternalAsync(config.ApiKey, config.BaseUrl, config.Model, messages);
    }

    public IAsyncEnumerable<string> SendStreamingMessageAsync(LlmEndpoint config, List<ChatMessage> messages)
    {
        var baseUrl = GetCurrentBaseUrl();
        return SendStreamingMessageInternalAsync(config.Id, baseUrl, config.Name, messages);
    }

    private async IAsyncEnumerable<string> SendStreamingMessageInternalAsync(
        string apiKey,
        string baseUrl,
        string model,
        List<ChatMessage> messages)
    {
        _logger.LogInformation("开始发送流式消息，模型: {Model}@{BaseUrl}, 消息数量: {MessageCount}", 
            model, baseUrl, messages.Count);

        Kernel kernel;
        OpenAIPromptExecutionSettings settings;
        
        // 获取第一个消息的tools配置
        var firstMessage = messages.FirstOrDefault();

        if (firstMessage?.Tools != null && firstMessage.Tools.Count > 0)
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
            var httpClient = _httpClientFactory.CreateClient();
            var baseUrl = GetCurrentBaseUrl();
            
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
            httpClient.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);

            var response = await httpClient.PostAsync($"{baseUrl}/v1/messages", httpContent);
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
        var httpClient = _httpClientFactory.CreateClient();
        var baseUrl = GetCurrentBaseUrl();
        
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
        httpClient.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);

        HttpResponseMessage? response = null;
        Stream? stream = null;
        StreamReader? reader = null;
        
        try
        {
            response = await httpClient.PostAsync($"{baseUrl}/v1/messages", httpContent);
            
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