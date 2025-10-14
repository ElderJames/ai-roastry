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
/// HTTP message handler to inject parameters into the request body.
/// </summary>
internal class ParameterInjectionHandler : DelegatingHandler
{
    private readonly Dictionary<string, object>? _parameters;

    public ParameterInjectionHandler(Dictionary<string, object>? parameters)
    {
        _parameters = parameters;
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
        return await base.SendAsync(request, cancellationToken);
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

public class LlmPoolClient
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

    private (IChatClient, Microsoft.Extensions.AI.ChatOptions) CreateChatClientAndOptions(
        string model,
        IEnumerable<object>? toolObjects,
        ChatOptions? options,
        Dictionary<string, object>? parameters)
    {
        HttpClient httpClient;

        // 如果有参数需要注入,创建带参数注入的新 HttpClient
        if (parameters != null && parameters.Count > 0)
        {
            // 创建 handler 链: ParameterInjectionHandler -> HttpClientHandler
            HttpMessageHandler innerHandler = new HttpClientHandler();
            var paramHandler = new ParameterInjectionHandler(parameters)
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
        }
        else
        {
            // 直接使用传入的 HttpClient
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

        var chatOptions = new Microsoft.Extensions.AI.ChatOptions
        {
            Temperature = options?.Temperature.HasValue == true ? (float)options.Temperature.Value : null,
            TopP = options?.TopP.HasValue == true ? (float)options.TopP.Value : null,
            MaxOutputTokens = options?.MaxTokens,
            Tools = tools.Count > 0 ? tools : null
        };

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

public class ChatOptions
{
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? MaxTokens { get; set; }
}

public class StreamingChatUpdate
{
    public string? Text { get; set; }
    public string? Content { get; set; }
    public IReadOnlyList<AIContent>? Items { get; set; }
    public IReadOnlyList<AIContent>? Contents { get; set; }
    public ChatRole? Role { get; set; }
}
