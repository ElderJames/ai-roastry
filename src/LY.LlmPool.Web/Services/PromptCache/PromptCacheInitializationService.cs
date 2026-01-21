using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using LY.LlmPool.Web.Data.Entities;

namespace LY.LlmPool.Web.Services.PromptCache;

/// <summary>
/// Prompt 缓存初始化服务 - 使用 Microsoft.Extensions.VectorData
/// </summary>
public class PromptCacheInitializationService : IHostedService
{
    private readonly VectorStoreCollection<string, PromptCacheEntry> _collection;
    private readonly ILogger<PromptCacheInitializationService> _logger;

    public PromptCacheInitializationService(
        VectorStoreCollection<string, PromptCacheEntry> collection,
        ILogger<PromptCacheInitializationService> logger)
    {
        _collection = collection;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 确保集合存在
            await _collection.EnsureCollectionExistsAsync(cancellationToken);
            
            _logger.LogInformation("✅ Prompt Cache vector store initialized successfully using Microsoft.Extensions.VectorData");
            _logger.LogInformation("📦 Collection: {CollectionName}, Dimensions: {Dimensions}, Distance: {DistanceFunction}", 
                PromptCacheEntry.CollectionName, 
                PromptCacheEntry.VectorDimensions,
                PromptCacheEntry.VectorDistanceFunction);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to initialize Prompt Cache vector store");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Prompt Cache initialization service stopped");
        return Task.CompletedTask;
    }
}
