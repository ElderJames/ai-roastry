using HandlebarsDotNet;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services.Aggregation;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace LY.LlmPool.Web.Services;

/// <summary>
/// Service implementation for managing MCP server configurations.
/// </summary>
public class McpServerService : IMcpServerService
{ 
    private readonly ILogger<McpServerService> _logger;
    private readonly McpClientsFactory _mcpClientsFactory;
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;

    public McpServerService(
        IDbContextFactory<LlmDbContext> dbContextFactory,
        ILogger<McpServerService> logger,
        McpClientsFactory mcpClientsFactory)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _mcpClientsFactory = mcpClientsFactory;
    }

    public async Task<McpServerInfo[]> GetAllServersAsync()
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            var mcpServerConfigs = await dbContext.McpServerConfigs.AsNoTracking().ToListAsync();
            var mcpServerConfigDtos = _mcpClientsFactory.ASMcpServerConfig(mcpServerConfigs);

            var serverData = mcpServerConfigDtos.Select(kvp => new McpServerInfo
            {
                ServerId = kvp.Key,
                Name = kvp.Value.Name ?? "",
                Type = kvp.Value.Type ?? "stdio",
                Enabled = kvp.Value.Enabled ?? true,
                Command = kvp.Value.Command,
                Url = kvp.Value.Url,
                Args = kvp.Value.Args,
                EnvCount = kvp.Value.Env?.Count ?? 0,
                Environment = kvp.Value.Env,
                HeadersCount = kvp.Value.Headers?.Count ?? 0,
                Headers = kvp.Value.Headers,
                IsValid = kvp.Value.IsValid(),
                ValidationErrors = kvp.Value.GetValidationErrors().ToArray(),
                Configuration = GetConfigurationSummary(kvp.Value)
            }).ToArray();

            return serverData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving MCP servers");
            throw;
        }
    } 
    /// <summary>
    /// 
    /// </summary>
    /// <param name="serverId"></param>
    /// <param name="serverConfig"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public async Task CreateServerAsync(McpServerConfigDto serverConfig)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        try
        {
            if (serverConfig == null)
                throw new ArgumentNullException(nameof(serverConfig));
             
            // Validate the configuration
            if (!ValidateServerConfig(serverConfig))
            {
                var errors = GetValidationErrors(serverConfig);
                throw new ArgumentException($"Invalid configuration: {string.Join(", ", errors)}");
            }
            //更新数据库 todo
            var data = AsMcpServerConfigDto(serverConfig);
           
            dbContext.Add(data);
            
            // Add only the new MCP client for this server
            _logger.LogInformation("Adding MCP client for new server {ServerId}", serverConfig.Id);
            await _mcpClientsFactory.AddClientAsync(serverConfig.Id);

            await dbContext.SaveChangesAsync();
            _logger.LogInformation("Created MCP server {Name}:{ServerId}", serverConfig.Id, serverConfig.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating MCP server {Name}", serverConfig.Name);
            throw;
        }
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="serverId"></param>
    /// <param name="serverConfig"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public async Task UpdateServerAsync(McpServerConfigDto serverConfig)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        try
        { 
            var entity = await dbContext.McpServerConfigs.FirstOrDefaultAsync(x => x.Id == serverConfig.Id);
            if (string.IsNullOrWhiteSpace(serverConfig.Id))
                throw new ArgumentException("Server ID is required", nameof(serverConfig.Id));

            if (entity == null)
                throw new ArgumentNullException(nameof(entity));
             
            // Validate the configuration
            if (!ValidateServerConfig(serverConfig))
            {
                var errors = GetValidationErrors(serverConfig);
                throw new ArgumentException($"Invalid configuration: {string.Join(", ", errors)}");
            }
            var data = AsMcpServerConfigDto(serverConfig);
            entity.Name = data.Name;
            entity.Type = data.Type;
            entity.Command = data.Command;
            entity.Url = data.Url;
            entity.Args = data.Args;
            entity.Env = data.Env;
            entity.Headers = data.Headers;
            entity.IsEnabled = data.IsEnabled;
            entity.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
            // Update only the specific MCP client for this server
            _logger.LogInformation("Updating MCP client for server {Name}:{ServerId}", serverConfig.Id, serverConfig.Name);
            await _mcpClientsFactory.UpdateClientAsync(serverConfig.Id);
             
            _logger.LogInformation("Updated MCP server {Name}:{ServerId}", serverConfig.Id, serverConfig.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating MCP server {ServerId}", serverConfig.Id);
            throw;
        }
    }
    /// <summary>
    ///
    /// </summary>
    /// <param name="serverId"></param>
    /// <param name="serverConfig"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public async Task DeleteServerAsync(string serverId)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(); 
        try
        {
            if (string.IsNullOrWhiteSpace(serverId))
                throw new ArgumentException("Server ID is required", nameof(serverId));
             
            //更新数据库 
            var entity = await dbContext.McpServerConfigs.FirstOrDefaultAsync(x => x.Id == serverId);
            if (entity == null) 
                throw new ArgumentException($"Server with Name '{entity?.Name}' not found");
            dbContext.McpServerConfigs.Remove(entity);
            await dbContext.SaveChangesAsync();

            // Remove only the specific MCP client for this server
            _logger.LogInformation("Removing MCP client for deleted server {ServerId}", serverId);
            await _mcpClientsFactory.RemoveClientAsync(serverId);
           
            _logger.LogInformation("Deleted MCP server {ServerId}", serverId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting MCP server {ServerId}", serverId);
            throw;
        }
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="serverId"></param>
    /// <param name="enabled"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public async Task ToggleServerStatusAsync(string serverId, bool enabled)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(serverId))
                throw new ArgumentException("Server ID is required", nameof(serverId));

            // Check if server exists
            if (!await ServerExistsAsync(serverId))
                throw new ArgumentException($"Server with ID '{serverId}' not found");

            await UpdateServerStatusAsync(serverId, enabled);

            // Update only the specific MCP client for this server (enable/disable)
            _logger.LogInformation("Updating MCP client status for server {ServerId} to {Enabled}", serverId, enabled);
            await _mcpClientsFactory.UpdateClientAsync(serverId);

            _logger.LogInformation("Toggled MCP server {ServerId} status to {Enabled}", serverId, enabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling MCP server {ServerId} status", serverId);
            throw;
        }
    }

    public bool ValidateServerConfig(McpServerConfigDto serverConfig)
    {
        return serverConfig?.IsValid() ?? false;
    }

    public IEnumerable<string> GetValidationErrors(McpServerConfigDto serverConfig)
    {
        return serverConfig?.GetValidationErrors() ?? new[] { "Server configuration is null" };
    }

    public async Task<bool> ServerExistsAsync(string serverId)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            var mcpServerConfigs = await dbContext.McpServerConfigs.AsNoTracking().ToListAsync();
            var mcpServerConfigDtos = _mcpClientsFactory.ASMcpServerConfig(mcpServerConfigs);

            return mcpServerConfigDtos.ContainsKey(serverId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if server {ServerId} exists", serverId);
            throw;
        }
    }

    public McpServerConfig AsMcpServerConfigDto(McpServerConfigDto serverConfig)
    {

        return new McpServerConfig
        {
            Id = serverConfig.Id,
            Name = serverConfig.Name ?? "",
            Type = serverConfig.Type ?? "http",
            Command = serverConfig.Command,
            Url = serverConfig.Url ?? "",
            Args = (serverConfig.Args != null && serverConfig.Args.Any()) ? string.Join("\n", serverConfig.Args) : null, //(serverConfig.Args != null && serverConfig.Args.Count() > 0) ? JsonSerializer.Serialize(serverConfig.Args) : null,
            Env = (serverConfig.Env != null && serverConfig.Env.Any()) ? JsonSerializer.Serialize(serverConfig.Env) : null,
            Headers = (serverConfig.Headers != null && serverConfig.Headers.Any()) ? JsonSerializer.Serialize(serverConfig.Headers) : null,
            IsEnabled = serverConfig.Enabled ?? true
        };
    }
    private string GetConfigurationSummary(McpServerConfigDto config)
    {
        if (!string.IsNullOrEmpty(config.Command))
        {
            var args = config.Args?.Length > 0 ? $" {string.Join(" ", config.Args)}" : "";
            return $"{config.Command}{args}";
        }

        if (!string.IsNullOrEmpty(config.Url))
        {
            return config.Url;
        }

        return "Not configured";
    }

    private async Task UpdateServerStatusAsync(string serverId, bool enabled)
    {
        try
        { 
            // Read the current configuration
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            var entity = await dbContext.McpServerConfigs.FirstOrDefaultAsync(x => x.Id == serverId);
            if (string.IsNullOrWhiteSpace(serverId))
                throw new ArgumentException("Server ID is required", nameof(serverId));


            // Update the server status
            if (entity != null)
            {
                //更新数据库 todo
                entity.IsEnabled = enabled;
                await dbContext.SaveChangesAsync();
            }
            else
            {
                throw new ArgumentException($"Server '{serverId}' not found in configuration");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to update server status in configuration: {ex.Message}", ex);
        }
    }
 

    private static JsonSerializerOptions GetJsonSerializerOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }

    public async Task<(int tools, int prompts, int resources)> FetchAndCacheSchemaAsync(string id, CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var entity = await dbContext.McpServerConfigs.FirstOrDefaultAsync(x => x.Id == id);

        if (entity == null) throw new InvalidOperationException("MCP config not found");

        string? content = null;

        var mcpClient = await _mcpClientsFactory.GetMcpClientAsync(id, ct);
        if (mcpClient == null)
        {
            throw new InvalidOperationException($"Client not found for server '{id}'");
        }

        // Use the official SDK only (no HTTP fallback). Build an HttpClient and ask the factory to create an SDK adapter.
        try
        { 
           
            var tools = new List<object>();
            try
            {
                await foreach (var t in mcpClient.EnumerateToolsAsync(McpJsonUtilities.DefaultOptions, ct))
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
                var ps = await mcpClient.ListPromptsAsync(ct).ConfigureAwait(false);
                foreach (var p in ps)
                {
                    prompts.Add(new { name = p.Name, title = p.Title, description = p.Description });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to list prompts from MCP {Id}", id);
            }

            var resources = new List<object>();
            try
            {
                var ps = await mcpClient.ListResourcesAsync(ct).ConfigureAwait(false);
                foreach (var p in ps)
                {
                    resources.Add(new { name = p.Name, title = p.Title, description = p.Description });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to list prompts from MCP {Id}", id);
            }

            var wrapper = new { tools, prompts, resources };
            content = JsonSerializer.Serialize(wrapper, McpJsonUtilities.DefaultOptions);

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

        entity.SchemaCacheJson = content;
        await dbContext.SaveChangesAsync(ct);

        return ParseCounts(content);
    }
    public static (int tools, int prompts,int resources) ParseCounts(string json)
    {
        return McpSchemaParser.McpParseCounts(json);
    }
}