using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text.Json;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.ComponentModel;

namespace LY.LlmPool.Client;

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

    private Kernel CreateKernel(string model)
    {
        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(model, _apiKey, httpClient: _httpClient);
        return builder.Build();
    }

    private Kernel CreateKernelWithObjects(string model, IEnumerable<object> toolObjects)
    {
        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(model, _apiKey, httpClient: _httpClient);
        var kernel = builder.Build();
        foreach (var obj in toolObjects)
        {
            if (obj != null) kernel.Plugins.AddFromObject(obj, obj.GetType().Name);
        }
        return kernel;
    }

    private (Kernel Kernel, OpenAIPromptExecutionSettings Settings) CreateKernelAndSettings(string model, IEnumerable<object>? toolObjects, ChatOptions? options)
    {
        if (toolObjects != null && toolObjects.Any())
        {
            var kernel = CreateKernelWithObjects(model, toolObjects);
            var settings = new OpenAIPromptExecutionSettings { ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions };
            if (options?.Temperature is not null) settings.Temperature = options.Temperature;
            if (options?.TopP is not null) settings.TopP = options.TopP;
            if (options?.MaxTokens is not null) settings.MaxTokens = options.MaxTokens;
            return (kernel, settings);
        }
        else
        {
            var kernel = CreateKernel(model);
            var settings = new OpenAIPromptExecutionSettings();
            if (options?.Temperature is not null) settings.Temperature = options.Temperature;
            if (options?.TopP is not null) settings.TopP = options.TopP;
            if (options?.MaxTokens is not null) settings.MaxTokens = options.MaxTokens;
            return (kernel, settings);
        }
    }

    private ChatHistory BuildChatHistory(IEnumerable<ClientMessage> messages)
    {
        var chatHistory = new ChatHistory();
        foreach (var m in messages)
        {
            var role = m.Role?.ToLower() switch
            {
                "system" => AuthorRole.System,
                "assistant" => AuthorRole.Assistant,
                _ => AuthorRole.User
            };
            if (m.ContentItems != null && m.ContentItems.Count > 0)
            {
                chatHistory.AddMessage(role, m.ContentItems);
            }
            else
            {
                chatHistory.AddMessage(role, m.Content ?? string.Empty);
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
        var (kernel, settings) = CreateKernelAndSettings(model, toolObjects, options);
        // 不再尝试通过 AdditionalProperties 透传参数；如需参数，请使用自定义 ITextGenerationService
        var chatHistory = BuildChatHistory(messages);
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        // 注意：若底层方法不支持 CancellationToken，此参数将被忽略
        var result = await chat.GetChatMessageContentAsync(chatHistory, settings, kernel);
        return result.Content ?? string.Empty;
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(
        string model,
        IEnumerable<ClientMessage> messages,
        Dictionary<string, object>? parameters = null,
        IEnumerable<object>? toolObjects = null,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (kernel, settings) = CreateKernelAndSettings(model, toolObjects, options);
        // 不再尝试通过 AdditionalProperties 透传参数；如需参数，请使用自定义 ITextGenerationService
        var chatHistory = BuildChatHistory(messages);
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var streaming = chat.GetStreamingChatMessageContentsAsync(chatHistory, settings, kernel);
        await foreach (var delta in streaming.WithCancellation(cancellationToken))
        {
            if (!string.IsNullOrEmpty(delta.Content)) yield return delta.Content;
        }
    }

    // Note: controller-direct HTTP methods removed to enforce SK-only calls
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

public class ClientTool
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Dictionary<string, object>? InputSchema { get; set; }
}

public partial class LlmPoolClient
{
    private HttpClient CreateRawHttp()
    {
        if (!string.IsNullOrEmpty(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }
        if (!_httpClient.DefaultRequestHeaders.Accept.Any())
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
        if (!_httpClient.DefaultRequestHeaders.Contains("Accept-Charset"))
        {
            _httpClient.DefaultRequestHeaders.Add("Accept-Charset", "utf-8");
        }
        return _httpClient;
    }

    public async Task<string> ChatAppAsync(
        string modelId,
        List<ClientMessage> messages,
        Dictionary<string, object>? parameters = null,
        List<ClientTool>? tools = null,
        Dictionary<string, object>? toolChoice = null,
        OpenAIPromptExecutionSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        var body = BuildOpenAIRequest(modelId, messages, parameters, tools, toolChoice, settings, stream: false);
        using var http = CreateRawHttp();
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() > 0)
        {
            var msg = choices[0].GetProperty("message");
            if (msg.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
            {
                return contentEl.GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    public async IAsyncEnumerable<string> ChatAppStreamAsync(
        string modelId,
        List<ClientMessage> messages,
        Dictionary<string, object>? parameters = null,
        List<ClientTool>? tools = null,
        Dictionary<string, object>? toolChoice = null,
        OpenAIPromptExecutionSettings? settings = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = BuildOpenAIRequest(modelId, messages, parameters, tools, toolChoice, settings, stream: true);
        using var http = CreateRawHttp();
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:")) continue;
            var data = line.Substring(5).Trim();
            if (data == "[DONE]") yield break;
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                {
                    var text = contentEl.GetString();
                    if (!string.IsNullOrEmpty(text)) yield return text!;
                }
            }
        }
    }

    private object BuildOpenAIRequest(
        string modelId,
        List<ClientMessage> messages,
        Dictionary<string, object>? parameters,
        List<ClientTool>? tools,
        Dictionary<string, object>? toolChoice,
        OpenAIPromptExecutionSettings? settings,
        bool stream)
    {
        object? toolsArr = null;
        if (tools != null && tools.Count > 0)
        {
            toolsArr = tools.Select(t => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.InputSchema
                }
            }).ToList();
        }

        object BuildContentPayload(ClientMessage m)
        {
            if (m.ContentItems == null || m.ContentItems.Count == 0)
            {
                return m.Content;
            }
            var list = new List<object>();
            foreach (var item in m.ContentItems)
            {
                switch (item)
                {
                    case TextContent text:
                        list.Add(new { type = "text", text = text.Text });
                        break;
                    case ImageContent img:
                        string? dataUrl = null;
                        if (img.Data.HasValue && img.Data.Value.Length > 0)
                        {
                            var bytes = img.Data.Value.ToArray();
                            var mime = string.IsNullOrEmpty(img.MimeType) ? "image/png" : img.MimeType;
                            dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                        }
                        else
                        {
                            dataUrl = img.Uri?.ToString();
                        }
                        if (!string.IsNullOrEmpty(dataUrl))
                        {
                            list.Add(new { type = "image_url", image_url = new { url = dataUrl } });
                        }
                        break;
                }
            }
            return list.Count > 0 ? list : (object)(m.Content ?? string.Empty);
        }

        var payloadMessages = messages.Select(m => new { role = m.Role, content = BuildContentPayload(m) }).ToList();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["messages"] = payloadMessages,
            ["stream"] = stream,
        };
        if (settings?.Temperature is not null) payload["temperature"] = settings.Temperature;
        if (settings?.MaxTokens is not null) payload["max_tokens"] = settings.MaxTokens;
        if (settings?.TopP is not null) payload["top_p"] = settings.TopP;
        if (parameters != null && parameters.Count > 0) payload["parameters"] = parameters;
        if (toolsArr != null) payload["tools"] = toolsArr;
        if (toolChoice != null && toolChoice.Count > 0) payload["tool_choice"] = toolChoice;
        return payload;
    }

    // -------- App + Real ToolObjects (non-streaming) --------

    public async Task<string> ChatAppAsync(
        string modelId,
        List<ClientMessage> messages,
        IEnumerable<object> toolObjects,
        Dictionary<string, object>? parameters = null,
        OpenAIPromptExecutionSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        // Use Semantic Kernel to register and auto-invoke functions
        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(modelId, _apiKey, httpClient: _httpClient);
        var kernel = builder.Build();

        foreach (var obj in toolObjects)
        {
            if (obj != null) kernel.Plugins.AddFromObject(obj, obj.GetType().Name);
        }

        var exec = settings ?? new OpenAIPromptExecutionSettings();
        exec.ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions;
        var chatHistory = BuildChatHistory(messages);
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var result = await chat.GetChatMessageContentAsync(chatHistory, exec, kernel, cancellationToken);
        return result.Content ?? string.Empty;
    }
}
