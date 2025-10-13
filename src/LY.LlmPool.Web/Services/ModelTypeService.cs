using System.Collections.Concurrent;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LY.LlmPool.Web.Services;

public class ModelTypeService
{
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;

    public ModelTypeService(IDbContextFactory<LlmDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<List<LlmModelType>> GetModelTypesAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.ModelTypes
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync();
    }

    public async Task<LlmModelType?> GetModelTypeByIdAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.ModelTypes.FindAsync(id);
    }

    public async Task<LlmModelType> AddModelTypeAsync(LlmModelType modelType)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        dbContext.ModelTypes.Add(modelType);
        await dbContext.SaveChangesAsync();
        return modelType;
    }

    public async Task<LlmModelType> UpdateModelTypeAsync(LlmModelType modelType)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var existing = await dbContext.ModelTypes.FindAsync(modelType.Id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Model type with ID {modelType.Id} not found.");
        }

        existing.Name = modelType.Name;
        existing.Description = modelType.Description;
        existing.Icon = modelType.Icon;
        existing.DefaultEndpoint = modelType.DefaultEndpoint;
        existing.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteModelTypeAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var modelType = await dbContext.ModelTypes
            .Include(x => x.Configs)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (modelType == null)
        {
            throw new KeyNotFoundException($"Model type with ID {id} not found.");
        }

        if (modelType.Configs.Any())
        {
            throw new InvalidOperationException("Cannot delete model type that has configurations.");
        }

        dbContext.ModelTypes.Remove(modelType);
        await dbContext.SaveChangesAsync();
    }
}
