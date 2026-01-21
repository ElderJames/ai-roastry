using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using LY.LlmPool.Web.Data.Entities;

namespace LY.LlmPool.Web.Services.PromptCache;

/// <summary>
/// Prompt 缓存服务，使用 Microsoft.Extensions.VectorData 抽象实现语义缓存
/// </summary>
public class PromptCacheService
{
    private readonly PromptCacheOptions _options;
    private readonly VectorStoreCollection<string, PromptCacheEntry> _collection;
    private readonly ILogger<PromptCacheService> _logger;
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);
    private DateTime _lastCleanup = DateTime.UtcNow;

    public PromptCacheService(
        IOptions<PromptCacheOptions> options,
        VectorStoreCollection<string, PromptCacheEntry> collection,
        ILogger<PromptCacheService> logger)
    {
        _options = options.Value;
        _collection = collection;
        _logger = logger;
    }

    /// <summary>
    /// 尝试从缓存中获取响应
    /// </summary>
    public async Task<PromptCacheResult?> TryGetCachedResponseAsync(
        string prompt,
        string? llmConfigId = null,
        string? modelName = null,
        string? modelParametersJson = null,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(prompt) || prompt.Length < _options.MinPromptLength)
        {
            return null;
        }

        try
        {
            // 1. 首先尝试精确匹配（哈希对比）
            var hash = ComputeHash(prompt);
            var exactMatch = await TryGetExactMatchAsync(hash, llmConfigId, modelName, cancellationToken);
            if (exactMatch != null)
            {
                if (_options.LogCacheHitMiss)
                {
                    _logger.LogInformation("Prompt cache HIT (exact match): {Hash}, saved {Tokens} tokens", 
                        hash[..8], exactMatch.SavedTokens);
                }
                return exactMatch;
            }

            // 2. 如果启用语义匹配，使用向量搜索
            if (_options.EnableSemanticMatching)
            {
                var semanticMatch = await TryGetSemanticMatchAsync(prompt, llmConfigId, modelName, cancellationToken);
                if (semanticMatch != null)
                {
                    if (_options.LogCacheHitMiss)
                    {
                        _logger.LogInformation("Prompt cache HIT (semantic match): similarity={Score:F3}, saved {Tokens} tokens", 
                            semanticMatch.SimilarityScore, semanticMatch.SavedTokens);
                    }
                    return semanticMatch;
                }
            }

            if (_options.LogCacheHitMiss)
            {
                _logger.LogDebug("Prompt cache MISS for prompt: {Prompt}", 
                    prompt.Length > 100 ? prompt[..100] + "..." : prompt);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving from prompt cache");
            return null;
        }
    }

    /// <summary>
    /// 缓存响应
    /// </summary>
    public async Task CacheResponseAsync(
        string prompt,
        string response,
        string? responseJson = null,
        string? llmConfigId = null,
        string? modelName = null,
        string? modelParametersJson = null,
        int? promptTokens = null,
        int? completionTokens = null,
        double? responseTimeMs = null,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(prompt) || prompt.Length < _options.MinPromptLength)
        {
            return;
        }

        try
        {
            var entry = new PromptCacheEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                PromptHash = ComputeHash(prompt),
                PromptContent = prompt,
                CachedResponse = response,
                CachedResponseJson = responseJson,
                LlmConfigId = llmConfigId,
                ModelName = modelName,
                ModelParametersJson = modelParametersJson,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                AverageResponseTimeMs = responseTimeMs,
                CreatedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddSeconds(_options.ExpirationSeconds),
                HitCount = 0,
                SavedTokens = 0
            };

            // 使用 UpsertAsync 存储，向量会自动生成
            await _collection.UpsertAsync(entry, cancellationToken: cancellationToken);

            _logger.LogDebug("Cached prompt response: {Hash}, expires at {ExpiresAt}", 
                entry.PromptHash[..8], entry.ExpiresAt);

            // 定期清理过期缓存
            await TryCleanupExpiredCacheAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error caching prompt response");
        }
    }

    /// <summary>
    /// 精确匹配查找（通过哈希）
    /// </summary>
    private async Task<PromptCacheResult?> TryGetExactMatchAsync(
        string hash,
        string? llmConfigId,
        string? modelName,
        CancellationToken cancellationToken)
    {
        try
        {
            // 使用 VectorStoreRecordOptions 的 Filter 进行过滤
            var filter = new VectorSearchFilter()
                .EqualTo(nameof(PromptCacheEntry.PromptHash), hash);
            
            if (!string.IsNullOrEmpty(llmConfigId))
            {
                filter.EqualTo(nameof(PromptCacheEntry.LlmConfigId), llmConfigId);
            }

            if (!string.IsNullOrEmpty(modelName))
            {
                filter.EqualTo(nameof(PromptCacheEntry.ModelName), modelName);
            }

            // 使用 VectorStoreCollection 的 SearchAsync 方法
            var results = _collection.SearchAsync(
                hash, // 搜索文本（这里用hash，实际上会匹配 PromptHash 字段）
                1, // maxResults
                new VectorSearchOptions<PromptCacheEntry>
                {
                    Filter = record => 
                        record.PromptHash == hash &&
                        (llmConfigId == null || record.LlmConfigId == llmConfigId) &&
                        (modelName == null || record.ModelName == modelName) &&
                        record.ExpiresAt > DateTime.UtcNow
                });

            await foreach (var result in results)
            {
                var entry = result.Record;
                
                // 更新访问统计
                if (_options.UpdateAccessTimeOnHit)
                {
                    entry.LastAccessedAt = DateTime.UtcNow;
                    entry.HitCount++;
                    entry.SavedTokens += (entry.PromptTokens ?? 0) + (entry.CompletionTokens ?? 0);
                    await _collection.UpsertAsync(entry, cancellationToken: cancellationToken);
                }

                return new PromptCacheResult
                {
                    CachedResponse = entry.CachedResponse ?? string.Empty,
                    CachedResponseJson = entry.CachedResponseJson,
                    PromptTokens = entry.PromptTokens,
                    CompletionTokens = entry.CompletionTokens,
                    SimilarityScore = 1.0f, // 精确匹配
                    HitCount = entry.HitCount,
                    SavedTokens = entry.SavedTokens,
                    CachedAt = entry.CreatedAt,
                    LastAccessedAt = entry.LastAccessedAt
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in exact match search");
            return null;
        }
    }

    /// <summary>
    /// 语义匹配查找（基于向量相似度）
    /// </summary>
    private async Task<PromptCacheResult?> TryGetSemanticMatchAsync(
        string prompt,
        string? llmConfigId,
        string? modelName,
        CancellationToken cancellationToken)
    {
        try
        {
            // 使用 VectorStoreCollection 的 SearchAsync 进行语义搜索
            var results = _collection.SearchAsync(
                prompt, // 查询文本，会自动生成 embedding
                _options.MaxSearchResults, // maxResults
                new VectorSearchOptions<PromptCacheEntry>
                {
                    // 过滤条件
                    Filter = record =>
                        (llmConfigId == null || record.LlmConfigId == llmConfigId) &&
                        (modelName == null || record.ModelName == modelName) &&
                        record.ExpiresAt > DateTime.UtcNow,
                });

            PromptCacheEntry? bestMatch = null;
            float bestSimilarity = 0;

            await foreach (var result in results)
            {
                var similarity = (float)(result.Score ?? 0);
                
                // 检查是否超过阈值并且是最佳匹配
                if (similarity >= _options.SimilarityThreshold && similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestMatch = result.Record;
                }
            }

            if (bestMatch != null)
            {
                // 更新访问统计
                if (_options.UpdateAccessTimeOnHit)
                {
                    bestMatch.LastAccessedAt = DateTime.UtcNow;
                    bestMatch.HitCount++;
                    bestMatch.SavedTokens += (bestMatch.PromptTokens ?? 0) + (bestMatch.CompletionTokens ?? 0);
                    await _collection.UpsertAsync(bestMatch, cancellationToken: cancellationToken);
                }

                return new PromptCacheResult
                {
                    CachedResponse = bestMatch.CachedResponse ?? string.Empty,
                    CachedResponseJson = bestMatch.CachedResponseJson,
                    PromptTokens = bestMatch.PromptTokens,
                    CompletionTokens = bestMatch.CompletionTokens,
                    SimilarityScore = bestSimilarity,
                    HitCount = bestMatch.HitCount,
                    SavedTokens = bestMatch.SavedTokens,
                    CachedAt = bestMatch.CreatedAt,
                    LastAccessedAt = bestMatch.LastAccessedAt
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in semantic match search");
            return null;
        }
    }

    /// <summary>
    /// 清理过期缓存
    /// </summary>
    private async Task TryCleanupExpiredCacheAsync(CancellationToken cancellationToken)
    {
        if ((DateTime.UtcNow - _lastCleanup).TotalSeconds < _options.CleanupIntervalSeconds)
        {
            return;
        }

        if (!await _cleanupLock.WaitAsync(0, cancellationToken))
        {
            return; // 已有清理任务在运行
        }

        try
        {
            _lastCleanup = DateTime.UtcNow;
            
            // 注意：VectorStore 抽象层可能不支持批量删除
            // 这里简化处理，实际生产环境可能需要后台任务
            _logger.LogDebug("Expired cache cleanup skipped (use background service for production)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cache cleanup");
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    /// <summary>
    /// 计算 prompt 的哈希值
    /// </summary>
    private static string ComputeHash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// 获取缓存统计信息
    /// </summary>
    public async Task<PromptCacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 注意：VectorStore 抽象层不直接支持聚合查询
            // 这里返回基本统计，详细统计需要通过其他方式实现
            _logger.LogWarning("Statistics not fully implemented with VectorStore abstraction");
            
            return new PromptCacheStatistics
            {
                TotalEntries = 0,
                TotalHits = 0,
                TotalSavedTokens = 0,
                AverageHitRate = 0,
                ExpiredEntries = 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache statistics");
            return new PromptCacheStatistics();
        }
    }

    /// <summary>
    /// 清空所有缓存
    /// </summary>
    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 删除并重建集合
            await _collection.EnsureCollectionDeletedAsync(cancellationToken);
            await _collection.EnsureCollectionExistsAsync(cancellationToken);
            
            _logger.LogInformation("Cleared all prompt cache entries");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing prompt cache");
        }
    }
}

/// <summary>
/// Prompt 缓存查询结果
/// </summary>
public class PromptCacheResult
{
    public string CachedResponse { get; set; } = string.Empty;
    public string? CachedResponseJson { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public float SimilarityScore { get; set; }
    public int HitCount { get; set; }
    public long SavedTokens { get; set; }
    public DateTime CachedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
}

/// <summary>
/// Prompt 缓存统计信息
/// </summary>
public class PromptCacheStatistics
{
    public int TotalEntries { get; set; }
    public int TotalHits { get; set; }
    public long TotalSavedTokens { get; set; }
    public float AverageHitRate { get; set; }
    public int ExpiredEntries { get; set; }
}
