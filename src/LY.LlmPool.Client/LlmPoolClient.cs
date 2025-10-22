using Microsoft.Extensions.AI;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.ClientModel;
using OpenAI;
using System.ClientModel.Primitives;
using Microsoft.Extensions.Logging;

namespace LY.LlmPool.Client;

/// <summary>
/// HTTP message handler to inject parameters into the request body and extract ConversationId from response.
/// </summary>
internal class ParameterInjectionHandler : DelegatingHandler
{
    private readonly Dictionary<string, object>? _parameters;
    private readonly Action<string>? _onConversationIdReceived;

    public ParameterInjectionHandler(Dictionary<string, object>? parameters, Action<string>? onConversationIdReceived = null)
    {
        _parameters = parameters;
        _onConversationIdReceived = onConversationIdReceived;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_parameters is not null && _parameters.Count > 0 && request.Content is not null)
        {
            var originalContent = await request.Content.ReadAsStringAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(originalContent))
            {

                var jsonNode = JsonNode.Parse(originalContent);
                if (jsonNode is JsonObject jsonObject)
                {
                    if (jsonObject["parameters"] is JsonObject existingParams)
                    {
                        foreach (var kv in _parameters)
                        {
                            existingParams[kv.Key] = ToJsonValue(kv.Value);
                        }
                    }
                    else
                    {
                        var paramsObj = new JsonObject();
                        foreach (var kv in _parameters)
                        {
                            paramsObj[kv.Key] = ToJsonValue(kv.Value);
                        }
                        jsonObject["parameters"] = paramsObj;
                    }

                    var modifiedContent = jsonObject.ToJsonString();
                    request.Content = new StringContent(modifiedContent, System.Text.Encoding.UTF8, "application/json");
                }
            }
        }

        // 调用下一个处理器或发送请求
        var response = await base.SendAsync(request, cancellationToken);
        
        // 🎯 从响应头中提取 ConversationId
        if (response.Headers.TryGetValues("X-Conversation-Id", out var conversationIdValues))
        {
            var conversationId = conversationIdValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(conversationId))
            {
                _onConversationIdReceived?.Invoke(conversationId);
            }
        }
        
        return response;
    }

    private static JsonNode? ToJsonValue(object value)
    {
        return value switch
        {
            null => null,
            string s => s,
            bool b => b,
            int i => i,
            long l => l,
            double d => d,
            float f => f,
            decimal m => m,
            Guid g => g.ToString(),
            DateTime dt => dt.ToString("O"),
            IEnumerable<string> strEnum => new JsonArray(strEnum.Select(v => (JsonNode?)v).ToArray()),
            IEnumerable<int> intEnum => new JsonArray(intEnum.Select(v => (JsonNode?)v).ToArray()),
            _ => JsonValue.Create(value?.ToString())
        };
    }
}

/// <summary>
/// LlmPool 客户端,用于与 LlmPool 服务端通信
/// </summary>
public class LlmPoolClient : ILlmPoolClient
{
    private readonly HttpClient _httpClient;
    private readonly HttpMessageHandler? _customHandler; // 🎯 保存自定义 handler（用于测试 Mock）
    private readonly string _apiKey;
    private string? _conversationId;
    
    /// <summary>
    /// 当前会话的 ConversationId（从服务端响应中获取或设置）
    /// </summary>
    public string? ConversationId 
    { 
        get => _conversationId;
        set
        {
            _conversationId = value;
            // 更新 HttpClient 的默认请求头
            _httpClient.DefaultRequestHeaders.Remove("X-Conversation-Id");
            if (!string.IsNullOrEmpty(_conversationId))
            {
                _httpClient.DefaultRequestHeaders.Add("X-Conversation-Id", _conversationId);
            }
        }
    }

