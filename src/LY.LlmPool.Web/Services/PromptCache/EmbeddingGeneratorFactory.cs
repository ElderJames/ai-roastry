using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LY.LlmPool.Web.Services.PromptCache;

/// <summary>
/// Embedding 生成器配置选项
/// </summary>
public class EmbeddingGeneratorOptions
{
    /// <summary>
    /// Embedding 模型提供者（OpenAI, Azure, GitHub, 等）
    /// </summary>
    public string Provider { get; set; } = "OpenAI";

    /// <summary>
    /// API 端点
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// API Key
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Embedding 模型名称
    /// </summary>
    public string ModelName { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// 模型维度
    /// </summary>
    public int Dimensions { get; set; } = 1536;

    /// <summary>
    /// 批量处理大小
    /// </summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>
    /// 请求超时（秒）
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Embedding 生成器工厂
/// </summary>
public class EmbeddingGeneratorFactory
{
    private readonly EmbeddingGeneratorOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public EmbeddingGeneratorFactory(
        IOptions<EmbeddingGeneratorOptions> options,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// 创建 Embedding 生成器
    /// </summary>
    public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator()
    {
        return _options.Provider.ToLowerInvariant() switch
        {
            "openai" => CreateOpenAIGenerator(),
            "azure" => CreateAzureOpenAIGenerator(),
            "github" => CreateGitHubGenerator(),
            _ => throw new NotSupportedException($"Provider '{_options.Provider}' is not supported")
        };
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateOpenAIGenerator()
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
        {
            throw new InvalidOperationException("OpenAI API Key is required");
        }

        var client = new OpenAI.OpenAIClient(
            new System.ClientModel.ApiKeyCredential(_options.ApiKey),
            new OpenAI.OpenAIClientOptions
            {
                Endpoint = string.IsNullOrEmpty(_options.Endpoint) 
                    ? null 
                    : new Uri(_options.Endpoint)
            });

        return client
            .GetEmbeddingClient(_options.ModelName)
            .AsIEmbeddingGenerator();
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateAzureOpenAIGenerator()
    {
        if (string.IsNullOrEmpty(_options.ApiKey) || string.IsNullOrEmpty(_options.Endpoint))
        {
            throw new InvalidOperationException("Azure OpenAI API Key and Endpoint are required");
        }

        var client = new Azure.AI.OpenAI.AzureOpenAIClient(
            new Uri(_options.Endpoint),
            new System.ClientModel.ApiKeyCredential(_options.ApiKey));

        return client
            .GetEmbeddingClient(_options.ModelName)
            .AsIEmbeddingGenerator();
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateGitHubGenerator()
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
        {
            throw new InvalidOperationException("GitHub Token is required");
        }

        var client = new OpenAI.OpenAIClient(
            new System.ClientModel.ApiKeyCredential(_options.ApiKey),
            new OpenAI.OpenAIClientOptions 
            { 
                Endpoint = new Uri("https://models.inference.ai.azure.com")
            });

        return client
            .GetEmbeddingClient(_options.ModelName)
            .AsIEmbeddingGenerator();
    }
}
