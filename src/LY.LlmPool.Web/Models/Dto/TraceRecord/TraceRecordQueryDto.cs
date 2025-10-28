using System.ComponentModel.DataAnnotations;

namespace LY.LlmPool.Web.Models.Dto;

/// <summary>
/// 追踪记录查询参数
/// </summary>
public class TraceRecordQueryDto
{
    /// <summary>
    /// 名称（必填，来自 TraceNode.Name）
    /// </summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 开始时间范围 - 起始
    /// </summary>
    public DateTime? StartTimeFrom { get; set; }

    /// <summary>
    /// 开始时间范围 - 结束
    /// </summary>
    public DateTime? StartTimeTo { get; set; }

    /// <summary>
    /// 状态筛选（可选）
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// Trace ID 筛选（支持部分匹配）
    /// </summary>
    public string? TraceId { get; set; }

    /// <summary>
    /// 页码（从 1 开始）
    /// </summary>
    [Range(1, int.MaxValue)]
    public int PageIndex { get; set; } = 1;

    /// <summary>
    /// 每页大小
    /// </summary>
    [Range(1, 100)]
    public int PageSize { get; set; } = 20;
}
