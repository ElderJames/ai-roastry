using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Services.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LY.LlmPool.Web.DebugScripts;

/// <summary>
/// 调试工具：检查 PromptTool 和 App 的关联数据
/// </summary>
public class CheckToolDataScript
{
    public static async Task RunAsync(IServiceProvider serviceProvider)
    {
        var dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<LlmDbContext>>();
        var toolMetadataService = serviceProvider.GetRequiredService<ToolMetadataService>();
        var logger = serviceProvider.GetRequiredService<ILogger<CheckToolDataScript>>();

        await using var db = await dbContextFactory.CreateDbContextAsync();

        Console.WriteLine("=== 1. 检查 Tool Apps ===");
        var toolApps = await db.Apps
            .Where(a => a.AppType == "Tool")
            .Select(a => new { a.Id, a.Name, a.AppType, a.IsEnabled })
            .ToListAsync();

        Console.WriteLine($"找到 {toolApps.Count} 个 Tool Apps:");
        foreach (var app in toolApps)
        {
            Console.WriteLine($"  - ID: {app.Id}, Name: {app.Name}, Enabled: {app.IsEnabled}");
        }

        Console.WriteLine("\n=== 2. 检查 ToolMetadataService 缓存 ===");
        var cachedTools = await toolMetadataService.GetAllToolsAsync();
        Console.WriteLine($"缓存中有 {cachedTools.Count} 个工具:");
        foreach (var tool in cachedTools)
        {
            Console.WriteLine($"  - Name: {tool.Name}, Source: {tool.Source}, SourceId: {tool.SourceId}");
        }

        Console.WriteLine("\n=== 3. 检查 PromptTools 关联 ===");
        var promptTools = await db.PromptTools
            .Include(pt => pt.Prompt)
            .ToListAsync();

        Console.WriteLine($"找到 {promptTools.Count} 个 PromptTool 关联:");
        foreach (var pt in promptTools)
        {
            var promptName = pt.Prompt?.Name ?? "NULL";
            
            // 检查 App 是否存在
            var appExists = await db.Apps.AnyAsync(a => a.Id == pt.ToolId);
            var appName = "NOT FOUND";
            if (appExists)
            {
                var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == pt.ToolId);
                appName = app?.Name ?? "NULL";
            }

            Console.WriteLine($"  - Prompt: {promptName} ({pt.PromptId})");
            Console.WriteLine($"    Tool: {appName} ({pt.ToolId})");
            Console.WriteLine($"    Type: {pt.ToolType}");
            Console.WriteLine($"    App Exists: {appExists}");
            Console.WriteLine();
        }

        Console.WriteLine("\n=== 4. 检查不匹配的数据 ===");
        var missingApps = await db.PromptTools
            .Where(pt => pt.ToolType == Data.Entities.ToolType.Internal)
            .Where(pt => !db.Apps.Any(a => a.Id == pt.ToolId))
            .ToListAsync();

        if (missingApps.Any())
        {
            Console.WriteLine($"发现 {missingApps.Count} 个指向不存在 App 的 PromptTool:");
            foreach (var pt in missingApps)
            {
                Console.WriteLine($"  - PromptId: {pt.PromptId}, ToolId: {pt.ToolId}");
            }
        }
        else
        {
            Console.WriteLine("所有 PromptTool 都正确关联到存在的 App ✓");
        }

        Console.WriteLine("\n=== 5. 模拟 PromptEdit 加载流程 ===");
        var prompts = await db.Prompts.Take(5).ToListAsync();
        foreach (var prompt in prompts)
        {
            Console.WriteLine($"\nPrompt: {prompt.Name} ({prompt.Id})");
            
            var tools = await db.PromptTools
                .Where(pt => pt.PromptId == prompt.Id)
                .ToListAsync();

            Console.WriteLine($"  关联的工具数量: {tools.Count}");
            
            foreach (var pt in tools)
            {
                var sourceType = pt.ToolType == Data.Entities.ToolType.Internal ? "App" : "MCP";
                var value = $"{sourceType}:{pt.ToolId}";
                
                // 检查是否在可用工具列表中
                var matchingTool = cachedTools.FirstOrDefault(t => 
                    $"{t.Source}:{t.SourceId}" == value);
                
                Console.WriteLine($"    {value}");
                Console.WriteLine($"      在缓存中: {(matchingTool != null ? "YES ✓" : "NO ✗")}");
                if (matchingTool != null)
                {
                    Console.WriteLine($"      工具名称: {matchingTool.Name}");
                }
            }
        }
    }
}
