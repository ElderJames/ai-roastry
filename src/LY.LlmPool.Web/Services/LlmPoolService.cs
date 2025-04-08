using System.Collections.Concurrent;
using LY.LlmPool.Web.Data;
using Microsoft.Extensions.Options;

namespace LY.LlmPool.Web.Services;

public class LlmPoolService
{
    private readonly List<LlmConfig> _configs;

    public LlmPoolService(IOptions<LlmPoolOptions> options)
    {
        _configs = options.Value.Configs;
    }

    public IEnumerable<LlmConfigGroup> GetLlmGroups()
    {
        return _configs
            .GroupBy(x => x.Type)
            .Select(g => new LlmConfigGroup
            {
                Type = g.Key,
                Configs = g.ToList()
            });
    }

    public void AddOrUpdateConfig(LlmConfig config)
    {
        var existingConfig = _configs.FirstOrDefault(x => x.Id == config.Id);
        if (existingConfig != null)
        {
            var index = _configs.IndexOf(existingConfig);
            _configs[index] = config;
        }
        else
        {
            _configs.Add(config);
        }
    }

    public void RemoveConfig(string id)
    {
        var config = _configs.FirstOrDefault(x => x.Id == id);
        if (config != null)
        {
            _configs.Remove(config);
        }
    }

    public async Task<LlmConfig?> GetAvailableLlmAsync(LlmType? type = null, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var availableConfigs = _configs
                .Where(x => x.IsEnabled && !x.IsBusy && (type == null || x.Type == type))
                .ToList();

            if (availableConfigs.Any())
            {
                var selected = availableConfigs[Random.Shared.Next(availableConfigs.Count)];
                selected.IsBusy = true;
                return selected;
            }

            await Task.Delay(100, cancellationToken);
        }

        return null;
    }

    public Task ReleaseLlm(string id)
    {
        var config = _configs.FirstOrDefault(x => x.Id == id);
        if (config != null)
        {
            config.IsBusy = false;
        }
        return Task.CompletedTask;
    }
} 