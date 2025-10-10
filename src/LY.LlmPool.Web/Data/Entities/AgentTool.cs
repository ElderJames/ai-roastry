namespace LY.LlmPool.Web.Data.Entities
{
    public class AgentTool
    {
        public string AgentMemberId { get; set; }
        public virtual AgentMember AgentMember { get; set; }

        public string ToolId { get; set; }
        public ToolType ToolType { get; set; }
    }

    public enum ToolType { Internal, Mcp }
}
