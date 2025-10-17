using LY.LlmPool.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LY.LlmPool.Web.Data;

public class LlmDbContext : DbContext
{
    public LlmDbContext(DbContextOptions<LlmDbContext> options)
        : base(options)
    {
    }

    public DbSet<LlmModelType> ModelTypes { get; set; } = null!;
    public DbSet<LlmConfig> Configs { get; set; } = null!;
    public DbSet<LlmEndpoint> Endpoints { get; set; } = null!;
    public DbSet<LlmEndpointConfig> EndpointConfigs { get; set; } = null!;
    public DbSet<EndpointCallRecord> EndpointCallRecords { get; set; } = null!;
    public DbSet<LlmPrompt> Prompts { get; set; } = null!;
    public DbSet<LlmPromptHistory> PromptHistory { get; set; } = null!;
    public DbSet<LlmApp> Apps { get; set; } = null!;

    public DbSet<AgentMember> AgentMembers { get; set; } = null!;
    public DbSet<McpServerConfig> McpServerConfigs { get; set; } = null!;
    public DbSet<PromptTool> PromptTools { get; set; } = null!;

    public DbSet<ChatExecutionRecord> ChatExecutionRecords { get; set; } = null!;
    public DbSet<ChatExecutionTimelineNode> ChatExecutionTimelineNodes { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<LlmModelType>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<LlmConfig>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            
            entity.HasOne(e => e.ModelType)
                .WithMany(e => e.Configs)
                .HasForeignKey(e => e.ModelTypeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LlmEndpoint>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<LlmEndpointConfig>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Endpoint)
                .WithMany(e => e.EndpointConfigs)
                .HasForeignKey(e => e.EndpointId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.LlmConfig)
                .WithMany(e => e.EndpointConfigs)
                .HasForeignKey(e => e.LlmConfigId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.EndpointId, e.LlmConfigId }).IsUnique();
        });

        modelBuilder.Entity<EndpointCallRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RequestReceivedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            
            entity.HasOne(e => e.Endpoint)
                .WithMany()
                .HasForeignKey(e => e.EndpointId)
                .OnDelete(DeleteBehavior.Restrict);
                
            entity.HasOne(e => e.LlmConfig)
                .WithMany()
                .HasForeignKey(e => e.LlmConfigId)
                .OnDelete(DeleteBehavior.SetNull);
                
            entity.HasOne(e => e.ParentCall)
                .WithMany(e => e.ChildCalls)
                .HasForeignKey(e => e.ParentCallId)
                .OnDelete(DeleteBehavior.Restrict);
                
            entity.HasIndex(e => e.EndpointId);
            entity.HasIndex(e => e.LlmConfigId);
            entity.HasIndex(e => e.RequestReceivedAt);
            entity.HasIndex(e => e.ParentCallId);
        });

        modelBuilder.Entity<LlmPrompt>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.CreateTime).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdateTime).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<LlmPromptHistory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CreateTime).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Prompt)
                .WithMany()
                .HasForeignKey(e => e.PromptId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.PromptId);
            entity.HasIndex(e => e.CreateTime);
        });

        modelBuilder.Entity<LlmApp>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            // AppType is string now; no conversion needed
            // OrchestrationMode 存为字符串，但容错：无法解析的旧值映射为 null，避免异常
            entity.Property(e => e.OrchestrationMode)
                .HasConversion(
                    v => v.HasValue ? v.Value.ToString() : null,
                    v => v == null
                        ? (OrchestrationMode?)null
                        : (v == nameof(OrchestrationMode.Sequential)
                            ? OrchestrationMode.Sequential
                            : (v == nameof(OrchestrationMode.GroupChat)
                                ? OrchestrationMode.GroupChat
                                : (OrchestrationMode?)null))
                );
        });

        modelBuilder.Entity<AgentMember>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.LlmApp)
                .WithMany(e => e.AgentMembers)
                .HasForeignKey(e => e.LlmAppId);
        });

        modelBuilder.Entity<McpServerConfig>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
        });

        modelBuilder.Entity<PromptTool>(entity =>
        {
            entity.HasKey(e => new { e.PromptId, e.ToolId, e.ToolType });
            entity.HasOne(e => e.Prompt)
                .WithMany(e => e.PromptTools)
                .HasForeignKey(e => e.PromptId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(e => e.ToolType).HasConversion<string>();
        });

        modelBuilder.Entity<ChatExecutionRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.RequestId).IsUnique();
            entity.HasIndex(e => e.StartTime);
            entity.HasIndex(e => e.ModelName);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<ChatExecutionTimelineNode>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ExecutionRecordId);
            entity.HasIndex(e => new { e.ExecutionRecordId, e.Sequence });
            entity.Property(e => e.NodeType).HasConversion<string>();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.ExecutionRecord)
                .WithMany(e => e.TimelineNodes)
                .HasForeignKey(e => e.ExecutionRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
