using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.VectorData;
using System.Text.Json.Serialization;

namespace LY.LlmPool.Web.Data.Entities;

/// <summary>
/// Prompt 缓存条目，使用向量存储实现语义缓存
/// </summary>
public class PromptCacheEntry
{
    public const int VectorDimensions = 1536; // OpenAI text-embedding-3-small default
    public const string VectorDistanceFunction = DistanceFunction.CosineDistance;
    public const string CollectionName = "prompt_cache";

    /// <summary>
    /// 唯一标识符
    /// </summary>
    [VectorStoreKey(StorageName = "id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Prompt 内容的哈希值（用于精确匹配）
    /// </summary>
    [VectorStoreData(IsIndexed = true, StorageName = "prompt_hash")]
    [JsonPropertyName("prompt_hash")]
    public string PromptHash { get; set; } = string.Empty;

    /// <summary>
    /// 原始 Prompt 内容（也作为向量生成源）
    /// </summary>
    [VectorStoreData(StorageName = "prompt_content")]
    [JsonPropertyName("prompt_content")]
    public string PromptContent { get; set; } = string.Empty;

    /// <summary>
    /// Prompt 的向量表示（用于语义相似度搜索）
    /// Vector 属性返回 PromptContent，表示对其进行向量化
    /// </summary>
    [VectorStoreVector(VectorDimensions, DistanceFunction = VectorDistanceFunction, StorageName = "embedding")]
    [JsonPropertyName("embedding")]
    public string? Vector => PromptContent;

    /// <summary>
    /// 缓存的响应内容
    /// </summary>
    [VectorStoreData(StorageName = "cached_response")]
    [JsonPropertyName("cached_response")]
    public string? CachedResponse { get; set; }

    /// <summary>
    /// 缓存的响应 JSON（包含完整的响应对象）
    /// </summary>
    [VectorStoreData(StorageName = "cached_response_json")]
    [JsonPropertyName("cached_response_json")]
    public string? CachedResponseJson { get; set; }

    /// <summary>
    /// 模型配置 ID
    /// </summary>
    [VectorStoreData(IsIndexed = true, StorageName = "llm_config_id")]
    [JsonPropertyName("llm_config_id")]
    public string? LlmConfigId { get; set; }

    /// <summary>
    /// 模型名称
    /// </summary>
    [VectorStoreData(IsIndexed = true, StorageName = "model_name")]
    [JsonPropertyName("model_name")]
    public string? ModelName { get; set; }

    /// <summary>
    /// 模型参数（温度、top_p 等）的 JSON 表示
    /// </summary>
    [VectorStoreData(StorageName = "model_parameters_json")]
    [JsonPropertyName("model_parameters_json")]
    public string? ModelParametersJson { get; set; }

    /// <summary>
    /// Prompt tokens 数量
    /// </summary>
    [VectorStoreData(StorageName = "prompt_tokens")]
    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; set; }

    /// <summary>
    /// Completion tokens 数量
    /// </summary>
    [VectorStoreData(StorageName = "completion_tokens")]
    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; set; }

    /// <summary>
    /// 缓存命中次数
    /// </summary>
    [VectorStoreData(StorageName = "hit_count")]
    [JsonPropertyName("hit_count")]
    public int HitCount { get; set; } = 0;

    /// <summary>
    /// 创建时间
    /// </summary>
    [VectorStoreData(StorageName = "created_at")]
    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 最后访问时间
    /// </summary>
    [VectorStoreData(StorageName = "last_accessed_at")]
    [JsonPropertyName("last_accessed_at")]
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 过期时间
    /// </summary>
    [VectorStoreData(IsIndexed = true, StorageName = "expires_at")]
    [JsonPropertyName("expires_at")]
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(1);

    /// <summary>
    /// 是否已过期
    /// </summary>
    [JsonIgnore]
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;

    /// <summary>
    /// 平均响应时间（毫秒）
    /// </summary>
    [VectorStoreData(StorageName = "average_response_time_ms")]
    [JsonPropertyName("average_response_time_ms")]
    public double? AverageResponseTimeMs { get; set; }

    /// <summary>
    /// 节省的总 token 数量
    /// </summary>
    [VectorStoreData(StorageName = "saved_tokens")]
    [JsonPropertyName("saved_tokens")]
    public long SavedTokens { get; set; } = 0;

    /// <summary>
    /// 元数据（JSON 格式）
    /// </summary>
    [VectorStoreData(StorageName = "metadata")]
    [JsonPropertyName("metadata")]
    public string? Metadata { get; set; }
}
