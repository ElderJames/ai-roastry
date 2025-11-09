using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Models.Tools;
using LY.LlmPool.Web.Services.Aggregation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using System.Text.Json;

namespace LY.LlmPool.Web.Services.Tools;

/// <summary>
/// 工具元数据服务 - 管理所有 App Tool 和 MCP Tool 的元数据缓存
/// </summary>
public class ToolMetadataService
{
    private readonly HybridCache _cache;
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;
    private readonly PromptParameterService _promptParameterService;
    private readonly McpClientsFactory _mcpClientsFactory;
    private readonly ILogger<ToolMetadataService> _logger;

    private const string CacheKeyPrefix = "tool_metadata:";
    private const string AllToolsCacheKey = "tool_metadata:all";

    public ToolMetadataService(
        HybridCache cache,
        IDbContextFactory<LlmDbContext> dbContextFactory,
        PromptParameterService promptParameterService,
        McpClientsFactory mcpClientsFactory,
        ILogger<ToolMetadataService> logger)
    {
        _cache = cache;
        _dbContextFactory = dbContextFactory;
        _promptParameterService = promptParameterService;
        _mcpClientsFactory = mcpClientsFactory;
        _logger = logger;
    }

    /// <summary>
    /// 初始化工具元数据缓存 (应用启动时调用)
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing tool metadata cache...");
        var startTime = DateTime.UtcNow;

        try
        {
            // 扫描所有 App Tools
            var appTools = await ScanAppToolsAsync();

            // 扫描所有 MCP Tools (TODO: 集成 MCP SDK 后实现)
            var mcpTools = await ScanMcpToolsAsync();

            // 合并所有工具
            var allTools = appTools.Concat(mcpTools).ToList();

            // 缓存所有工具列表
            await _cache.SetAsync(AllToolsCacheKey, allTools);

            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.LogInformation(
                "Tool metadata cache initialized: {AppToolCount} App Tools, {McpToolCount} MCP Tools, {TotalCount} total tools loaded in {Elapsed}ms",
                appTools.Count, mcpTools.Count, allTools.Count, elapsed
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize tool metadata cache");
            throw;
        }
    }

    /// <summary>
    /// 扫描所有 App Tool 并缓存
    /// </summary>
    public async Task<List<ToolMetadata>> ScanAppToolsAsync()
    {
        _logger.LogDebug("Scanning App Tools...");

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var toolApps = await db.Apps
            .Include(a => a.LlmPrompt)
            .Where(app => app.AppType == "Tool" && app.IsEnabled)
            .ToListAsync();

        var toolMetadataList = new List<ToolMetadata>();

        foreach (var app in toolApps)
        {
            try
            {
                // 提取参数 (从 LlmPrompt.Content) - 支持带描述的参数格式 {{param|description}}
                var promptTemplate = app.LlmPrompt?.Content ?? "";
                var parameters = _promptParameterService.ExtractParameters(promptTemplate);

                // 解析 ConfigJson 获取 parameterOverrides
                Dictionary<string, object>? parameterOverrides = null;
                if (!string.IsNullOrWhiteSpace(app.ConfigJson))
                {
                    var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(app.ConfigJson);
                    if (config != null && config.TryGetValue("parameterOverrides", out var overridesElement))
                    {
                        parameterOverrides = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            overridesElement.GetRawText()
                        );
                    }
                }

                // 生成 JSON Schema
                var schema = _promptParameterService.GenerateParameterSchema(parameters, parameterOverrides);

                // 创建 ToolMetadata
                var metadata = new ToolMetadata
                {
                    Name = app.Name,
                    Description = app.Description ?? $"Tool: {app.Name}",
                    Source = ToolSource.App,
                    SourceId = app.Id ?? string.Empty, // 直接使用字符串 ID
                    ParametersSchema = schema,
                    ConfigJson = app.ConfigJson
                };

                toolMetadataList.Add(metadata);

                // 缓存单个工具
                await _cache.SetAsync($"{CacheKeyPrefix}{app.Name}", metadata);

                _logger.LogDebug("Cached App Tool: {Name} with {ParamCount} parameters", 
                    app.Name, parameters.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to scan App Tool: {AppName} (ID: {AppId})", 
                    app.Name, app.Id);
            }
        }

