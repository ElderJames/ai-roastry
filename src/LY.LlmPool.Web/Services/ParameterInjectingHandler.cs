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

            // 🎯 创建新的 JSON 对象，将参数注入到 "parameters" 字段中
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();

                // 复制所有现有属性（除了 parameters，我们会特殊处理）
                bool hasExistingParameters = false;
                JsonElement existingParameters = default;
                
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name == "parameters")
                    {
                        hasExistingParameters = true;
                        existingParameters = property.Value;
                        continue; // 先不写入，稍后合并
                    }
                    property.WriteTo(writer);
                }

                // 🎯 写入 parameters 字段（合并已有的和新注入的）
                writer.WritePropertyName("parameters");
                writer.WriteStartObject();
                
                // 先写入已存在的 parameters
                if (hasExistingParameters && existingParameters.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in existingParameters.EnumerateObject())
                    {
                        property.WriteTo(writer);
                        _logger?.LogDebug("保留已存在的 parameter: {ParamKey}", property.Name);
                    }
                }
                
                // 注入新参数（不覆盖已存在的）
                foreach (var param in _parameters)
                {
                    // 跳过已存在的 parameter
                    if (hasExistingParameters && 
                        existingParameters.ValueKind == JsonValueKind.Object &&
                        existingParameters.TryGetProperty(param.Key, out _))
                    {
                        _logger?.LogDebug("参数 {ParamKey} 已存在于 parameters 中，跳过注入", param.Key);
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

                    _logger?.LogDebug("注入参数到 parameters: {ParamKey} = {ParamValue}", param.Key, param.Value);
                }
                
                // 🎯 结束 parameters 对象
                writer.WriteEndObject();

                // 🎯 结束根对象
                writer.WriteEndObject();
            }

            // 创建新的请求内容
            var modifiedContent = Encoding.UTF8.GetString(stream.ToArray());
            request.Content = new StringContent(modifiedContent, Encoding.UTF8, "application/json");

            _logger?.LogInformation("成功注入 {Count} 个参数到 parameters 字段", _parameters.Count);
            _logger?.LogDebug("修改后的请求体: {Content}", modifiedContent);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "注入参数时发生错误");
            // 发生错误时，使用原始请求继续
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
