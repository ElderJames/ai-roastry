using LY.LlmPool.Web.Data.Entities;

namespace LY.LlmPool.Web.Data;

public class LlmConfigGroup
{
    public string ModelTypeId { get; set; } = string.Empty;
    public List<LlmConfig> Configs { get; set; } = new();
} 