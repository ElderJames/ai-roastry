namespace LY.LlmPool.Web.Services.PromptCache;

/// <summary>
/// Prompt 缓存配置选项
/// </summary>
public class PromptCacheOptions
{
    /// <summary>
    /// 是否启用 Prompt 缓存
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 缓存过期时间（秒），默认 1 小时
    /// </summary>
    public int ExpirationSeconds { get; set; } = 3600;

    /// <summary>
    /// 语义相似度阈值（0-1），默认 0.95
    /// 只有相似度大于等于此阈值的 prompt 才会被视为缓存命中
    /// </summary>
    public float SimilarityThreshold { get; set; } = 0.95f;

    /// <summary>
    /// 是否使用精确匹配（哈希对比）
    /// </summary>
    public bool UseExactMatch { get; set; } = true;

    /// <summary>
    /// 是否使用语义匹配（向量相似度）
    /// </summary>
    public bool UseSemanticMatch { get; set; } = true;

    /// <summary>
    /// 是否启用语义匹配功能（需要 embedding generator）
    /// </summary>
    public bool EnableSemanticMatching { get; set; } = true;

    /// <summary>
    /// 语义搜索返回的最大结果数
    /// </summary>
    public int MaxSearchResults { get; set; } = 5;

    /// <summary>
    /// 最小 prompt 长度才启用缓存（字符数）
    /// </summary>
    public int MinPromptLength { get; set; } = 50;

    /// <summary>
    /// 最大缓存条目数（LRU 淘汰）
    /// </summary>
    public int MaxCacheSize { get; set; } = 10000;

    /// <summary>
    /// 自动清理过期缓存的间隔（秒）
    /// </summary>
    public int CleanupIntervalSeconds { get; set; } = 300; // 5 分钟

    /// <summary>
    /// 是否在缓存命中时更新访问时间
    /// </summary>
    public bool UpdateAccessTimeOnHit { get; set; } = true;

    /// <summary>
    /// SQLite 向量存储数据库路径
    /// </summary>
    public string VectorDbPath { get; set; } = "prompt-cache-vectors.db";

    /// <summary>
    /// 向量存储集合名称
    /// </summary>
    public string CollectionName { get; set; } = "prompt_cache";

    /// <summary>
    /// 嵌入模型维度
    /// </summary>
    public int EmbeddingDimensions { get; set; } = 1536; // OpenAI text-embedding-3-small

    /// <summary>
    /// 是否记录缓存统计信息
    /// </summary>
    public bool EnableStatistics { get; set; } = true;

    /// <summary>
    /// 是否在日志中记录缓存命中/未命中
    /// </summary>
    public bool LogCacheHitMiss { get; set; } = true;
}
