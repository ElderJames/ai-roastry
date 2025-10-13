namespace LY.LlmPool.Web.Models
{
    public static class LlmAppTypes
    {
        public const string Prompt = "Prompt";
        public const string AgentGroup = "AgentGroup";
        public const string Tool = "Tool";
        public static readonly string[] All = new[] { Prompt, AgentGroup, Tool };
    }
}
