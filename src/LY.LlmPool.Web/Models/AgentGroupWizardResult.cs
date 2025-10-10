using System.Collections.Generic;
using LY.LlmPool.Web.Data.Entities;

namespace LY.LlmPool.Web.Models
{
    public class AgentGroupWizardResult
    {
        public OrchestrationMode OrchestrationMode { get; set; } = OrchestrationMode.Sequential;
        public int? GroupChatMaxRounds { get; set; }
        public bool? GroupChatStopOnFinal { get; set; }
        public string? GroupChatRoleOrder { get; set; }
        public int? GroupChatContextLimit { get; set; }
        
        // DAG workflow settings
        public int? DAGMaxParallelism { get; set; }
        public int? DAGGlobalTimeout { get; set; }
        public string? DAGFailureStrategy { get; set; }
        
        public List<AgentMember> Members { get; set; } = new();
    }
}
