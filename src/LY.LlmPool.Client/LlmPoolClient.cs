using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LY.LlmPool.Client;

/// <summary>
/// HTTP message handler to inject parameters into the request body.
/// </summary>
file class ParameterInjectionHandler : HttpMessageHandler
{
    private readonly Dictionary<string, object>? _parameters;
    private readonly HttpClient _forwardClient;

    public ParameterInjectionHandler(HttpClient forwardClient, Dictionary<string, object>? parameters)
    {
        _forwardClient = forwardClient;
        _parameters = parameters;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_parameters is not null && _parameters.Count > 0 && request.Content is not null)
        {
            var originalContent = await request.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(originalContent))
            {
                try
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
                        request.Content = new StringContent(jsonObject.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                    }
                }
                catch
                {
                    // ignore invalid json and keep original content
                }
            }
        }

        // 转发到外部 HttpClient（其内部可能包含自定义处理器，如测试中的 StreamingHandler）
        // 需要克隆请求以避免多次发送产生的副作用
        using var forwardRequest = await CloneHttpRequestMessageAsync(request, cancellationToken);
        return await _forwardClient.SendAsync(forwardRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<HttpRequestMessage> CloneHttpRequestMessageAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        // copy headers
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        foreach (var prop in request.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(prop.Key), prop.Value);
        }
        if (request.Content != null)
        {
            var ms = new MemoryStream();
            await request.Content.CopyToAsync(ms, ct);
            ms.Position = 0;
            var content = new StreamContent(ms);
            foreach (var header in request.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            clone.Content = content;
        }
        return clone;
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

public partial class LlmPoolClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public LlmPoolClient(string baseUrl, string apiKey)
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10) };
        _apiKey = apiKey;
    }

    public LlmPoolClient(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
    }

    private (Kernel, OpenAIPromptExecutionSettings) CreateKernelAndSettings(
        string model,
        IEnumerable<object>? toolObjects,
        ChatOptions? options,
        Dictionary<string, object>? parameters)
    {
    var handler = new ParameterInjectionHandler(_httpClient, parameters);
    var httpClient = new HttpClient(handler) { BaseAddress = _httpClient.BaseAddress, Timeout = _httpClient.Timeout };
        
        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(model, _apiKey, httpClient: httpClient);

        var kernel = builder.Build();

        if (toolObjects != null)
        {
            foreach (var obj in toolObjects)
            {
                if (obj != null) kernel.Plugins.AddFromObject(obj, obj.GetType().Name);
            }
        }

        var settings = new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = toolObjects?.Any() == true ? ToolCallBehavior.AutoInvokeKernelFunctions : null,
        };

        // 不再依赖 ExtensionData 注入，改由 handler 直接修改 HTTP 内容

        if (options?.Temperature is not null) settings.Temperature = options.Temperature;
        if (options?.TopP is not null) settings.TopP = options.TopP;
        if (options?.MaxTokens is not null) settings.MaxTokens = options.MaxTokens;

        return (kernel, settings);
    }

    private static ChatHistory BuildChatHistory(IEnumerable<ClientMessage> messages)
    {
        var chatHistory = new ChatHistory();
        foreach (var msg in messages)
        {
            var role = msg.Role.ToLowerInvariant() switch
            {
                "user" => AuthorRole.User,
                "assistant" => AuthorRole.Assistant,
                "system" => AuthorRole.System,
                "tool" => AuthorRole.Tool,
                _ => throw new ArgumentException($"Unknown role: {msg.Role}")
            };

            if (msg.ContentItems is not null && msg.ContentItems.Any())
            {
                chatHistory.Add(new ChatMessageContent(role, msg.ContentItems));
            }
            else
            {
                chatHistory.Add(new ChatMessageContent(role, msg.Content));
            }
        }
        return chatHistory;
    }

    public async Task<string> ChatAsync(
        string model,
        IEnumerable<ClientMessage> messages,
        Dictionary<string, object>? parameters = null,
        IEnumerable<object>? toolObjects = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (kernel, settings) = CreateKernelAndSettings(model, toolObjects, options, parameters);
        var chatHistory = BuildChatHistory(messages);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var result = await chat.GetChatMessageContentAsync(chatHistory, settings, kernel, cancellationToken);
        return result.Content ?? string.Empty;
    }

    public IAsyncEnumerable<StreamingChatMessageContent> ChatStreamAsync(
        string model,
        IEnumerable<ClientMessage> messages,
        Dictionary<string, object>? parameters = null,
        IEnumerable<object>? toolObjects = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (kernel, settings) = CreateKernelAndSettings(model, toolObjects, options, parameters);
        var chatHistory = BuildChatHistory(messages);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        return chat.GetStreamingChatMessageContentsAsync(chatHistory, settings, kernel, cancellationToken);
    }

}

/// <summary>
/// Custom execution settings for LlmPool that includes a 'parameters' dictionary.
/// </summary>
public class LlmPoolPromptExecutionSettings : OpenAIPromptExecutionSettings
{
    /// <summary>
    /// Gets or sets the parameters for the request.
    /// This will be serialized as a 'parameters' object in the JSON request body.
    /// </summary>
    [JsonPropertyName("parameters")]
    public Dictionary<string, object>? Parameters { get; set; }
}

public class ClientMessage
{
    public string Role { get; set; } = "user";
    public string? Content { get; set; }
    public ChatMessageContentItemCollection? ContentItems { get; set; }
}

public class ChatOptions
{
    // 采样参数
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? MaxTokens { get; set; }
}
