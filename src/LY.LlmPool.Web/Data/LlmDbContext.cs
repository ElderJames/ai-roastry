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
                .WithMany()
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
    }
} 