    public LlmPoolClient(string baseUrl, string apiKey, string? conversationId = null)
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10) };
        _customHandler = null; // 没有自定义 handler
        _apiKey = apiKey;
        ConversationId = conversationId; // 使用属性设置器
    }

    /// <summary>
    /// 构造函数：使用已有的 HttpClient
    /// </summary>
    public LlmPoolClient(HttpClient httpClient, string apiKey, string? conversationId = null)
        : this(httpClient, apiKey, conversationId, customHandler: null)
    {
    }

    /// <summary>
    /// 构造函数：使用 HttpClient 和自定义 HttpMessageHandler（用于测试 Mock）
    /// </summary>
    /// <param name="httpClient">HttpClient 实例</param>
    /// <param name="apiKey">API 密钥</param>
    /// <param name="conversationId">会话 ID</param>
    /// <param name="customHandler">自定义 handler（如测试的 Mock handler），会被包装在 ParameterInjectionHandler 中</param>
    public LlmPoolClient(HttpClient httpClient, string apiKey, string? conversationId, HttpMessageHandler? customHandler)
    {
        _httpClient = httpClient;
        _customHandler = customHandler; // 🎯 保存自定义 handler
        _apiKey = apiKey;
        ConversationId = conversationId; // 使用属性设置器
    }

    private (IChatClient, Microsoft.Extensions.AI.ChatOptions) CreateChatClientAndOptions(
        string model,
        IEnumerable<object>? toolObjects,
        ChatOptions? options,
        Dictionary<string, object>? parameters)
    {
        // 🎯 ConversationId 优先级: options.ConversationId > this.ConversationId > null (从响应提取)
        string? effectiveConversationId = options?.ConversationId ?? this.ConversationId;
        
        HttpClient httpClient;

        // 🎯 只在真正需要参数注入或 ConversationId 提取时才创建新的 handler 链
        bool needsParameterInjection = parameters?.Count > 0;
        bool needsConversationIdExtraction = string.IsNullOrEmpty(effectiveConversationId);
        
        if (needsParameterInjection || needsConversationIdExtraction)
        {
            // 🎯 使用依赖注入的 customHandler（如果有），否则创建新的 HttpClientHandler
            HttpMessageHandler innerHandler = _customHandler ?? new HttpClientHandler();
            
            var paramHandler = new ParameterInjectionHandler(
                parameters, 
                onConversationIdReceived: (convId) => 
                {
                    // 🎯 从响应中接收到 ConversationId 后，更新客户端的 ConversationId
                    // 但不覆盖 options 中显式设置的值
                    if (string.IsNullOrEmpty(options?.ConversationId) && string.IsNullOrEmpty(this.ConversationId))
                    {
                        this.ConversationId = convId;
                    }
                })
            {
                InnerHandler = innerHandler
            };

            httpClient = new HttpClient(paramHandler)
            {
                BaseAddress = _httpClient.BaseAddress,
                Timeout = _httpClient.Timeout
            };

            // 复制原有 HttpClient 的默认请求头
            foreach (var header in _httpClient.DefaultRequestHeaders)
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }
            
            // 🎯 如果有 effectiveConversationId，添加到请求头
            if (!string.IsNullOrEmpty(effectiveConversationId))
            {
                httpClient.DefaultRequestHeaders.Remove("X-Conversation-Id");
                httpClient.DefaultRequestHeaders.Add("X-Conversation-Id", effectiveConversationId);
            }
        }
        else
        {
            // 🎯 不需要参数注入也不需要 ConversationId 提取，直接重用 _httpClient
            httpClient = _httpClient;
        }

        // 创建 OpenAI Client 并获取 IChatClient
        var credential = new ApiKeyCredential(_apiKey);
        var openAiClient = new OpenAIClient(credential, new OpenAIClientOptions
        {
            Endpoint = _httpClient.BaseAddress ?? new Uri("http://localhost"),
            Transport = new HttpClientPipelineTransport(httpClient)
        });

        // 获取 ChatClient 并转换为 IChatClient
        var chatClient = openAiClient.GetChatClient(model).AsIChatClient();

        // 提取并添加工具
        var tools = new List<AITool>();
        if (toolObjects != null)
        {
            foreach (var obj in toolObjects)
            {
                if (obj != null)
                {
                    // 使用反射提取带有 [Description] 属性的公共方法,转换为 MEAI 工具
                    var methods = obj.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                    foreach (var method in methods)
                    {
                        // 查找 Description 属性
                        var descAttr = method.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                            .FirstOrDefault() as System.ComponentModel.DescriptionAttribute;

                        if (descAttr != null)
                        {
                            var methodParams = method.GetParameters();
                            var description = descAttr.Description;

                            // 创建委托
                            Delegate func;
                            if (methodParams.Length == 0)
                            {
                                func = (Func<string>)(() => method.Invoke(obj, null)?.ToString() ?? string.Empty);
                            }
                            else if (methodParams.Length == 1 && methodParams[0].ParameterType == typeof(string))
                            {
                                func = (Func<string, string>)((p) => method.Invoke(obj, new object?[] { p })?.ToString() ?? string.Empty);
                            }
                            else if (methodParams.Length == 2 && methodParams[0].ParameterType == typeof(int) && methodParams[1].ParameterType == typeof(int))
                            {
                                func = (Func<int, int, int>)((a, b) => (int)(method.Invoke(obj, new object[] { a, b }) ?? 0));
                            }
                            else
                            {
                                // 跳过不支持的方法签名
                                continue;
                            }

                            // 创建 AIFunction 并添加到工具列表
                            var aiFunc = AIFunctionFactory.Create(func, method.Name, description);
                            tools.Add(aiFunc);
                        }
                    }
                }
            }
        }

        // 如果有工具,启用自动调用
        if (tools.Any())
        {
            chatClient = new ChatClientBuilder(chatClient)
                .UseFunctionInvocation()
                .Build();
        }

        // 如果传入了 options，使用它（它已经是 ChatOptions，继承自 Microsoft.Extensions.AI.ChatOptions）
        // 否则创建默认的
        var chatOptions = options ?? new ChatOptions();
        
        // 确保 Tools 被设置
        if (tools.Count > 0 && chatOptions.Tools == null)
        {
            chatOptions.Tools = tools;
        }
        
        // 🎯 确保 ConversationId 被设置到 options 中（使用优先级后的值）
        if (!string.IsNullOrEmpty(effectiveConversationId))
        {
            chatOptions.ConversationId = effectiveConversationId;
        }

        return (chatClient, chatOptions);
    }

    private static List<ChatMessage> BuildChatMessages(IEnumerable<ClientMessage> messages)
    {
        var chatMessages = new List<ChatMessage>();
        foreach (var msg in messages)
        {
            var role = msg.Role.ToLowerInvariant() switch
            {
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                "system" => ChatRole.System,
                "tool" => ChatRole.Tool,
                _ => throw new ArgumentException($"Unknown role: {msg.Role}")
            };

            if (msg.ContentItems is not null && msg.ContentItems.Count > 0)
            {
                chatMessages.Add(new ChatMessage(role, msg.ContentItems));
            }
            else
            {
                chatMessages.Add(new ChatMessage(role, msg.Content));
            }
        }
        return chatMessages;
    }

    public async Task<string> ChatAsync(
        string model,
        IEnumerable<ClientMessage> messages,
        Dictionary<string, object>? parameters = null,
        IEnumerable<object>? toolObjects = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (chatClient, chatOptions) = CreateChatClientAndOptions(model, toolObjects, options, parameters);
        var chatMessages = BuildChatMessages(messages);

        var response = await chatClient.GetResponseAsync(chatMessages, chatOptions, cancellationToken);
        return response.Text ?? string.Empty;
    }

    public async IAsyncEnumerable<StreamingChatUpdate> ChatStreamAsync(
        string model,
        IEnumerable<ClientMessage> messages,
        Dictionary<string, object>? parameters = null,
        IEnumerable<object>? toolObjects = null,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (chatClient, chatOptions) = CreateChatClientAndOptions(model, toolObjects, options, parameters);
        var chatMessages = BuildChatMessages(messages);

        await foreach (var update in chatClient.GetStreamingResponseAsync(chatMessages, chatOptions, cancellationToken))
        {
            yield return new StreamingChatUpdate
            {
                Text = update.Text,
                Content = update.Text,
                Items = update.Contents?.ToList(),
                Contents = update.Contents?.ToList(),
                Role = update.Role
            };
        }
    }
}

public class ClientMessage
{
    public string Role { get; set; } = "user";
    public string? Content { get; set; }
    public IList<AIContent>? ContentItems { get; set; }
}

/// <summary>
/// Chat options - directly use Microsoft.Extensions.AI.ChatOptions
/// Note: Microsoft.Extensions.AI.ChatOptions already has ConversationId property
/// </summary>
public class ChatOptions : Microsoft.Extensions.AI.ChatOptions
{
    // Inherits all properties from Microsoft.Extensions.AI.ChatOptions including:
    // - Temperature
    // - TopP
    // - MaxOutputTokens (use this instead of MaxTokens)
    // - ConversationId
    // - ModelId
    // - etc.
}

public class StreamingChatUpdate
{
    public string? Text { get; set; }
    public string? Content { get; set; }
    public IReadOnlyList<AIContent>? Items { get; set; }
    public IReadOnlyList<AIContent>? Contents { get; set; }
    public ChatRole? Role { get; set; }
}
