using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Net.Http;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol;

namespace LY.LlmPool.Web.Services;

public class McpServerConfigService
{
    // DTO returned to UI containing richer tool information
    public record ToolInfo(string Id, string Name, string? Description, string? JsonSchema);

    private readonly LlmDbContext _db;
    private readonly ILogger<McpServerConfigService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IMcpClientFactory? _mcpClientFactory;

    public McpServerConfigService(LlmDbContext db, ILogger<McpServerConfigService> logger, ILoggerFactory loggerFactory, IHttpClientFactory? httpClientFactory = null, IMcpClientFactory? mcpClientFactory = null)
    {
        _db = db;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _httpClientFactory = httpClientFactory;
        _mcpClientFactory = mcpClientFactory;
    }

    // Backwards-compatible constructor used by tests: (db, logger, httpClientFactory)
    public McpServerConfigService(LlmDbContext db, ILogger<McpServerConfigService> logger, IHttpClientFactory httpClientFactory)
        : this(db, logger, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, httpClientFactory, null)
    {
    }

    public async Task<McpServerConfig> CreateAsync(McpServerConfig input, CancellationToken ct = default)
    {
        input.Id = Guid.NewGuid().ToString("N");
        _db.McpServerConfigs.Add(input);
        await _db.SaveChangesAsync(ct);
        return input;
    }

    public async Task<McpServerConfig?> GetAsync(string id, CancellationToken ct = default)
    {
        return await _db.McpServerConfigs.FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<List<McpServerConfig>> ListAsync(string? search = null, int skip = 0, int take = 50, CancellationToken ct = default)
    {
        var q = _db.McpServerConfigs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(x => x.Name.Contains(s) || x.Description.Contains(s));
        }
        return await q.OrderBy(x => x.Name).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
    }

    public async Task<bool> UpdateAsync(McpServerConfig updated, CancellationToken ct = default)
    {
        var entity = await _db.McpServerConfigs.FirstOrDefaultAsync(x => x.Id == updated.Id, ct);
        if (entity == null) return false;
        entity.Name = updated.Name;
        entity.Url = updated.Url;
        entity.Description = updated.Description;
        entity.SchemaCacheJson = updated.SchemaCacheJson;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var entity = await _db.McpServerConfigs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity == null) return false;
        _db.McpServerConfigs.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Fetch MCP server schema/proto JSON from known endpoints and cache it into SchemaCacheJson.
    /// Returns (toolsCount, promptsCount) parsed from the fetched JSON.
    /// </summary>
    public async Task<(int tools, int prompts)> FetchAndCacheSchemaAsync(string id, CancellationToken ct = default)
    {
        var cfg = await _db.McpServerConfigs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (cfg == null) throw new InvalidOperationException("MCP config not found");

        var baseUrl = cfg.Url?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("MCP Url is empty");

        string? content = null;

        // Use the official SDK only (no HTTP fallback). Build an HttpClient and ask the factory to create an SDK adapter.
        try
        {
            var clientHttp = _httpClientFactory?.CreateClient() ?? new System.Net.Http.HttpClient();
            clientHttp.BaseAddress = new Uri(baseUrl);
            clientHttp.Timeout = TimeSpan.FromMinutes(5);

            IMcpClient sdkClient;
            if (_mcpClientFactory != null)
            {
                sdkClient = await _mcpClientFactory.CreateAsync(clientHttp, ct).ConfigureAwait(false);
            }
            else
            {
                // default factory
                var defaultFactory = new McpSdkClientFactory(_loggerFactory);
                sdkClient = await defaultFactory.CreateAsync(clientHttp, ct).ConfigureAwait(false);
            }

            await using (sdkClient.ConfigureAwait(false))
            {
                var tools = new List<object>();
                try
                {
                    await foreach (var t in sdkClient.EnumerateToolsAsync(McpJsonUtilities.DefaultOptions, ct))
                    {
                        try
                        {
                            tools.Add(new
                            {
                                id = t.Name ?? string.Empty,
                                name = string.IsNullOrWhiteSpace(t.Title) ? (t.Name ?? string.Empty) : t.Title,
                                description = t.Description,
                                json_schema = t.JsonSchema
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to enumerate tool {Tool} from MCP {Id}", t?.Name, id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // If enumeration fails part-way (or immediately), log but continue to attempt prompts
                    _logger.LogDebug(ex, "Failed to enumerate tools from MCP {Id}", id);
                }

                var prompts = new List<object>();
                try
                {
                    var ps = await sdkClient.ListPromptsAsync(ct).ConfigureAwait(false);
                    foreach (var p in ps)
                    {
                        prompts.Add(new { name = p.Name, title = p.Title, description = p.Description });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to list prompts from MCP {Id}", id);
                }

                var wrapper = new { tools, prompts };
                content = JsonSerializer.Serialize(wrapper, McpJsonUtilities.DefaultOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SDK-based MCP discovery failed for MCP {Id}", id);
            throw new InvalidOperationException("Failed to fetch MCP schema/proto from server via SDK.", ex);
        }

        if (string.IsNullOrEmpty(content))
        {
            throw new InvalidOperationException("Failed to fetch MCP schema/proto from server.");
        }

        cfg.SchemaCacheJson = content;
        await _db.SaveChangesAsync(ct);

        return ParseCounts(content);
    }

    public static (int tools, int prompts) ParseCounts(string json)
    {
        return McpSchemaParser.ParseCounts(json);
    }

    /// <summary>
    /// Parse cached SchemaCacheJson and return a simple list of tools (id,name) if present.
    /// </summary>
    public async Task<List<(string id, string name)>> GetToolsAsync(string id, CancellationToken ct = default)
    {
        var cfg = await _db.McpServerConfigs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (cfg == null) return new List<(string id, string name)>();
        if (string.IsNullOrWhiteSpace(cfg.SchemaCacheJson))
        {
            // Attempt to fetch schema if not present
            try
            {
                await FetchAndCacheSchemaAsync(id, ct);
            }
            catch
            {
                // ignore fetch errors and fall through to return empty
            }
        }

        return McpSchemaParser.GetTools(cfg.SchemaCacheJson ?? string.Empty);
    }

    /// <summary>
    /// Return detailed tool information (id, name, description, json_schema) parsed from cached SchemaCacheJson.
    /// Ensures schema is fetched/cached if missing.
    /// </summary>
    public async Task<List<ToolInfo>> GetToolsDetailedAsync(string id, CancellationToken ct = default)
    {
        var cfg = await _db.McpServerConfigs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (cfg == null) return new List<ToolInfo>();

        if (string.IsNullOrWhiteSpace(cfg.SchemaCacheJson))
        {
            try
            {
                await FetchAndCacheSchemaAsync(id, ct);
                // reload cfg to pick up changes
                cfg = await _db.McpServerConfigs.FirstOrDefaultAsync(x => x.Id == id, ct);
            }
            catch
            {
                // ignore fetch errors and fall through to parsing if any
            }
        }

    // map internal parser's ToolInfo to the service's ToolInfo record
    var schemaJson = cfg?.SchemaCacheJson ?? string.Empty;
    var parsed = McpSchemaParser.GetToolsDetailed(schemaJson);
        var mapped = new List<ToolInfo>();
        foreach (var p in parsed)
        {
            mapped.Add(new ToolInfo(p.Id, p.Name, p.Description, p.JsonSchema));
        }
        return mapped;
    }
}