        _logger.LogInformation("Scanned {Count} App Tools", toolMetadataList.Count);
        return toolMetadataList;
    }

    /// <summary>
    /// 扫描所有 MCP Tool 并缓存
    /// </summary>
    public async Task<List<ToolMetadata>> ScanMcpToolsAsync()
    {
        _logger.LogDebug("Scanning MCP Tools...");
        
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var mcpServers = await db.McpServerConfigs
            .Where(s => s.IsEnabled)
            .ToListAsync();

        var toolMetadataList = new List<ToolMetadata>();

        foreach (var server in mcpServers)
        {
            try
            {
                _logger.LogDebug("Scanning tools from MCP Server: {ServerName} (ID: {ServerId})", 
                    server.Name, server.Id);

                // 获取 MCP Client
                var client = await _mcpClientsFactory.GetMcpClientAsync(server.Id);
                if (client == null)
                {
                    _logger.LogWarning("Failed to get MCP client for server: {ServerName}", server.Name);
                    continue;
                }

                // 获取该 Server 的所有工具
                var tools = await client.ListToolsAsync();
                
                _logger.LogDebug("Found {ToolCount} tools in MCP Server: {ServerName}", 
                    tools.Count, server.Name);

                // 为每个工具创建 ToolMetadata
                foreach (var tool in tools)
                {
                    try
                    {
                        // 将 InputSchema 转换为 JSON 字符串
                        var schemaJson = tool.ProtocolTool.InputSchema.ValueKind != JsonValueKind.Null 
                            && tool.ProtocolTool.InputSchema.ValueKind != JsonValueKind.Undefined
                            ? JsonSerializer.Serialize(tool.ProtocolTool.InputSchema)
                            : "{}";

                        var metadata = new ToolMetadata
                        {
                            Name = tool.ProtocolTool.Name,
                            Description = tool.ProtocolTool.Description ?? $"MCP Tool: {tool.ProtocolTool.Name}",
                            Source = ToolSource.MCP,
                            SourceId = $"{server.Id}:{tool.ProtocolTool.Name}", // 使用 "serverId:toolName" 作为唯一标识
                            ParametersSchema = schemaJson,
                            ConfigJson = server.ConfigJson
                        };

                        toolMetadataList.Add(metadata);

                        // 缓存单个工具
                        await _cache.SetAsync($"{CacheKeyPrefix}{metadata.Name}", metadata);

                        _logger.LogDebug("Cached MCP Tool: {ToolName} from server {ServerName}", 
                            tool.ProtocolTool.Name, server.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process MCP Tool: {ToolName} from server {ServerName}", 
                            tool.ProtocolTool.Name, server.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to scan MCP Server: {ServerName} (ID: {ServerId})", 
                    server.Name, server.Id);
            }
        }

        _logger.LogInformation("Scanned {Count} MCP Tools from {ServerCount} servers", 
            toolMetadataList.Count, mcpServers.Count);
        return toolMetadataList;
    }

    /// <summary>
    /// 获取所有工具 (从缓存)
    /// </summary>
    public async Task<List<ToolMetadata>> GetAllToolsAsync()
    {
        try
        {
            var tools = await _cache.GetOrCreateAsync(
                AllToolsCacheKey,
                async cancel =>
                {
                    _logger.LogWarning("Cache miss for all tools, re-scanning...");
                    await InitializeAsync();
                    return await _cache.GetOrCreateAsync<List<ToolMetadata>>(
                        AllToolsCacheKey,
                        _ => ValueTask.FromResult(new List<ToolMetadata>())
                    );
                }
            );

            return tools ?? new List<ToolMetadata>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all tools from cache");
            return new List<ToolMetadata>();
        }
    }

    /// <summary>
    /// 根据名称获取工具 (从缓存)
    /// </summary>
    public async Task<ToolMetadata?> GetToolByNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            var cacheKey = $"{CacheKeyPrefix}{name}";
            var tool = await _cache.GetOrCreateAsync<ToolMetadata?>(
                cacheKey,
                async cancel =>
                {
                    // Cache miss - 尝试从所有工具中查找
                    var allTools = await GetAllToolsAsync();
                    return allTools.FirstOrDefault(t => 
                        string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                    );
                }
            );

            return tool;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get tool by name: {Name}", name);
            return null;
        }
    }

    /// <summary>
    /// 刷新单个 App Tool 的缓存 (当 LlmApp 更新时调用)
    /// </summary>
    /// <param name="appId">LlmApp 的 ID</param>
    public async Task RefreshAppToolAsync(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            _logger.LogWarning("RefreshAppToolAsync called with empty appId");
            return;
        }

        _logger.LogInformation("Refreshing cache for App Tool with ID: {AppId}", appId);

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var app = await db.Apps
                .Include(a => a.LlmPrompt)
                .FirstOrDefaultAsync(a => a.Id == appId);

            if (app == null)
            {
                _logger.LogWarning("App not found: {AppId}, cache will be refreshed via all tools scan", appId);
                // App 不存在,可能已被删除,刷新整个缓存
                await RefreshAllToolsCacheAsync();
                return;
            }

            // 如果不是 Tool 类型或已禁用,从缓存中移除
            if (app.AppType != "Tool" || !app.IsEnabled)
            {
                _logger.LogInformation("App {AppName} is not a Tool or is disabled, removing from cache", app.Name);
                await RemoveToolByNameAsync(app.Name);
                return;
            }

            // 生成新的 ToolMetadata
            var promptTemplate = app.LlmPrompt?.Content ?? "";
            var parameters = _promptParameterService.ExtractParameters(promptTemplate);

            Dictionary<string, object>? parameterOverrides = null;
            if (!string.IsNullOrWhiteSpace(app.ConfigJson))
            {
                var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(app.ConfigJson);
                if (config != null && config.TryGetValue("parameterOverrides", out var overridesElement))
                {
                    parameterOverrides = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        overridesElement.GetRawText()
                    );
                }
            }

            var schema = _promptParameterService.GenerateParameterSchema(parameters, parameterOverrides);

            var metadata = new ToolMetadata
            {
                Name = app.Name,
                Description = app.Description ?? $"Tool: {app.Name}",
                Source = ToolSource.App,
                SourceId = app.Id ?? string.Empty, // 直接使用字符串 ID
                ParametersSchema = schema,
                ConfigJson = app.ConfigJson
            };

            // 更新单个工具缓存
            await _cache.SetAsync($"{CacheKeyPrefix}{app.Name}", metadata);

            // 刷新所有工具列表缓存
            await RefreshAllToolsCacheAsync();

            _logger.LogInformation("Successfully refreshed cache for App Tool: {AppName}", app.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh App Tool cache for ID: {AppId}", appId);
            throw;
        }
    }

    /// <summary>
    /// 根据工具名称刷新或移除缓存 (当 App 被删除时使用)
    /// </summary>
    /// <param name="toolName">工具名称</param>
    public async Task RefreshAppToolByNameAsync(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            _logger.LogWarning("RefreshAppToolByNameAsync called with empty toolName");
            return;
        }

        _logger.LogInformation("Removing tool from cache by name: {ToolName}", toolName);

        try
        {
            // 直接从缓存中移除该工具
            await RemoveToolByNameAsync(toolName);
            _logger.LogInformation("Successfully removed tool from cache: {ToolName}", toolName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove tool from cache: {ToolName}", toolName);
            throw;
        }
    }

    /// <summary>
    /// 刷新指定 MCP Server 的所有工具缓存 (当 MCP Server 配置更新时调用)
    /// </summary>
    /// <param name="serverId">MCP Server 的 ID</param>
    public async Task RefreshMcpServerAsync(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            _logger.LogWarning("RefreshMcpServerAsync called with empty serverId");
            return;
        }

        _logger.LogInformation("Refreshing cache for MCP Server with ID: {ServerId}", serverId);

        try
        {
            // TODO: 实现 MCP Server 工具刷新逻辑
            // 1. 查询 MCP Server 配置
            // 2. 连接到 MCP Server 获取工具列表
            // 3. 更新缓存中该 Server 的所有工具
            // 4. 刷新所有工具列表缓存

            await Task.CompletedTask;
            _logger.LogWarning("MCP Server refresh not yet implemented for Server ID: {ServerId}", serverId);

            // 暂时刷新整个缓存
            await RefreshAllToolsCacheAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh MCP Server cache for ID: {ServerId}", serverId);
            throw;
        }
    }

    /// <summary>
    /// 刷新所有工具列表缓存
    /// </summary>
    public async Task RefreshAllToolsCacheAsync()
    {
        _logger.LogDebug("Refreshing all tools cache...");

        // 🔥 先移除缓存，确保更新生效（避免 HybridCache 本地缓存导致的延迟）
        await _cache.RemoveAsync(AllToolsCacheKey);

        var appTools = await ScanAppToolsAsync();
        var mcpTools = await ScanMcpToolsAsync();
        var allTools = appTools.Concat(mcpTools).ToList();

        await _cache.SetAsync(AllToolsCacheKey, allTools);

        _logger.LogInformation("All tools cache refreshed: {Count} tools", allTools.Count);
    }

    /// <summary>
    /// 从缓存中移除指定工具
    /// </summary>
    public async Task RemoveToolByNameAsync(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return;
        }

        try
        {
            var cacheKey = $"{CacheKeyPrefix}{toolName}";
            await _cache.RemoveAsync(cacheKey);
            
            // 刷新所有工具列表缓存
            await RefreshAllToolsCacheAsync();

            _logger.LogInformation("Removed tool from cache: {ToolName}", toolName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove tool from cache: {ToolName}", toolName);
        }
    }
}
