using Microsoft.Extensions.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Data;
using Microsoft.Data.Sqlite;
using LY.LlmPool.Web.Data.Entities;

namespace LY.LlmPool.Web.Services.PromptCache;

/// <summary>
/// Prompt 缓存向量存储工厂
/// 简化实现：使用 SQLite 数据库存储向量数据
/// </summary>
public class PromptCacheVectorStoreFactory : IDisposable
{
    private readonly PromptCacheOptions _options;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ILogger<PromptCacheVectorStoreFactory> _logger;
    private SqliteConnection? _connection;
    private bool _disposed;

    public PromptCacheVectorStoreFactory(
        IOptions<PromptCacheOptions> options,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ILogger<PromptCacheVectorStoreFactory> logger)
    {
        _options = options.Value;
        _embeddingGenerator = embeddingGenerator;
        _logger = logger;
    }

    /// <summary>
    /// 获取或创建数据库连接
    /// </summary>
    public async Task<SqliteConnection> GetConnectionAsync()
    {
        if (_connection == null)
        {
            var connectionString = $"Data Source={_options.VectorDbPath};";
            _connection = new SqliteConnection(connectionString);
            await _connection.OpenAsync();
        }

        return _connection;
    }

    /// <summary>
    /// 初始化向量存储（创建表和索引）
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync();

        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS prompt_cache (
                Id TEXT PRIMARY KEY,
                PromptHash TEXT NOT NULL,
                PromptContent TEXT NOT NULL,
                Embedding BLOB,
                CachedResponse TEXT,
                CachedResponseJson TEXT,
                LlmConfigId TEXT,
                ModelName TEXT,
                ModelParametersJson TEXT,
                PromptTokens INTEGER,
                CompletionTokens INTEGER,
                HitCount INTEGER DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                LastAccessedAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL,
                AverageResponseTimeMs REAL,
                SavedTokens INTEGER DEFAULT 0,
                Metadata TEXT
            );
            
            CREATE INDEX IF NOT EXISTS idx_prompt_cache_hash ON prompt_cache(PromptHash);
            CREATE INDEX IF NOT EXISTS idx_prompt_cache_expires ON prompt_cache(ExpiresAt);
            CREATE INDEX IF NOT EXISTS idx_prompt_cache_config ON prompt_cache(LlmConfigId, ModelName);
        ";

        using var command = connection.CreateCommand();
        command.CommandText = createTableSql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation("Prompt cache database initialized");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection?.Dispose();
            _disposed = true;
        }
    }
}
