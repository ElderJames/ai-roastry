using System.Diagnostics;

namespace LY.LlmPool.Web.Services.Telemetry;

/// <summary>
/// OTLP 解析器 - 简化版
/// 由于 Protobuf 类的访问级别问题,当前仅支持 JSON 格式
/// 如需 Protobuf 支持,需要生成自定义的 Protobuf 类
/// </summary>
public class OtlpTraceParser
{
    private readonly ILogger<OtlpTraceParser> _logger;

    public OtlpTraceParser(ILogger<OtlpTraceParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 解析 OTLP Protobuf 数据
    /// 注意: 当前未实现,请使用 JSON 端点
    /// </summary>
    public List<ExternalActivityDto> ParseProtobuf(byte[] protobufData)
    {
        _logger.LogWarning("⚠️ Protobuf 解析未实现,请使用 /v1/traces/json 端点");
        throw new NotImplementedException(
            "Protobuf parsing is not implemented yet. " +
            "Please use JSON endpoint (/v1/traces/json) or configure TestMcpServer to use HTTP/JSON format.");
    }
}
