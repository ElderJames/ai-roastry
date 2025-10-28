using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using LY.LlmPool.Web.Services.Telemetry;
using LY.LlmPool.Web.Models.Dto;
using AntDesign;

namespace LY.LlmPool.Web.Components.Pages;

public partial class TraceRecordDetail
{
    [Inject]
    private ITraceRecordService TraceRecordService { get; set; } = null!;

    [Inject]
    private IMessageService MessageService { get; set; } = null!;

    [Inject]
    private ILogger<TraceRecordDetail> Logger { get; set; } = null!;

    [Inject]
    private IJSRuntime JSRuntime { get; set; } = null!;

    [Parameter]
    public bool Visible { get; set; }

    [Parameter]
    public TraceNode? Node { get; set; }

    [Parameter]
    public EventCallback<bool> VisibleChanged { get; set; }

    // 数据状态
    private bool _loading = false;
    private List<TraceRecordDto> _records = new();
    private int _totalCount = 0;
    private int _pageIndex = 1;
    private int _pageSize = 20;

    // 筛选条件
    private DateTime?[]? _dateRange = null;
    private string? _statusFilter = null;
    private string? _traceIdFilter = null;

    // 下拉选项
    private readonly List<string> _statusOptions = new()
    {
        "Success",
        "Error",
        "Completed",
        "Pending",
        "InProgress"
    };

    // ==================== 列可见性控制 ====================
    /// <summary>
    /// 列可见性配置的本地存储键
    /// </summary>
    private const string ColumnVisibilityStorageKey = "TraceRecordDetail_ColumnVisibility";

  
    /// <summary>
    /// 列可见性状态
    /// Column visibility state for user customization
    /// </summary>
    private ColumnVisibilityState _columnVisibility = new();

    /// <summary>
    /// 列可见性状态类
    /// Manages which columns are visible in the table
    /// </summary>
    private class ColumnVisibilityState
    {
        public bool ShowTraceId { get; set; } = true;
        public bool ShowConversationId { get; set; } = true;
        public bool ShowStartTime { get; set; } = true;
        public bool ShowEndTime { get; set; } = true;
        public bool ShowDuration { get; set; } = true;
        public bool ShowStatus { get; set; } = true;
        public bool ShowInputMessages { get; set; } = true;
        public bool ShowInputParameters { get; set; } = true;
        public bool ShowOutputContent { get; set; } = true;
    }

    /// <summary>
    /// 组件初始化时加载列可见性配置
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        await LoadColumnVisibilityAsync();
    }

    /// <summary>
    /// 参数变化时触发（Modal 打开时加载数据）
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        // 只在 Modal 打开且 Node 不为空时加载数据
        if (Visible && Node != null && !_loading)
        {
            await LoadDataAsync();
        }
    }

    /// <summary>
    /// 加载数据
    /// </summary>
    private async Task LoadDataAsync()
    {
        if (Node == null || string.IsNullOrWhiteSpace(Node.Name))
        {
            Logger.LogWarning("⚠️ LoadDataAsync: Node 或 Node.Name 为空");
            return;
        }

        _loading = true;
        StateHasChanged();

        try
        {
            var query = new TraceRecordQueryDto
            {
                Name = Node.Name,
                StartTimeFrom = _dateRange?[0],
                StartTimeTo = _dateRange?[1],
                Status = _statusFilter,
                TraceId = _traceIdFilter,
                PageIndex = _pageIndex,
                PageSize = _pageSize
            };

            var result = await TraceRecordService.GetPagedRecordsAsync(query);

            _records = result.Items;
            _totalCount = result.TotalCount;

            Logger.LogInformation(
                "✅ LoadDataAsync 成功: Name={Name}, TotalCount={TotalCount}",
                Node.Name, _totalCount);
        }
        catch (Exception ex)
        {
            var errorMsg = ex.Message.Length > 50
                ? ex.Message.Substring(0, 50)
                : ex.Message;

            await MessageService.ErrorAsync(errorMsg);
            Logger.LogError(ex, "❌ 加载追踪记录失败: Name={Name}", Node.Name);

            _records = new List<TraceRecordDto>();
            _totalCount = 0;
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// 处理查询按钮点击
    /// </summary>
    private async Task HandleSearch()
    {
        _pageIndex = 1; // 重置为第一页
        await LoadDataAsync();
    }

    /// <summary>
    /// 处理重置按钮点击
    /// </summary>
    private async Task HandleReset()
    {
        _dateRange = null;
        _statusFilter = null;
        _traceIdFilter = null;
        _pageIndex = 1;
        _pageSize = 20;

        await LoadDataAsync();
    }

    /// <summary>
    /// 处理分页变化
    /// </summary>
    private async Task HandlePageChange(PaginationEventArgs args)
    {
        _pageIndex = args.Page;
        _pageSize = args.PageSize;

        await LoadDataAsync();
    }

    /// <summary>
    /// 处理取消按钮
    /// </summary>
    private async Task HandleCancel()
    {
        Visible = false;
        await VisibleChanged.InvokeAsync(Visible);

        // 清空筛选条件（可选）
        _dateRange = null;
        _statusFilter = null;
        _traceIdFilter = null;
        _pageIndex = 1;
        _records = new List<TraceRecordDto>();
        _totalCount = 0;
    }

    /// <summary>
    /// 截断内容用于表格显示
    /// </summary>
    private static string TruncateContent(string? content, int maxLength = 50)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        return content.Length <= maxLength
            ? content
            : content.Substring(0, maxLength) + "...";
    }

    /// <summary>
    /// 根据状态获取颜色
    /// </summary>
    private static string GetStatusColor(string status)
    {
        return status switch
        {
            "Success" or "Completed" => "green",
            "Error" => "red",
            "Pending" => "orange",
            "InProgress" => "blue",
            _ => "default"
        };
    }

    /// <summary>
    /// 分页显示总数格式化函数
    /// </summary>
    private RenderFragment<PaginationTotalContext> ShowTotalFormat => context => __builder =>
    {
        __builder.AddContent(0, $"共 {context.Total} 条记录");
    };
     

    // ==================== 列可见性管理 ====================

    /// <summary>
    /// 从本地存储加载列可见性配置
    /// Loads column visibility preferences from localStorage
    /// </summary>
    private async Task LoadColumnVisibilityAsync()
    {
        try
        {
            var json = await JSRuntime.InvokeAsync<string>("localStorage.getItem", ColumnVisibilityStorageKey);
            if (!string.IsNullOrEmpty(json))
            {
                var loaded = JsonSerializer.Deserialize<ColumnVisibilityState>(json);
                if (loaded != null)
                {
                    _columnVisibility = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "加载列可见性配置失败，使用默认配置");
        }
    }

    /// <summary>
    /// 保存列可见性配置到本地存储
    /// Saves column visibility preferences to localStorage
    /// </summary>
    private async Task SaveColumnVisibilityAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_columnVisibility);
            await JSRuntime.InvokeVoidAsync("localStorage.setItem", ColumnVisibilityStorageKey, json);
            await MessageService.SuccessAsync("列设置已保存");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "保存列可见性配置失败");
            await MessageService.ErrorAsync("保存设置失败");
        }
    }

    
    /// <summary>
    /// 重置所有列为可见
    /// </summary>
    private async Task ResetColumnVisibility()
    {
        _columnVisibility = new ColumnVisibilityState();
        await SaveColumnVisibilityAsync();
    }
}
