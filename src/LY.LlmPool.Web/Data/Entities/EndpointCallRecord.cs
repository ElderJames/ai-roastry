using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace LY.LlmPool.Web.Data.Entities;

public class EndpointCallRecord
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Required]
    public string EndpointId { get; set; } = string.Empty;
    
    public virtual LlmEndpoint Endpoint { get; set; } = null!;
    
    // Request received timestamp
    public DateTime RequestReceivedAt { get; set; } = DateTime.UtcNow;
    
    // Time spent waiting for an available model
    public TimeSpan WaitTime { get; set; }
    
    // Config that was used for the call
    public string? LlmConfigId { get; set; }
    
    public virtual LlmConfig? LlmConfig { get; set; }
    
    // Model call timing
    public DateTime? ModelCallStartedAt { get; set; }
    
    public DateTime? ModelResponseStartedAt { get; set; }
    
    public DateTime? ModelResponseEndedAt { get; set; }
    
    // Call results
    public bool IsSuccessful { get; set; }
    
    public string? ErrorMessage { get; set; }
    
    // Request and response data
    [Column(TypeName = "jsonb")]
    public string? RequestDataJson { get; set; }
    
    [NotMapped]
    public object? RequestData
    {
        get => string.IsNullOrEmpty(RequestDataJson)
            ? null
            : JsonSerializer.Deserialize<object>(RequestDataJson, _jsonOptions);
        set => RequestDataJson = value != null 
            ? JsonSerializer.Serialize(value, _jsonOptions) 
            : null;
    }
    
    [Column(TypeName = "jsonb")]
    public string? ResponseDataJson { get; set; }
    
    [NotMapped]
    public object? ResponseData
    {
        get => string.IsNullOrEmpty(ResponseDataJson)
            ? null
            : JsonSerializer.Deserialize<object>(ResponseDataJson, _jsonOptions);
        set => ResponseDataJson = value != null 
            ? JsonSerializer.Serialize(value, _jsonOptions) 
            : null;
    }
    
    // Child calls - to track the call chain
    public string? ParentCallId { get; set; }
    
    public virtual EndpointCallRecord? ParentCall { get; set; }
    
    public virtual ICollection<EndpointCallRecord> ChildCalls { get; set; } = new List<EndpointCallRecord>();
    
    // Time metrics
    [NotMapped]
    public TimeSpan? ModelProcessingTime => 
        ModelResponseEndedAt.HasValue && ModelCallStartedAt.HasValue 
            ? ModelResponseEndedAt.Value - ModelCallStartedAt.Value 
            : null;
    
    [NotMapped]
    public TimeSpan? TotalTime =>
        ModelResponseEndedAt.HasValue 
            ? ModelResponseEndedAt.Value - RequestReceivedAt 
            : null;

    public long? PromptTokens { get; set; }
    public long? CompletionTokens { get; set; }
    public long? TotalTokens { get; set; }

    // Use relaxed encoder to preserve Chinese and symbols in stored JSON
    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false
    };
} 