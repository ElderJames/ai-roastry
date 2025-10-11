using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Services.Aggregation;
using Microsoft.EntityFrameworkCore;
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
    /// 待完善
    /// </summary>
    /// <param name="serverId"></param>
    /// <param name="serverConfig"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public async Task CreateServerAsync(string serverId, McpServerConfigDto serverConfig)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(serverId))
                throw new ArgumentException("Server ID is required", nameof(serverId));

            if (serverConfig == null)
                throw new ArgumentNullException(nameof(serverConfig));

            // Check if server ID already exists
            if (await ServerExistsAsync(serverId))
                throw new ArgumentException($"Server with ID '{serverId}' already exists");

            // Validate the configuration
            if (!ValidateServerConfig(serverConfig))
            {
                var errors = GetValidationErrors(serverConfig);
                throw new ArgumentException($"Invalid configuration: {string.Join(", ", errors)}");
            }
            //更新数据库 todo
            //await UpdateConfigurationFileAsync(serverId, serverConfig);

            // Add only the new MCP client for this server
            _logger.LogInformation("Adding MCP client for new server {ServerId}", serverId);
            await _mcpClientsFactory.AddClientAsync(serverId);

            _logger.LogInformation("Created MCP server {ServerId}", serverId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating MCP server {ServerId}", serverId);
            throw;
        }
    }
    /// <summary>
    /// 待完善
    /// </summary>
    /// <param name="serverId"></param>
    /// <param name="serverConfig"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public async Task UpdateServerAsync(string serverId, McpServerConfigDto serverConfig)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(serverId))
                throw new ArgumentException("Server ID is required", nameof(serverId));

            if (serverConfig == null)
                throw new ArgumentNullException(nameof(serverConfig));

            // Check if server exists
            if (!await ServerExistsAsync(serverId))
                throw new ArgumentException($"Server with ID '{serverId}' not found");

            // Validate the configuration
            if (!ValidateServerConfig(serverConfig))
            {
                var errors = GetValidationErrors(serverConfig);
                throw new ArgumentException($"Invalid configuration: {string.Join(", ", errors)}");
            }
            //更新数据库 todo
            //await UpdateConfigurationFileAsync(serverId, serverConfig);

            // Update only the specific MCP client for this server
            _logger.LogInformation("Updating MCP client for server {ServerId}", serverId);
            await _mcpClientsFactory.UpdateClientAsync(serverId);

            _logger.LogInformation("Updated MCP server {ServerId}", serverId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating MCP server {ServerId}", serverId);
            throw;
        }
    }
    /// <summary>
    /// 待完善
    /// </summary>
    /// <param name="serverId"></param>
    /// <param name="serverConfig"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public async Task DeleteServerAsync(string serverId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(serverId))
                throw new ArgumentException("Server ID is required", nameof(serverId));

            // Check if server exists
            if (!await ServerExistsAsync(serverId))
                throw new ArgumentException($"Server with ID '{serverId}' not found");
            //更新数据库 todo
            //await RemoveServerFromConfigurationAsync(serverId);

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
    /// 待完善
    /// </summary>
    /// <param name="serverId"></param>
    /// <param name="serverConfig"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
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
            var mcpServerConfigs = await dbContext.McpServerConfigs.AsNoTracking().ToListAsync();
            var mcpServerConfigDtos = _mcpClientsFactory.ASMcpServerConfig(mcpServerConfigs);

            // Update the server status
            if (mcpServerConfigDtos.ContainsKey(serverId))
            {
                //更新数据库 todo
                mcpServerConfigDtos[serverId].Enabled = enabled;

                // Serialize and write back to file
                var json = JsonSerializer.Serialize(mcpServerConfigDtos, GetJsonSerializerOptions());

               
            }
            else
            {
                throw new ArgumentException($"Server '{serverId}' not found in configuration");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to update server status in configuration file: {ex.Message}", ex);
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
  
}