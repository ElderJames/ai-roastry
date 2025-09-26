using System.Text.Json;
using System.Diagnostics.Metrics;
using LY.LlmPool.Web.Data.Entities;

namespace LY.LlmPool.Web.Services;

public class CallRecordService
{
    private readonly LlmPoolService _llmPoolService;
    private static readonly Meter _meter = new("LY.LlmPool.Web", "1.0.0");
    private static readonly Histogram<double> _waitSeconds = _meter.CreateHistogram<double>("llmpool.call.wait.seconds", unit: "s", description: "Time spent waiting for available model");
    private static readonly Histogram<double> _processingSeconds = _meter.CreateHistogram<double>("llmpool.call.processing.seconds", unit: "s", description: "Time from model call start to end");
    private static readonly Histogram<double> _totalSeconds = _meter.CreateHistogram<double>("llmpool.call.total.seconds", unit: "s", description: "Total time from request received to response end");

    public CallRecordService(LlmPoolService llmPoolService)
    {
        _llmPoolService = llmPoolService;
    }

    public async Task<EndpointCallRecord> CreateAsync(string endpointId, object? requestData)
    {
        return await _llmPoolService.CreateCallRecordAsync(endpointId, requestData);
    }

    public async Task UpdateWaitAsync(EndpointCallRecord record, TimeSpan wait)
    {
        record.WaitTime = wait;
        await _llmPoolService.UpdateCallRecordAsync(record);
        try { _waitSeconds.Record(wait.TotalSeconds); } catch { }
    }

    public async Task UpdateConfigAsync(EndpointCallRecord record, string? configId)
    {
        record.LlmConfigId = configId;
        await _llmPoolService.UpdateCallRecordAsync(record);
    }

    public async Task MarkStartAsync(EndpointCallRecord record)
    {
        record.ModelCallStartedAt = DateTime.UtcNow;
        await _llmPoolService.UpdateCallRecordAsync(record);
    }

    public async Task MarkResponseStartAsync(EndpointCallRecord record)
    {
        record.ModelResponseStartedAt = DateTime.UtcNow;
        await _llmPoolService.UpdateCallRecordAsync(record);
    }

    public async Task WriteSelectionAsync(EndpointCallRecord record,
        string selectionStrategy,
        string? endpointId,
        string? configId,
        string? configName,
        string? appName,
        string model,
        object? message,
        object? toolCalls,
        DateTime requestReceivedAt)
    {
        record.ResponseData = new
        {
            model,
            message,
            tool_calls = toolCalls,
            selection = new
            {
                strategy = selectionStrategy,
                endpoint_id = endpointId,
                config_id = configId,
                config_name = configName,
                app_name = appName
            },
            timings = new
            {
                request_received_at = requestReceivedAt,
                model_call_started_at = record.ModelCallStartedAt,
                model_response_started_at = record.ModelResponseStartedAt
            }
        };
        await _llmPoolService.UpdateCallRecordAsync(record);
    }

