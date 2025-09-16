using System.ClientModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using OpenAI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;
using System.Net.Http;

namespace LY.LlmPool.Web.Controllers;

[ApiController]
[Route("v1")]
public class OpenAICompatController : ControllerBase
{
    private readonly LlmPoolService _llmPoolService;
    private readonly ILogger<OpenAICompatController> _logger;
    private readonly ILogger<LoggingHttpHandler> _httpLogger;
    private readonly static JsonSerializerOptions _jsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public OpenAICompatController(
        LlmPoolService llmPoolService,
        ILogger<OpenAICompatController> logger,
        ILogger<LoggingHttpHandler> httpLogger)
    {
        _llmPoolService = llmPoolService;
        _logger = logger;
        _httpLogger = httpLogger;
    }

    private Kernel CreateKernel(LlmConfig config)
    {
        var handler = new LoggingHttpHandler(_httpLogger);
        handler.InnerHandler = new HttpClientHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(config.BaseUrl) };

        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(config.Model, config.ApiKey, httpClient: httpClient);
        
        return builder.Build();
    }

    [HttpPost("chat/completions")]
    public async Task ChatCompletions()
    {
        EndpointCallRecord? callRecord = null;
        var requestStartTime = DateTime.UtcNow;
        object? requestData = null;
        string? requestBody = null;

        try
        {
            // Capture request data
            using (var reader = new StreamReader(Request.Body))
            {
                requestBody = await reader.ReadToEndAsync();
            }

            if (!string.IsNullOrEmpty(requestBody))
            {
                try
                {
                    requestData = JsonSerializer.Deserialize<object>(requestBody, _jsonSerializerOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Error parsing request JSON");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading request data");
        }

        // 从请求头中获取API Key
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader) ||
            string.IsNullOrEmpty(authHeader) ||
            !authHeader.ToString().StartsWith("Bearer "))
        {
            Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            await Response.WriteAsJsonAsync(new { error = "Missing or invalid API key" });
            return;
        }

        var apiKey = authHeader.ToString().Replace("Bearer ", "");

        try
        {
            // Create initial call record
            callRecord = await _llmPoolService.CreateCallRecordAsync(apiKey, requestData);

            var modelAcquireStartTime = DateTime.UtcNow;
            var config = await _llmPoolService.GetAvailableConfigByKeyAsync(apiKey);
            var waitTime = DateTime.UtcNow - modelAcquireStartTime;

            // Update call record with waiting time
            if (callRecord != null)
            {
                callRecord.WaitTime = waitTime;
            }

            if (config == null)
            {
                // Update call record with error
                if (callRecord != null)
                {
                    callRecord.IsSuccessful = false;
                    callRecord.ErrorMessage = "No available model found or invalid API key";
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }

                Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                await Response.WriteAsJsonAsync(new { error = "No available model found or invalid API key" });
                return;
            }

            // Update call record with config info
            if (callRecord != null)
            {
                callRecord.LlmConfigId = config.Id;
                await _llmPoolService.UpdateCallRecordAsync(callRecord);
            }

            try
            {
                // Parse request
                _logger.LogInformation("收到聊天请求，请求体: {RequestBody}", requestBody);
                var chatRequest = JsonSerializer.Deserialize<ChatRequest>(requestBody ?? "{}", _jsonSerializerOptions);
                if (chatRequest == null)
                {
                    throw new InvalidOperationException("Invalid chat request");
                }
                
                _logger.LogInformation("解析的聊天请求 - 模型: {Model}, 消息数量: {MessageCount}", 
                    chatRequest.Model, chatRequest.Messages.Count);

                // Override model if specified in config
                if (!string.IsNullOrEmpty(config.Model))
                {
                    chatRequest.Model = config.Model;
                }

                // Record model call start time
                var modelCallStartTime = DateTime.UtcNow;
                if (callRecord != null)
                {
                    callRecord.ModelCallStartedAt = modelCallStartTime;
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }

                // Set response headers

                if (Request.Headers.Accept.Any(x => x.Contains("text/event-stream")))
                {
                    Response.Headers["Transfer-Encoding"] = "chunked";
                    Response.Headers["Content-Type"] = "text/event-stream";
                }
                else
                {
                    Response.Headers["Content-Type"] = "application/json";
                }

                // Record model response start time
                var modelResponseStartTime = DateTime.UtcNow;
                if (callRecord != null)
                {
                    callRecord.ModelResponseStartedAt = modelResponseStartTime;
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }

                // Create kernel and chat history
                var kernel = CreateKernel(config);
                var chatHistory = new ChatHistory();

                // Add messages to chat history
                _logger.LogInformation("开始处理 {MessageCount} 条消息", chatRequest.Messages.Count);
                foreach (var message in chatRequest.Messages)
                {
                    var role = MapRoleForDeepseek(message.Role);
                    _logger.LogInformation("处理消息 - 角色: {Role}, 内容类型: {ContentType}, 包含图片: {HasImages}", 
                        role, message.ContentElement.ValueKind, message.HasImages);
                    
                    if (message.HasImages && message.ContentElement.ValueKind == JsonValueKind.Array)
                    {
                        // 处理多模态内容
                        _logger.LogInformation("处理多模态消息，内容项数量: {ItemCount}", 
                            message.ContentElement.GetArrayLength());
                        var contentItems = new ChatMessageContentItemCollection();
                        
                        foreach (var item in message.ContentElement.EnumerateArray())
                        {
                            if (item.TryGetProperty("type", out var typeElement))
                            {
                                var type = typeElement.GetString();
                                _logger.LogInformation("处理内容项 - 类型: {Type}", type);
                                
                                if (type == "text" && item.TryGetProperty("text", out var textElement))
                                {
                                    var text = textElement.GetString();
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        contentItems.Add(new Microsoft.SemanticKernel.TextContent(text));
                                        _logger.LogInformation("添加文本内容，长度: {Length}", text.Length);
                                    }
                                }
                                else if ((type == "image" || type == "image_url") && 
                                         TryExtractImageData(item, out var imageData, out var mimeType))
                                {
                                    contentItems.Add(new Microsoft.SemanticKernel.ImageContent(imageData, mimeType));
                                    _logger.LogInformation("添加图片内容 - MIME类型: {MimeType}, 大小: {Size} bytes", 
                                        mimeType, imageData.Length);
                                }
                                else
                                {
                                    _logger.LogWarning("无法处理的内容项类型: {Type}, JSON: {Json}", 
                                        type, item.GetRawText());
                                }
                            }
                        }
                        
                        // 添加多模态消息
                        var authorRole = role.ToLower() switch
                        {
                            "system" => AuthorRole.System,
                            "assistant" => AuthorRole.Assistant,
                            "user" => AuthorRole.User,
                            _ => AuthorRole.User
                        };
                        
                        chatHistory.AddMessage(authorRole, contentItems);
                        _logger.LogInformation("已添加多模态消息 - 角色: {Role}, 内容项数量: {ItemCount}", 
                            authorRole, contentItems.Count);
                    }
                    else
                    {
                        // 处理纯文本内容
                        _logger.LogInformation("处理纯文本消息 - 角色: {Role}, 内容: {Content}", 
                            role, message.Content);
                        switch (role.ToLower())
                        {
                            case "system":
                                chatHistory.AddSystemMessage(message.Content);
                                break;
                            case "assistant":
                                chatHistory.AddAssistantMessage(message.Content);
                                break;
                            case "user":
                            default:
                                chatHistory.AddUserMessage(message.Content);
                                break;
                        }
                    }
                }

                var settings = new OpenAIPromptExecutionSettings 
                { 
                    Temperature = chatRequest.Temperature,
                    MaxTokens = chatRequest.MaxTokens,
                    FrequencyPenalty = chatRequest.FrequencyPenalty,
                    PresencePenalty = chatRequest.PresencePenalty,
                    TopP = chatRequest.TopP
                };

                _logger.LogInformation("开始调用聊天完成服务");
                var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();

                if (chatRequest.Stream ?? false)
                {
                    _logger.LogInformation("使用流式响应模式");
                    var streamingResult = chatCompletionService.GetStreamingChatMessageContentsAsync(
                        chatHistory,
                        settings
                    );

                    await foreach (var update in streamingResult)
                    {
                        var json = JsonSerializer.Serialize(new
                        {
                            id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                            Object = "chat.completion.chunk",
                            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            model = chatRequest.Model,
                            choices = new[]
                            {
                                new
                                {
                                    delta = new
                                    {
                                        role = "assistant",
                                        content = update.Content
                                    },
                                    index = 0,
                                    finish_reason = (string?)null
                                }
                            }
                        }, _jsonSerializerOptions);

                        await Response.WriteAsync($"data: {json}\n\n");
                        await Response.Body.FlushAsync();
                    }

                    await Response.WriteAsync("data: [DONE]\n\n");
                    await Response.Body.FlushAsync();
                }
                else
                {
                    _logger.LogInformation("使用非流式响应模式");
                    var response = await chatCompletionService.GetChatMessageContentAsync(
                        chatHistory,
                        settings
                    );
                    
                    _logger.LogInformation("收到模型响应，内容长度: {Length}", response.Content?.Length ?? 0);

                    var json = JsonSerializer.Serialize(new
                    {
                        id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                        Object = "chat.completion",
                        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        model = chatRequest.Model,
                        choices = new[]
                        {
                            new
                            {
                                message = new
                                {
                                    role = "assistant",
                                    content = response.Content
                                },
                                index = 0,
                                finish_reason = "stop"
                            }
                        },
                        usage = new
                        {
                            prompt_tokens = 0, // Semantic Kernel currently doesn't provide token counts
                            completion_tokens = 0,
                            total_tokens = 0
                        }
                    }, _jsonSerializerOptions);

                    await Response.WriteAsync(json);
                }

                // Record model response end time and success status
                if (callRecord != null)
                {
                    callRecord.ModelResponseEndedAt = DateTime.UtcNow;
                    callRecord.IsSuccessful = true;
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }
            }
            catch (Exception ex)
            {
                // Update call record with error
                if (callRecord != null)
                {
                    callRecord.IsSuccessful = false;
                    callRecord.ErrorMessage = ex.Message;
                    callRecord.ModelResponseEndedAt = DateTime.UtcNow;
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }

                _logger.LogError(ex, "Error processing request");
                Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await Response.WriteAsJsonAsync(new { error = "Internal server error" });
            }
            finally
            {
                if (config != null)
                {
                    _llmPoolService.ReleaseConfig(config.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in chat completions");
            Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await Response.WriteAsJsonAsync(new { error = "Internal server error" });
        }
    }

    private string MapRoleForDeepseek(string role)
    {
        return role.ToLower() switch
        {
            "assistant" => "assistant",
            "user" => "user",
            "system" => "system",
            _ => role
        };
    }

    private class ChatRequest
    {
        public string Model { get; set; } = string.Empty;
        public List<Message> Messages { get; set; } = new();
        public float? Temperature { get; set; }
        public int? MaxTokens { get; set; }
        public float? TopP { get; set; }
        public float? FrequencyPenalty { get; set; }
        public float? PresencePenalty { get; set; }
        public bool? Stream { get; set; }
    }

    private class Message
    {
        public string Role { get; set; } = string.Empty;
        
        private JsonElement _content;
        
        [JsonPropertyName("content")]
        public JsonElement ContentElement
        {
            get => _content;
            set => _content = value;
        }
        
        // 获取文本内容的便捷属性
        [JsonIgnore]
        public string Content
        {
            get
            {
                if (_content.ValueKind == JsonValueKind.String)
                {
                    return _content.GetString() ?? string.Empty;
                }
                else if (_content.ValueKind == JsonValueKind.Array)
                {
                    // 从数组中提取所有文本内容
                    var textContent = "";
                    foreach (var item in _content.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var typeElement) && 
                            typeElement.GetString() == "text" &&
                            item.TryGetProperty("text", out var textElement))
                        {
                            textContent += textElement.GetString();
                        }
                    }
                    return textContent;
                }
                return string.Empty;
            }
            set
            {
                // 设置时创建字符串类型的 JsonElement
                _content = JsonSerializer.SerializeToElement(value);
            }
        }
        
        // 检查是否包含图片
        [JsonIgnore]
        public bool HasImages
        {
            get
            {
                if (_content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in _content.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var typeElement))
                        {
                            var type = typeElement.GetString();
                            if (type == "image" || type == "image_url")
                            {
                                return true;
                            }
                        }
                    }
                }
                return false;
            }
        }
    }

    private static bool TryExtractImageData(JsonElement item, out byte[] imageData, out string mimeType)
    {
        imageData = Array.Empty<byte>();
        mimeType = "image/jpeg";

        try
        {
            // 处理 image_url 格式
            if (item.TryGetProperty("image_url", out var imageUrlElement) &&
                imageUrlElement.TryGetProperty("url", out var urlElement))
            {
                var url = urlElement.GetString();
                if (!string.IsNullOrEmpty(url) && url.StartsWith("data:"))
                {
                    var result = TryParseDataUrl(url, out imageData, out mimeType);
                    if (result)
                    {
                        Console.WriteLine($"成功解析 image_url 格式图片 - MIME: {mimeType}, 大小: {imageData.Length} bytes");
                    }
                    else
                    {
                        Console.WriteLine($"解析 image_url 格式图片失败 - URL: {url.Substring(0, Math.Min(50, url.Length))}...");
                    }
                    return result;
                }
                else
                {
                    Console.WriteLine($"image_url 不是 data URL 格式: {url?.Substring(0, Math.Min(50, url?.Length ?? 0))}...");
                }
            }
            // 处理直接的 image 格式
            else if (item.TryGetProperty("source", out var sourceElement))
            {
                if (sourceElement.TryGetProperty("type", out var typeElement) &&
                    typeElement.GetString() == "base64" &&
                    sourceElement.TryGetProperty("data", out var dataElement))
                {
                    var base64Data = dataElement.GetString();
                    if (!string.IsNullOrEmpty(base64Data))
                    {
                        imageData = Convert.FromBase64String(base64Data);
                        
                        if (sourceElement.TryGetProperty("media_type", out var mediaTypeElement))
                        {
                            mimeType = mediaTypeElement.GetString() ?? "image/jpeg";
                        }
                        
                        Console.WriteLine($"成功解析 source 格式图片 - MIME: {mimeType}, 大小: {imageData.Length} bytes");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine("source 格式图片的 base64 数据为空");
                    }
                }
                else
                {
                    Console.WriteLine($"source 格式不正确 - JSON: {sourceElement.GetRawText()}");
                }
            }
            else
            {
                Console.WriteLine($"未识别的图片格式 - JSON: {item.GetRawText()}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"解析图片数据时发生异常: {ex.Message}");
        }

        return false;
    }

    private static bool TryParseDataUrl(string dataUrl, out byte[] imageData, out string mimeType)
    {
        imageData = Array.Empty<byte>();
        mimeType = "image/jpeg";

        try
        {
            Console.WriteLine($"解析 Data URL，长度: {dataUrl.Length}");
            // data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAA...
            var commaIndex = dataUrl.IndexOf(',');
            if (commaIndex == -1) 
            {
                Console.WriteLine("Data URL 格式错误：找不到逗号分隔符");
                return false;
            }

            var header = dataUrl.Substring(5, commaIndex - 5); // 去掉 "data:" 前缀
            var base64Data = dataUrl.Substring(commaIndex + 1);
            Console.WriteLine($"Header: {header}, Base64 数据长度: {base64Data.Length}");

            // 解析 MIME 类型
            var semicolonIndex = header.IndexOf(';');
            if (semicolonIndex != -1)
            {
                mimeType = header.Substring(0, semicolonIndex);
            }
            else
            {
                mimeType = header;
            }

            imageData = Convert.FromBase64String(base64Data);
            Console.WriteLine($"成功解析 Data URL - MIME: {mimeType}, 图片大小: {imageData.Length} bytes");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"解析 Data URL 时发生异常: {ex.Message}");
            return false;
        }
    }
}