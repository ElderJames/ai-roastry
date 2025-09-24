using System.ClientModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services;
using LY.LlmPool.Web.Models;
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
    private readonly CallRecordService _callRecordService;
    private readonly PromptParameterService _promptParameterService;
    private readonly ILogger<OpenAICompatController> _logger;
    private readonly ILogger<LoggingHttpHandler> _httpLogger;
    private readonly static JsonSerializerOptions _jsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public OpenAICompatController(
        LlmPoolService llmPoolService,
        CallRecordService callRecordService,
        PromptParameterService promptParameterService,
        ILogger<OpenAICompatController> logger,
        ILogger<LoggingHttpHandler> httpLogger)
    {
        _llmPoolService = llmPoolService;
        _callRecordService = callRecordService;
        _promptParameterService = promptParameterService;
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

    private Kernel CreateKernelWithTools(LlmConfig config, List<Tool>? tools)
    {
        var handler = new LoggingHttpHandler(_httpLogger);
        handler.InnerHandler = new HttpClientHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(config.BaseUrl) };

        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(config.Model, config.ApiKey, httpClient: httpClient);

        var kernel = builder.Build();
        // 注册内置工具
        try { kernel.Plugins.AddFromObject(new BuiltinTools(), "BuiltinTools"); } catch { }

        if (tools != null && tools.Count > 0)
        {
            var functions = new Dictionary<string, KernelFunction>();

            foreach (var tool in tools)
            {
                var parameters = new List<KernelParameterMetadata>();

                if (tool.InputSchema != null && tool.InputSchema.TryGetValue("properties", out var propertiesObj))
                {
                    if (propertiesObj is JsonElement propertiesElement && propertiesElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in propertiesElement.EnumerateObject())
                        {
                            var paramName = property.Name;
                            var paramInfo = property.Value;

                            var paramType = typeof(string);
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

                            if (tool.InputSchema.TryGetValue("required", out var requiredObj) &&
                                requiredObj is JsonElement requiredElement &&
                                requiredElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var reqItem in requiredElement.EnumerateArray())
                                {
                                    if (reqItem.GetString() == paramName) { isRequired = true; break; }
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
                }

                // 若是内置工具，跳过动态桩实现，交由内置插件实现
                var builtinNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "echo", "get_time", "sum" };
                if (!builtinNames.Contains(tool.Name))
                {
                    var function = KernelFunctionFactory.CreateFromMethod(
                        method: () => $"Tool {tool.Name} would be called here",
                        functionName: tool.Name,
                        description: tool.Description ?? "",
                        parameters: parameters
                    );
                    functions[tool.Name] = function;
                }
            }

            if (functions.Count > 0)
            {
                kernel.Plugins.AddFromFunctions("DynamicTools", functions.Values);
            }
        }

        return kernel;
    }

    private static OpenAIPromptExecutionSettings CreateExecutionSettings(
        float? temperature,
        int? maxTokens,
        float? topP,
        float? frequencyPenalty,
        float? presencePenalty,
        List<Tool>? tools,
        Dictionary<string, object>? toolChoice)
    {
        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = temperature,
            MaxTokens = maxTokens,
            TopP = topP,
            FrequencyPenalty = frequencyPenalty,
            PresencePenalty = presencePenalty
        };

        if (tools != null && tools.Count > 0)
        {
            if (toolChoice != null && toolChoice.TryGetValue("type", out var choiceType))
            {
                switch (choiceType?.ToString())
                {
                    case "none":
                        break;
                    case "auto":
                        settings.ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions;
                        break;
                    case "required":
                    case "tool":
                    default:
                        settings.ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions;
                        break;
                }
            }
            else
            {
                settings.ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions;
            }
        }

        return settings;
    }

    [HttpPost("chat/completions")]
    public async Task ChatCompletions()
    {
        await ProcessChatCompletions();
    }

    private async Task ProcessChatCompletions()
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
            // Parse request first to get the model field
            _logger.LogInformation("收到聊天请求，请求体: {RequestBody}", requestBody);
            var chatRequest = JsonSerializer.Deserialize<ChatRequest>(requestBody ?? "{}", _jsonSerializerOptions);
            if (chatRequest == null)
            {
                throw new InvalidOperationException("Invalid chat request");
            }

            _logger.LogInformation("解析的聊天请求 - 模型: {Model}, 消息数量: {MessageCount}",
                chatRequest.Model, chatRequest.Messages.Count);

            var modelAcquireStartTime = DateTime.UtcNow;
            LlmConfig? config = null;
            LlmApp? app = null;
            string? promptContent = null;
            string? actualModelName = chatRequest.Model;
            string? actualEndpointId = null; // 用于存储实际的EndpointId
            string selectionStrategy = string.Empty; // 记录模型选择策略

            // Try to get app by model name first
            if (!string.IsNullOrEmpty(chatRequest.Model))
            {
                app = await _llmPoolService.GetAppByNameAsync(chatRequest.Model);
            }

            // If app is found, get configuration from app
            if (app != null)
            {
                _logger.LogInformation("找到应用: {AppName}, 类型: {AppType}", app.Name, app.AppType);

                // Get configuration from app
                if (!string.IsNullOrEmpty(app.LlmConfigId))
                {
                    var appConfig = await _llmPoolService.GetConfigByIdAsync(app.LlmConfigId);
                    if (appConfig != null && appConfig.IsEnabled)
                    {
                        config = appConfig;
                        actualModelName = appConfig.Model; // Use the actual model name
                        // 尝试占用该配置，避免并发冲突
                        var acquired = await _llmPoolService.AcquireConfigIfAvailableAsync(appConfig.Id!);
                        if (!acquired)
                        {
                            Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                            await Response.WriteAsJsonAsync(new { error = "All model configurations are busy. Please retry later." });
                            return;
                        }
                        selectionStrategy = "app-config";
                        var epForCfg = await _llmPoolService.GetEndpointForConfigAsync(appConfig.Id!);
                        if (epForCfg != null)
                        {
                            actualEndpointId = epForCfg.Id;
                            callRecord = await _callRecordService.CreateAsync(epForCfg.Id, requestData);
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(app.EndpointId))
                {
                    config = await _llmPoolService.GetAvailableConfigByKeyAsync(app.EndpointId);
                    if (config != null)
                    {
                        actualModelName = config.Model;
                        actualEndpointId = app.EndpointId; // Use the app's endpoint ID
                        selectionStrategy = "app-endpoint-lb";
                        callRecord = await _callRecordService.CreateAsync(app.EndpointId, requestData);
                    }
                }

                // Get prompt content if available
                if (!string.IsNullOrEmpty(app.PromptId))
                {
                    var prompt = await _llmPoolService.GetPromptByIdAsync(app.PromptId);
                    if (prompt != null)
                    {
                        promptContent = prompt.Content;
                    }
                }

                if (config == null)
                {
                    Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    await Response.WriteAsJsonAsync(new { error = $"App '{app.Name}' has no valid configuration" });
                    return;
                }
            }
            else
            {
                // Non-app requests: resolve by names with call record + load balancing
                // 1) Try resolve by config name directly, then find an endpoint containing this config
                var cfgByName = await _llmPoolService.GetConfigByNameAsync(chatRequest.Model);
                if (cfgByName != null)
                {
                    var epForCfg = await _llmPoolService.GetEndpointForConfigAsync(cfgByName.Id!);
                    if (epForCfg == null)
                    {
                        Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        await Response.WriteAsJsonAsync(new { error = $"No endpoint contains config '{cfgByName.Name}'" });
                        return;
                    }

                    // Try to acquire this specific config (non-blocking) to honor availability
                    var acquired = await _llmPoolService.AcquireConfigIfAvailableAsync(cfgByName.Id!);
                    if (!acquired)
                    {
                        Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                        await Response.WriteAsJsonAsync(new { error = "All model configurations are busy. Please retry later." });
                        return;
                    }

                    config = cfgByName;
                    actualModelName = cfgByName.Model;
                    actualEndpointId = epForCfg.Id; // ensure call record is tied to an endpoint
                    selectionStrategy = "config-name-direct-acquire";

                    // Create call record for the selected endpoint
                    callRecord = await _callRecordService.CreateAsync(epForCfg.Id, requestData);
                }
                else
                {
                    // 2) Try resolve by endpoint name (load balance under the endpoint)
                    var epByName = await _llmPoolService.GetEndpointByNameAsync(chatRequest.Model);
                    if (epByName != null)
                    {
                        config = await _llmPoolService.GetAvailableConfigByKeyAsync(epByName.Id);
                        actualEndpointId = epByName.Id;
                        if (config != null)
                        {
                            // Create call record for endpoint-name path
                            callRecord = await _callRecordService.CreateAsync(epByName.Id, requestData);
                            selectionStrategy = "endpoint-name-lb";
                        }
                        if (config != null)
                        {
                            actualModelName = config.Model;
                        }
                    }
                    else
                    {
                        // 3) Backward compatibility: treat model as endpoint id
                        config = await _llmPoolService.GetAvailableConfigByKeyAsync(chatRequest.Model);
                        actualEndpointId = chatRequest.Model;
                        if (config != null)
                        {
                            // Create call record for endpoint-id path
                            callRecord = await _callRecordService.CreateAsync(chatRequest.Model, requestData);
                            selectionStrategy = "endpoint-id-lb";
                        }
                    }
                }
            }

            // Call record is created above in each resolution path where endpoint is known

            var waitTime = DateTime.UtcNow - modelAcquireStartTime;

            // Update call record with waiting time
            if (callRecord != null)
            {
                await _callRecordService.UpdateWaitAsync(callRecord, waitTime);
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

                _logger.LogError("No available model found or invalid API key");
                await Response.WriteAsJsonAsync(new { error = "No available model found or invalid API key" });
                return;
            }

            // Update call record with config info
            if (callRecord != null)
            {
                await _callRecordService.UpdateConfigAsync(callRecord, config.Id);
            }

            try
            {

                // Override model if specified in config (use actual model name)
                if (!string.IsNullOrEmpty(actualModelName))
                {
                    chatRequest.Model = actualModelName;
                }

                // Record model call start time
                var modelCallStartTime = DateTime.UtcNow;
                if (callRecord != null)
                {
                    await _callRecordService.MarkStartAsync(callRecord);
                }

                // Set response headers
                if (Request.Headers.Accept.Any(x => x != null && x.Contains("text/event-stream")))
                {
                    Response.Headers["Transfer-Encoding"] = "chunked";
                    Response.Headers["Content-Type"] = "text/event-stream; charset=utf-8";
                    Response.Headers["Cache-Control"] = "no-cache";
                    Response.Headers["Connection"] = "keep-alive";
                    Response.Headers["X-Accel-Buffering"] = "no"; // for Nginx to disable buffering
                }
                else
                {
                    Response.Headers["Content-Type"] = "application/json; charset=utf-8";
                }

                // Record model response start time
                var modelResponseStartTime = DateTime.UtcNow;
                if (callRecord != null)
                {
                    await _callRecordService.MarkResponseStartAsync(callRecord);
                }

                // Create kernel and chat history
                // 根据请求中的 tools / tool_choice 创建 kernel 与 settings
                List<Tool>? requestTools = null;
                if (chatRequest.Tools != null && chatRequest.Tools.Count > 0)
                {
                    requestTools = ConvertOpenAITools(chatRequest.Tools);
                }

                var kernel = (requestTools != null && requestTools.Count > 0)
                    ? CreateKernelWithTools(config, requestTools)
                    : CreateKernel(config);
                var chatHistory = new ChatHistory();

                // Add app prompt as system message if available
                if (!string.IsNullOrEmpty(promptContent))
                {
                    // Process parameter replacement if app has parameters
                    if (app != null && chatRequest.Parameters != null && chatRequest.Parameters.Count > 0)
                    {
                        // Validate required parameters
                        var missingParams = _promptParameterService.ValidateParameters(promptContent, chatRequest.Parameters);
                        if (missingParams.Count > 0)
                        {
                            if (callRecord != null)
                            {
                                callRecord.IsSuccessful = false;
                                callRecord.ErrorMessage = $"Missing required parameters: {string.Join(", ", missingParams)}";
                                await _llmPoolService.UpdateCallRecordAsync(callRecord);
                            }

                            Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await Response.WriteAsJsonAsync(new
                            {
                                error = $"Missing required parameters: {string.Join(", ", missingParams)}",
                                required_parameters = missingParams
                            });
                            return;
                        }

                        // Replace parameters in prompt content
                        var originalPromptContent = promptContent;
                        promptContent = _promptParameterService.ReplaceParameters(promptContent, chatRequest.Parameters);

                        _logger.LogInformation("参数替换完成 - 原始长度: {OriginalLength}, 替换后长度: {NewLength}, 参数数量: {ParamCount}",
                            originalPromptContent.Length, promptContent.Length, chatRequest.Parameters.Count);
                    }

                    chatHistory.AddSystemMessage(promptContent);
                    _logger.LogInformation("添加应用Prompt作为系统消息，长度: {Length}", promptContent.Length);
                }

                // Add messages to chat history
                _logger.LogInformation("开始处理 {MessageCount} 条消息", chatRequest.Messages.Count);
                foreach (var message in chatRequest.Messages)
                {
                    var role = MapRoleForDeepseek(message.Role);
                    _logger.LogInformation("处理消息 - 角色: {Role}, 内容类型: {ContentType}, 包含图片: {HasImages}",
                        role, message.ContentElement.ValueKind, message.HasImages);

                    if (message.ContentElement.ValueKind == JsonValueKind.Array)
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
                                // 兼容 SK 的 input_text
                                if ((type == "text" || type == "input_text") && item.TryGetProperty("text", out var textElement))
                                {
                                    var text = textElement.GetString();
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        contentItems.Add(new Microsoft.SemanticKernel.TextContent(text));
                                        _logger.LogInformation("添加文本内容，长度: {Length}", text.Length);
                                    }
                                }
                                // 兼容 SK 的 input_image（按 image_url/data url 或 source/base64 解析）
                                else if ((type == "image" || type == "image_url" || type == "input_image") &&
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

                var settings = CreateExecutionSettings(
                    chatRequest.Temperature,
                    chatRequest.MaxTokens,
                    chatRequest.TopP,
                    chatRequest.FrequencyPenalty,
                    chatRequest.PresencePenalty,
                    requestTools,
                    chatRequest.ToolChoice);

                _logger.LogInformation("开始调用聊天完成服务");
                var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();

                if (chatRequest.Stream ?? false)
                {
                    _logger.LogInformation("使用流式响应模式");
                    var streamingResult = chatCompletionService.GetStreamingChatMessageContentsAsync(
                        chatHistory,
                        settings,
                        kernel
                    );

                    // 聚合工具调用（若有），在流结束时一次性返回（包含参数与结果）
                    var toolCallsAgg = new Dictionary<string, (string Id, string Name, StringBuilder Args, StringBuilder Result)>();

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

                        // 收集工具调用（如果更新中包含）：参数与结果分别聚合
                        if (update.Items != null)
                        {
                            foreach (var item in update.Items)
                            {
                                if (TryExtractFunctionCall(item, out var id, out var name, out var args))
                                {
                                    if (!toolCallsAgg.TryGetValue(id, out var entry))
                                    {
                                        entry = (id, name, new StringBuilder(), new StringBuilder());
                                    }
                                    entry.Args.Append(args);
                                    toolCallsAgg[id] = entry;
                                }
                                else if (TryExtractFunctionResult(item, out var rid, out var rname, out var result))
                                {
                                    if (!toolCallsAgg.TryGetValue(rid, out var entry))
                                    {
                                        entry = (rid, rname, new StringBuilder(), new StringBuilder());
                                    }
                                    entry.Result.Append(result);
                                    toolCallsAgg[rid] = entry;
                                }
                            }
                        }
                    }

                    // 若存在工具调用，发送一个包含 tool_calls 的增量块
                    if (toolCallsAgg.Count > 0)
                    {
                        var toolCalls = toolCallsAgg.Values.Select(v => new
                        {
                            id = v.Id,
                            type = "function",
                            function = new { name = v.Name, arguments = v.Args.ToString(), result = v.Result.ToString() }
                        }).ToArray();

                        var toolsJson = JsonSerializer.Serialize(new
                        {
                            id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                            Object = "chat.completion.chunk",
                            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            model = chatRequest.Model,
                            choices = new[]
                            {
                                new
                                {
                                    delta = new { tool_calls = toolCalls },
                                    index = 0,
                                    finish_reason = (string?)null
                                }
                            }
                        }, _jsonSerializerOptions);

                        await Response.WriteAsync($"data: {toolsJson}\n\n");
                        await Response.Body.FlushAsync();

                        // 将工具调用详情写入调用记录
                        if (callRecord != null)
                        {
                            await _callRecordService.WriteSelectionAsync(
                                callRecord,
                                selectionStrategy,
                                actualEndpointId,
                                config.Id,
                                config.Name,
                                app?.Name,
                                chatRequest.Model,
                                message: null,
                                toolCalls: toolCalls,
                                requestReceivedAt: requestStartTime);
                        }
                    }

                    await Response.WriteAsync("data: [DONE]\n\n");
                    await Response.Body.FlushAsync();
                }
                else
                {
                    _logger.LogInformation("使用非流式响应模式");
                    var response = await chatCompletionService.GetChatMessageContentAsync(
                        chatHistory,
                        settings,
                        kernel
                    );

                    _logger.LogInformation("收到模型响应，内容长度: {Length}", response.Content?.Length ?? 0);

                    // 解析工具调用（如有）
                    object? toolCallsObj = null;
                    object[]? toolCallsDetailed = null;
                    if (response.Items != null)
                    {
                        var callMap = new Dictionary<string, (string Name, string Args)>();
                        var resultMap = new Dictionary<string, string>();
                        foreach (var item in response.Items)
                        {
                            if (TryExtractFunctionCall(item, out var id, out var name, out var args))
                            {
                                callMap[id] = (name, args);
                            }
                            else if (TryExtractFunctionResult(item, out var rid, out var rname, out var result))
                            {
                                resultMap[rid] = result;
                            }
                        }

                        if (callMap.Count > 0)
                        {
                            toolCallsDetailed = callMap.Select(kvp => new
                            {
                                id = kvp.Key,
                                type = "function",
                                function = new
                                {
                                    name = kvp.Value.Name,
                                    arguments = kvp.Value.Args,
                                    result = resultMap.TryGetValue(kvp.Key, out var res) ? res : null
                                }
                            }).Cast<object>().ToArray();

                            toolCallsObj = toolCallsDetailed;
                        }
                    }

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
                                    content = response.Content,
                                    tool_calls = toolCallsObj
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

                    // 写入调用记录的响应数据（工具调用详情与最终消息）
                    if (callRecord != null)
                    {
                        await _callRecordService.WriteSelectionAsync(
                            callRecord,
                            selectionStrategy,
                            actualEndpointId,
                            config.Id,
                            config.Name,
                            app?.Name,
                            chatRequest.Model,
                            message: response.Content,
                            toolCalls: toolCallsDetailed,
                            requestReceivedAt: requestStartTime);
                    }
                }

                // Record model response end time and success status
                if (callRecord != null)
                {
                    await _callRecordService.FinalizeAsync(
                        callRecord,
                        selectionStrategy,
                        actualEndpointId,
                        config.Id,
                        config.Name,
                        app?.Name,
                        chatRequest.Model,
                        requestStartTime);
                }
            }
            catch (Exception ex)
            {
                // Update call record with error
                if (callRecord != null)
                {
                    await _callRecordService.MarkErrorAsync(callRecord, ex.Message);
                }

                _logger.LogError(ex, "Error processing request");
                Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await Response.WriteAsJsonAsync(new { error = "Internal server error" });
            }
            finally
            {
                if (config != null && !string.IsNullOrEmpty(config.Id))
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

        // 添加参数支持
        [JsonPropertyName("parameters")]
        public Dictionary<string, object>? Parameters { get; set; }

        // OpenAI tools 支持
        [JsonPropertyName("tools")]
        public List<OpenAITool>? Tools { get; set; }

        // OpenAI tool_choice 支持
        [JsonPropertyName("tool_choice")]
        public Dictionary<string, object>? ToolChoice { get; set; }
    }

    private class OpenAITool
    {
        public string Type { get; set; } = "function";
        public OpenAIFunction Function { get; set; } = new();
    }

    private class OpenAIFunction
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public Dictionary<string, object>? Parameters { get; set; }
    }

    private static List<Tool> ConvertOpenAITools(List<OpenAITool> tools)
    {
        var result = new List<Tool>();
        foreach (var t in tools)
        {
            if (!string.Equals(t.Type, "function", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new Tool
            {
                Name = t.Function.Name,
                Description = t.Function.Description,
                InputSchema = t.Function.Parameters ?? new Dictionary<string, object>()
            });
        }
        return result;
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
            // 1) image_url: 可能是对象 { url: "data:..." } 或字符串 "data:..."
            if (item.TryGetProperty("image_url", out var imageUrlElement))
            {
                string? url = null;
                if (imageUrlElement.ValueKind == JsonValueKind.Object && imageUrlElement.TryGetProperty("url", out var urlElement))
                {
                    url = urlElement.GetString();
                }
                else if (imageUrlElement.ValueKind == JsonValueKind.String)
                {
                    url = imageUrlElement.GetString();
                }

                if (!string.IsNullOrEmpty(url) && url.StartsWith("data:"))
                {
                    return TryParseDataUrl(url, out imageData, out mimeType);
                }
            }

            // 2) source/base64: { source: { type: "base64", data: "...", media_type?: "image/png" } }
            if (item.TryGetProperty("source", out var sourceElement))
            {
                if (sourceElement.TryGetProperty("type", out var typeElement) &&
                    string.Equals(typeElement.GetString(), "base64", StringComparison.OrdinalIgnoreCase) &&
                    sourceElement.TryGetProperty("data", out var dataElement))
                {
                    var base64Data = dataElement.GetString();
                    if (!string.IsNullOrEmpty(base64Data))
                    {
                        imageData = Convert.FromBase64String(base64Data);
                        if (sourceElement.TryGetProperty("media_type", out var mediaTypeElement))
                        {
                            mimeType = mediaTypeElement.GetString() ?? mimeType;
                        }
                        return true;
                    }
                }
            }

            // 3) data_url: { data_url: "data:..." }
            if (item.TryGetProperty("data_url", out var dataUrlEl))
            {
                var url = dataUrlEl.GetString();
                if (!string.IsNullOrEmpty(url) && url.StartsWith("data:"))
                {
                    return TryParseDataUrl(url, out imageData, out mimeType);
                }
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
            if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:"))
            {
                return false;
            }

            var commaIndex = dataUrl.IndexOf(',');
            if (commaIndex == -1)
            {
                Console.WriteLine("Data URL 格式错误：找不到逗号分隔符");
                return false;
            }

            var header = dataUrl.Substring(5, commaIndex - 5); // 去掉 "data:" 前缀
            var base64Data = dataUrl.Substring(commaIndex + 1);

            var semicolonIndex = header.IndexOf(';');
            if (semicolonIndex != -1)
            {
                mimeType = header.Substring(0, semicolonIndex);
            }
            else if (!string.IsNullOrWhiteSpace(header))
            {
                mimeType = header;
            }

            imageData = Convert.FromBase64String(base64Data);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"解析 Data URL 时发生异常: {ex.Message}");
            return false;
        }
    }

    // 从不同 SK 版本的内容项中提取函数调用信息（通过反射以增强兼容性）
    private static bool TryExtractFunctionCall(object item, out string id, out string name, out string arguments)
    {
        id = $"call_{Guid.NewGuid():N}";
        name = string.Empty;
        arguments = string.Empty;

        if (item == null) return false;

        try
        {
            var t = item.GetType();
            // 常见类型：Microsoft.SemanticKernel.FunctionCallContent 或 StreamingFunctionCallUpdate 等
            // 尝试读取常见属性
            var fnNameProp = t.GetProperty("FunctionName") ?? t.GetProperty("Name");
            var idProp = t.GetProperty("Id");
            var argsProp = t.GetProperty("Arguments");

            var fnNameVal = fnNameProp?.GetValue(item)?.ToString();
            if (string.IsNullOrEmpty(fnNameVal)) return false;
            name = fnNameVal;

            var idVal = idProp?.GetValue(item)?.ToString();
            if (!string.IsNullOrEmpty(idVal)) id = idVal;

            var argsVal = argsProp?.GetValue(item);
            if (argsVal is string s)
            {
                arguments = s;
            }
            else if (argsVal is System.Text.Json.Nodes.JsonNode node)
            {
                arguments = node.ToJsonString();
            }
            else if (argsVal != null)
            {
                arguments = JsonSerializer.Serialize(argsVal);
            }
            else
            {
                arguments = "{}";
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // 从不同 SK 版本的内容项中提取函数调用结果（通过反射以增强兼容性）
    private static bool TryExtractFunctionResult(object item, out string id, out string name, out string result)
    {
        id = $"call_{Guid.NewGuid():N}";
        name = string.Empty;
        result = string.Empty;

        if (item == null) return false;

        try
        {
            var t = item.GetType();
            // 常见类型：Microsoft.SemanticKernel.FunctionResultContent 或 StreamingFunctionResultUpdate 等
            var fnNameProp = t.GetProperty("FunctionName") ?? t.GetProperty("Name");
            var idProp = t.GetProperty("Id");
            var resultProp = t.GetProperty("Result") ?? t.GetProperty("Content") ?? t.GetProperty("Text");

            var fnNameVal = fnNameProp?.GetValue(item)?.ToString();
            if (string.IsNullOrEmpty(fnNameVal)) return false;
            name = fnNameVal;

            var idVal = idProp?.GetValue(item)?.ToString();
            if (!string.IsNullOrEmpty(idVal)) id = idVal;

            var resVal = resultProp?.GetValue(item);
            if (resVal is string s)
            {
                result = s;
            }
            else if (resVal != null)
            {
                result = JsonSerializer.Serialize(resVal);
            }
            else
            {
                result = string.Empty;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}