    public async Task FinalizeAsync(EndpointCallRecord record,
        string selectionStrategy,
        string? endpointId,
        string? configId,
        string? configName,
        string? appName,
        string model,
        DateTime requestReceivedAt)
    {
        record.ModelResponseEndedAt = DateTime.UtcNow;
        record.IsSuccessful = true;

        // Preserve any existing stream array if present
        object? existingStream = null;
        if (record.ResponseData is JsonElement je)
        {
            string? modelStr = je.TryGetProperty("model", out var m) ? m.GetString() : model;
            string? messageStr = je.TryGetProperty("message", out var msg) ? msg.GetString() : null;
            object? toolCallsObj = je.TryGetProperty("tool_calls", out var tc) ? (object?)tc : null;
            string? selStrategy = selectionStrategy;
            string? selEndpointId = endpointId;
            string? selConfigId = configId;
            string? selConfigName = configName;
            string? selAppName = appName;
            if (je.TryGetProperty("stream", out var st))
            {
                existingStream = st;
            }
            if (je.TryGetProperty("selection", out var sel) && sel.ValueKind == JsonValueKind.Object)
            {
                selStrategy = sel.TryGetProperty("strategy", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : selStrategy;
                selEndpointId = sel.TryGetProperty("endpoint_id", out var ei) && ei.ValueKind == JsonValueKind.String ? ei.GetString() : selEndpointId;
                selConfigId = sel.TryGetProperty("config_id", out var ci) && ci.ValueKind == JsonValueKind.String ? ci.GetString() : selConfigId;
                selConfigName = sel.TryGetProperty("config_name", out var cn) && cn.ValueKind == JsonValueKind.String ? cn.GetString() : selConfigName;
                selAppName = sel.TryGetProperty("app_name", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString() : selAppName;
            }

            record.ResponseData = new
            {
                model = modelStr,
                message = messageStr,
                tool_calls = toolCallsObj,
                stream = existingStream,
                selection = new { strategy = selStrategy, endpoint_id = selEndpointId, config_id = selConfigId, config_name = selConfigName, app_name = selAppName },
                timings = new
                {
                    request_received_at = requestReceivedAt,
                    model_call_started_at = record.ModelCallStartedAt,
                    model_response_started_at = record.ModelResponseStartedAt,
                    model_response_ended_at = record.ModelResponseEndedAt
                }
            };
        }
        else
        {
            record.ResponseData = new
            {
                model,
                tool_calls = (object?)null,
                stream = existingStream,
                selection = new { strategy = selectionStrategy, endpoint_id = endpointId, config_id = configId, config_name = configName, app_name = appName },
                timings = new
                {
                    request_received_at = requestReceivedAt,
                    model_call_started_at = record.ModelCallStartedAt,
                    model_response_started_at = record.ModelResponseStartedAt,
                    model_response_ended_at = record.ModelResponseEndedAt
                }
            };
        }
        await _llmPoolService.UpdateCallRecordAsync(record);

        try
        {
            if (record.ModelCallStartedAt.HasValue && record.ModelResponseEndedAt.HasValue)
            {
                _processingSeconds.Record((record.ModelResponseEndedAt.Value - record.ModelCallStartedAt.Value).TotalSeconds);
            }
            if (record.ModelResponseEndedAt.HasValue)
            {
                _totalSeconds.Record((record.ModelResponseEndedAt.Value - record.RequestReceivedAt).TotalSeconds);
            }
        }
        catch { }
    }

    public async Task MarkErrorAsync(EndpointCallRecord record, string message)
    {
        record.IsSuccessful = false;
        record.ErrorMessage = message;
        record.ModelResponseEndedAt = DateTime.UtcNow;
        await _llmPoolService.UpdateCallRecordAsync(record);
        try
        {
            if (record.ModelCallStartedAt.HasValue && record.ModelResponseEndedAt.HasValue)
            {
                _processingSeconds.Record((record.ModelResponseEndedAt.Value - record.ModelCallStartedAt.Value).TotalSeconds);
            }
            if (record.ModelResponseEndedAt.HasValue)
            {
                _totalSeconds.Record((record.ModelResponseEndedAt.Value - record.RequestReceivedAt).TotalSeconds);
            }
        }
        catch { }
    }

    public async Task AppendStreamEventAsync(EndpointCallRecord record, string data)
    {
        // Ensure ResponseData has a stream array and append the event data
        List<string> stream;
        if (record.ResponseData is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            if (je.TryGetProperty("stream", out var st) && st.ValueKind == JsonValueKind.Array)
            {
                stream = st.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList();
            }
            else
            {
                stream = new List<string>();
            }
        }
        else
        {
            stream = new List<string>();
        }

        stream.Add(data);

        // Recompose minimal response payload preserving existing selection if possible
        string? model = null;
        string? selStrategy = null; string? selEndpointId = null; string? selConfigId = null; string? selConfigName = null; string? selAppName = null;
        if (record.ResponseData is JsonElement je2 && je2.ValueKind == JsonValueKind.Object)
        {
            if (je2.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) model = m.GetString();
            if (je2.TryGetProperty("selection", out var sel) && sel.ValueKind == JsonValueKind.Object)
            {
                selStrategy = sel.TryGetProperty("strategy", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                selEndpointId = sel.TryGetProperty("endpoint_id", out var ei) && ei.ValueKind == JsonValueKind.String ? ei.GetString() : null;
                selConfigId = sel.TryGetProperty("config_id", out var ci) && ci.ValueKind == JsonValueKind.String ? ci.GetString() : null;
                selConfigName = sel.TryGetProperty("config_name", out var cn) && cn.ValueKind == JsonValueKind.String ? cn.GetString() : null;
                selAppName = sel.TryGetProperty("app_name", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString() : null;
            }
        }

        record.ResponseData = new
        {
            model,
            stream,
            selection = new { strategy = selStrategy, endpoint_id = selEndpointId, config_id = selConfigId, config_name = selConfigName, app_name = selAppName },
            timings = new
            {
                request_received_at = record.RequestReceivedAt,
                model_call_started_at = record.ModelCallStartedAt,
                model_response_started_at = record.ModelResponseStartedAt,
                model_response_ended_at = record.ModelResponseEndedAt
            }
        };
        await _llmPoolService.UpdateCallRecordAsync(record);
    }
}
