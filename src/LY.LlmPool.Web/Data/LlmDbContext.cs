using LY.LlmPool.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LY.LlmPool.Web.Data;

public class LlmDbContext : DbContext
{
    public LlmDbContext(DbContextOptions<LlmDbContext> options)
        : base(options)
    {
    }

    public DbSet<LlmModelType> ModelTypes => Set<LlmModelType>();
    public DbSet<LlmConfig> Configs => Set<LlmConfig>();
    public DbSet<LlmEndpoint> Endpoints => Set<LlmEndpoint>();
    public DbSet<LlmEndpointConfig> EndpointConfigs => Set<LlmEndpointConfig>();

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
            entity.HasIndex(e => e.Path).IsUnique();
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
    }
} 