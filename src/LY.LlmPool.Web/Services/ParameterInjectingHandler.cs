using System.Text;
using System.Text.Json;

namespace LY.LlmPool.Web.Services;

/// <summary>
/// HTTP 处理器，用于向 LLM API 请求中注入额外的参数（如 temperature, max_tokens 等）
/// </summary>
public class ParameterInjectingHandler : DelegatingHandler
{
    private readonly Dictionary<string, object>? _parameters;
    private readonly ILogger<ParameterInjectingHandler>? _logger;

    public ParameterInjectingHandler(
        Dictionary<string, object>? parameters,
        ILogger<ParameterInjectingHandler>? logger = null)
    {
        _parameters = parameters;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // 如果没有参数或不是 POST 请求，直接传递
        if (_parameters == null || _parameters.Count == 0 || request.Method != HttpMethod.Post)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // 只处理 JSON 内容
        if (request.Content?.Headers.ContentType?.MediaType != "application/json")
        {
            return await base.SendAsync(request, cancellationToken);
        }

        try
        {
            // 读取原始请求内容
            var originalContent = await request.Content.ReadAsStringAsync(cancellationToken);
            
            if (string.IsNullOrWhiteSpace(originalContent))
            {
                return await base.SendAsync(request, cancellationToken);
            }

            // 解析 JSON
            using var jsonDoc = JsonDocument.Parse(originalContent);
            var root = jsonDoc.RootElement;

            // 创建新的 JSON 对象，合并参数
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();

                // 复制所有现有属性
                foreach (var property in root.EnumerateObject())
                {
                    property.WriteTo(writer);
                }

                // 注入额外参数（如果不存在）
                foreach (var param in _parameters)
                {
                    // 跳过已存在的属性
                    if (root.TryGetProperty(param.Key, out _))
                    {
                        _logger?.LogDebug("参数 {ParamKey} 已存在于请求中，跳过注入", param.Key);
                        continue;
                    }

                    // 根据类型写入值
                    switch (param.Value)
                    {
                        case int intValue:
                            writer.WriteNumber(param.Key, intValue);
                            break;
                        case long longValue:
                            writer.WriteNumber(param.Key, longValue);
                            break;
                        case float floatValue:
                            writer.WriteNumber(param.Key, floatValue);
                            break;
                        case double doubleValue:
                            writer.WriteNumber(param.Key, doubleValue);
                            break;
                        case bool boolValue:
                            writer.WriteBoolean(param.Key, boolValue);
                            break;
                        case string stringValue:
                            writer.WriteString(param.Key, stringValue);
                            break;
                        default:
                            // 对于其他类型，序列化为 JSON
                            writer.WritePropertyName(param.Key);
                            JsonSerializer.Serialize(writer, param.Value);
                            break;
                    }

                    _logger?.LogDebug("注入参数: {ParamKey} = {ParamValue}", param.Key, param.Value);
                }

                writer.WriteEndObject();
            }

            // 创建新的请求内容
            var modifiedContent = Encoding.UTF8.GetString(stream.ToArray());
            request.Content = new StringContent(modifiedContent, Encoding.UTF8, "application/json");

            _logger?.LogInformation("成功注入 {Count} 个参数到请求中", 
                _parameters.Count(p => !root.TryGetProperty(p.Key, out _)));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "注入参数时发生错误");
            // 发生错误时，使用原始请求继续
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
