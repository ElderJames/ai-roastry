using LY.LlmPool.Web.Data.Entities;

namespace LY.LlmPool.Web.Models
{
    public class PromptTestConfig
    {
        public string Type { get; set; } = "Model";
        public string ConfigId { get; set; } = string.Empty;
        public string? Parameters { get; set; }
        public PromptTestResult? TestResult { get; set; }
        public bool IsStreaming { get; set; }
        public List<ChatMessage> ChatHistory { get; set; } = new();

        public static PromptTestConfig FromTestConfigRecord(TestConfigRecord record)
        {
            return new PromptTestConfig
            {
                Type = record.Type,
                ConfigId = record.ConfigId ?? string.Empty,
                Parameters = record.Parameters,
                TestResult = new PromptTestResult
                {
                    Success = record.Success,
                    Response = record.Response,
                    Error = record.Error
                },
                IsStreaming = false,
                ChatHistory = new List<ChatMessage>()
            };
        }
    }

    public class PromptTestResult
    {
        public bool Success { get; set; }
        public string? Response { get; set; }
        public string? Error { get; set; }
    }
} 