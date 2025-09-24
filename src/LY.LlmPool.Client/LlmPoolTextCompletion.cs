using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Services;
using Microsoft.SemanticKernel.TextGeneration;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace LY.LlmPool.Client
{
    /// <summary>
    /// LLM Pool专用文本生成服务，支持自定义参数
    /// </summary>
    public class LlmPoolTextCompletion : ITextGenerationService, IAIService
    {
        private readonly Dictionary<string, object?> _attributes = new();
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly LlmPoolTextCompletionOptions _options;
        private readonly ILogger<LlmPoolTextCompletion> _logger;

        private static readonly JsonSerializerOptions _jsonSerializerOptions = new()
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public IReadOnlyDictionary<string, object?> Attributes => _attributes;

        public LlmPoolTextCompletion(
            LlmPoolTextCompletionOptions options,
            IHttpClientFactory httpClientFactory,
            ILogger<LlmPoolTextCompletion> logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // 设置服务属性
            _attributes.Add(AIServiceExtensions.ModelIdKey, _options.ModelId);
        }

        public async Task<IReadOnlyList<TextContent>> GetTextContentsAsync(
            string prompt,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("开始获取文本内容，Prompt长度: {PromptLength}", prompt.Length);

            var chatExecutionSettings = OpenAIPromptExecutionSettings.FromExecutionSettings(executionSettings);
            var messages = ParsePromptToMessages(prompt);

            var request = CreateChatRequest(messages, chatExecutionSettings, false);

            var httpClient = CreateHttpClient();
            var response = await httpClient.PostAsJsonAsync(_options.ChatCompletionsEndpoint, request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("API调用失败: {StatusCode} - {Error}", response.StatusCode, errorContent);
                throw new HttpRequestException($"API调用失败: {response.StatusCode} - {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var chatResponse = JsonSerializer.Deserialize<ChatCompletionResponse>(responseContent, _jsonSerializerOptions);

            var content = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
            _logger.LogInformation("获取到文本内容，长度: {ContentLength}", content.Length);

            return [new TextContent(content)];
        }

        public IAsyncEnumerable<StreamingTextContent> GetStreamingTextContentsAsync(
            string prompt,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("开始流式获取文本内容，Prompt长度: {PromptLength}", prompt.Length);

            var chatExecutionSettings = OpenAIPromptExecutionSettings.FromExecutionSettings(executionSettings);
            var messages = ParsePromptToMessages(prompt);

            return GetStreamingContentAsync(messages, chatExecutionSettings, kernel, cancellationToken);
        }

        private async IAsyncEnumerable<StreamingTextContent> GetStreamingContentAsync(
            List<ChatMessage> messages,
            OpenAIPromptExecutionSettings executionSettings,
            Kernel? kernel,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = CreateChatRequest(messages, executionSettings, true);
            var httpClient = CreateHttpClient();

            var response = await httpClient.PostAsJsonAsync(_options.ChatCompletionsEndpoint, request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("流式API调用失败: {StatusCode} - {Error}", response.StatusCode, errorContent);
                yield return new StreamingTextContent($"错误: {response.StatusCode} - {errorContent}");
                yield break;
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            var totalChunks = 0;
            var totalLength = 0;

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line)) continue;

                if (line.StartsWith("data: "))
                {
                    var data = line.Substring(6);
                    if (data == "[DONE]") break;

                    var contentStr = ExtractContentFromStreamData(data);
                    if (!string.IsNullOrEmpty(contentStr))
                    {
                        totalChunks++;
                        totalLength += contentStr.Length;
                        yield return new StreamingTextContent(contentStr);
                    }
                }
            }

            _logger.LogInformation("流式响应完成，总块数: {Chunks}, 总长度: {Length}", totalChunks, totalLength);
        }

        private HttpClient CreateHttpClient()
        {
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.BaseAddress = new Uri(_options.BaseUrl);
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
            httpClient.Timeout = TimeSpan.FromMinutes(10);
            return httpClient;
        }

        private object CreateChatRequest(
            List<ChatMessage> messages,
            OpenAIPromptExecutionSettings executionSettings,
            bool stream)
        {
            var request = new
            {
                model = _options.ModelId,
                messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                stream = stream,
                temperature = executionSettings.Temperature,
                max_tokens = executionSettings.MaxTokens,
                top_p = executionSettings.TopP,
                frequency_penalty = executionSettings.FrequencyPenalty,
                presence_penalty = executionSettings.PresencePenalty,
                // 在报文中增加 prompt 模板参数
                parameters = _options.CustomParameters
            };

            return request;
        }

        private List<ChatMessage> ParsePromptToMessages(string prompt)
        {
            var messages = new List<ChatMessage>();

            if (prompt.Contains("history："))
            {
                var histories = prompt.Replace("history：", "")
                    .Split("\r\n")
                    .Select(m => m.Split(":", 2))
                    .Where(m => m.Length == 2)
                    .Select(pair => new ChatMessage
                    {
                        Role = pair[0].Trim() == "user" ? "user" : "assistant",
                        Content = pair[1].Trim()
                    }).ToList();

                messages.AddRange(histories);
            }
            else
            {
                messages.Add(new ChatMessage { Role = "user", Content = prompt });
            }

            return messages;
        }

        private string? ExtractContentFromStreamData(string data)
        {
            try
            {
                var jsonDoc = JsonDocument.Parse(data);
                var delta = jsonDoc.RootElement.GetProperty("choices")[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var content))
                {
                    return content.GetString();
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("解析流数据失败: {Error}", ex.Message);
            }
            return null;
        }

        private class ChatCompletionResponse
        {
            public List<Choice>? Choices { get; set; }
        }

        private class Choice
        {
            public Message? Message { get; set; }
        }

        private class Message
        {
            public string? Content { get; set; }
        }
    }

    /// <summary>
    /// LLM Pool文本生成服务配置选项
    /// </summary>
    public class LlmPoolTextCompletionOptions
    {
        public string BaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public string ChatCompletionsEndpoint { get; set; } = "/v1/chat/completions";
        /// <summary>
        /// 自定义参数（用于App调用时的参数传递、prompt模板参数）
        /// </summary>
        public Dictionary<string, object>? CustomParameters { get; set; }
    }

    /// <summary>
    /// LLM Pool文本生成服务扩展方法
    /// </summary>
    public static class LlmPoolTextCompletionExtensions
    {
        public static IKernelBuilder AddLlmPoolTextGeneration(
            this IKernelBuilder builder,
            string baseUrl,
            string apiKey,
            string modelId,
            Dictionary<string, object>? customParameters = null,
            string? serviceId = null)
        {
            var options = new LlmPoolTextCompletionOptions
            {
                BaseUrl = baseUrl,
                ApiKey = apiKey,
                ModelId = modelId,
                CustomParameters = customParameters
            };

            builder.Services.AddKeyedSingleton<ITextGenerationService>(serviceId, (serviceProvider, _) =>
            {
                var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
                var logger = serviceProvider.GetRequiredService<ILogger<LlmPoolTextCompletion>>();
                return new LlmPoolTextCompletion(options, httpClientFactory, logger);
            });

            return builder;
        }

        public static IKernelBuilder AddLlmPoolTextGeneration(
            this IKernelBuilder builder,
            LlmPoolTextCompletionOptions options,
            string? serviceId = null)
        {
            builder.Services.AddKeyedSingleton<ITextGenerationService>(serviceId, (serviceProvider, _) =>
            {
                var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
                var logger = serviceProvider.GetRequiredService<ILogger<LlmPoolTextCompletion>>();
                return new LlmPoolTextCompletion(options, httpClientFactory, logger);
            });

            return builder;
        }
    }

    internal class ChatMessage
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }
}
