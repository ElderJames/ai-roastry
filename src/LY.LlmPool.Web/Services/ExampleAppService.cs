using System.Text.Json;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LY.LlmPool.Web.Services;

/// <summary>
/// 提供示例应用（Examples）构建能力，便于在演示或测试环境中一键生成 DAG + ReAct Agent App。
/// </summary>
public class ExampleAppService
{
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;
    private readonly ILogger<ExampleAppService> _logger;

    public const string DemoAppName = "ReAct-DAG-Demo";
    public const string PlannerPromptName = "ReAct-Planner-DAG";
    public const string ResearcherPromptName = "ReAct-Researcher-Multi";
    public const string AggregatorPromptName = "ReAct-Aggregator";
    public const string ReviewerPromptName = "ReAct-Reviewer-DAG";

    public ExampleAppService(IDbContextFactory<LlmDbContext> dbContextFactory, ILogger<ExampleAppService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// 确保示例 ReAct DAG 应用存在；若缺失则自动创建所需 Prompt、Agent 成员与 DAG 配置。
    /// </summary>
    /// <param name="preferredConfigName">优先使用的模型配置名称，找不到时回退到首个启用配置。</param>
    public async Task<LlmApp?> EnsureReActDagDemoAsync(string preferredConfigName = "Qwen3-235B", CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var config = await db.Configs
            .AsNoTracking()
            .Where(c => c.IsEnabled)
            .OrderByDescending(c => c.Name == preferredConfigName)
            .ThenBy(c => c.Name)
            .FirstOrDefaultAsync(cancellationToken);

        if (config == null)
        {
            _logger.LogWarning("EnsureReActDagDemoAsync: no enabled LLM configuration found.");
            return null;
        }

        var promptMap = await EnsurePromptsAsync(db, cancellationToken);

        var existingApp = await db.Apps
            .Include(a => a.AgentMembers)
            .FirstOrDefaultAsync(a => a.Name == DemoAppName, cancellationToken);

        if (existingApp != null)
        {
            await RebuildMembersAsync(db, existingApp, config.Id!, promptMap, cancellationToken);
            _logger.LogInformation("EnsureReActDagDemoAsync: updated existing demo app {AppId}", existingApp.Id);
            return existingApp;
        }

        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = DemoAppName,
            Description = "DAG + ReAct 示例：Planner → Researcher 并行 → Aggregator → Reviewer",
            AppType = LlmAppTypes.AgentGroup,
            OrchestrationMode = OrchestrationMode.DAG,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ConfigJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["DAGWorkflow"] = new
                {
                    MaxParallelism = 2,
                    GlobalTimeoutSeconds = 600,
                    FailureStrategy = "stop"
                }
            })
        };
        db.Apps.Add(app);
        await db.SaveChangesAsync(cancellationToken);

        await AddMembersAsync(db, app, config.Id!, promptMap, cancellationToken);
        _logger.LogInformation("EnsureReActDagDemoAsync: created demo app {AppId}", app.Id);
        return app;
    }

    private async Task<Dictionary<string, string>> EnsurePromptsAsync(LlmDbContext db, CancellationToken ct)
    {
        var promptSpecs = new (string Name, string Description, string Content)[]
        {
            (PlannerPromptName, "ReAct Planner for DAG", "你是负责规划任务的 Planner。分析用户请求并输出分解后的步骤列表，注明每步目标与依赖。"),
            (ResearcherPromptName, "ReAct Researcher", "你是研究人员，需要基于 Planner 提供的任务，检索技术与市场信息并总结关键结论。输出结构化要点列表。"),
            (AggregatorPromptName, "ReAct Aggregator", "你负责汇总多个研究结果，提炼洞察并给出综合判断。明确信息来源并指出共同点/差异。"),
            (ReviewerPromptName, "ReAct Reviewer", "你是审核者，需要审视完整方案，指出优点、风险与改进建议，最后给出 FINAL: 开头的结论。")
        };

        var result = new Dictionary<string, string>();
        foreach (var spec in promptSpecs)
        {
            var prompt = await db.Prompts.FirstOrDefaultAsync(p => p.Name == spec.Name, ct);
            if (prompt == null)
            {
                prompt = new LlmPrompt
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = spec.Name,
                    Description = spec.Description,
                    Content = spec.Content,
                    Version = 1,
                    CreateTime = DateTime.UtcNow,
                    UpdateTime = DateTime.UtcNow
                };
                db.Prompts.Add(prompt);
            }
            else
            {
                prompt.Description = spec.Description;
                prompt.Content = spec.Content;
                prompt.UpdateTime = DateTime.UtcNow;
            }
            result[spec.Name] = prompt.Id!;
        }

        await db.SaveChangesAsync(ct);
        return result;
    }

    private async Task RebuildMembersAsync(LlmDbContext db, LlmApp app, string configId, Dictionary<string, string> promptMap, CancellationToken ct)
    {
        var existingMembers = db.AgentMembers.Where(m => m.LlmAppId == app.Id);
        db.AgentMembers.RemoveRange(existingMembers);
        await db.SaveChangesAsync(ct);

        await AddMembersAsync(db, app, configId, promptMap, ct);
    }

    private async Task AddMembersAsync(LlmDbContext db, LlmApp app, string configId, Dictionary<string, string> promptMap, CancellationToken ct)
    {
        var plannerId = Guid.NewGuid().ToString("N");
        var techId = Guid.NewGuid().ToString("N");
        var marketId = Guid.NewGuid().ToString("N");
        var aggregatorId = Guid.NewGuid().ToString("N");
        var reviewerId = Guid.NewGuid().ToString("N");

        var members = new List<AgentMember>
        {
            new()
            {
                Id = plannerId,
                LlmAppId = app.Id!,
                Name = "Planner",
                Role = "planner",
                Order = 1,
                LlmPromptId = promptMap[PlannerPromptName],
                LlmConfigId = configId
            },
            new()
            {
                Id = techId,
                LlmAppId = app.Id!,
                Name = "TechResearcher",
                Role = "researcher.tech",
                Order = 2,
                LlmPromptId = promptMap[ResearcherPromptName],
                LlmConfigId = configId,
                ConfigJson = JsonSerializer.Serialize(new AgentMemberDAGConfig
                {
                    Dependencies = new List<string> { plannerId },
                    ParallelGroup = "research",
                    TimeoutSeconds = 240
                })
            },
            new()
            {
                Id = marketId,
                LlmAppId = app.Id!,
                Name = "MarketResearcher",
                Role = "researcher.market",
                Order = 3,
                LlmPromptId = promptMap[ResearcherPromptName],
                LlmConfigId = configId,
                ConfigJson = JsonSerializer.Serialize(new AgentMemberDAGConfig
                {
                    Dependencies = new List<string> { plannerId },
                    ParallelGroup = "research",
                    TimeoutSeconds = 240
                })
            },
            new()
            {
                Id = aggregatorId,
                LlmAppId = app.Id!,
                Name = "Aggregator",
                Role = "aggregator",
                Order = 4,
                LlmPromptId = promptMap[AggregatorPromptName],
                LlmConfigId = configId,
                ConfigJson = JsonSerializer.Serialize(new AgentMemberDAGConfig
                {
                    Dependencies = new List<string> { techId, marketId },
                    TimeoutSeconds = 180
                })
            },
            new()
            {
                Id = reviewerId,
                LlmAppId = app.Id!,
                Name = "Reviewer",
                Role = "reviewer",
                Order = 5,
                LlmPromptId = promptMap[ReviewerPromptName],
                LlmConfigId = configId,
                ConfigJson = JsonSerializer.Serialize(new AgentMemberDAGConfig
                {
                    Dependencies = new List<string> { aggregatorId },
                    ContinueOnFailure = true,
                    TimeoutSeconds = 180
                })
            }
        };

        db.AgentMembers.AddRange(members);
        app.OrchestrationMode = OrchestrationMode.DAG;
        app.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
