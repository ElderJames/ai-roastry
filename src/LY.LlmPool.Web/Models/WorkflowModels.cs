using System.Collections.Generic;

namespace LY.LlmPool.Web.Models
{
    /// <summary>
    /// DAG 工作流节点配置
    /// </summary>
    public class WorkflowNode
    {
        /// <summary>
        /// 节点 ID（对应 AgentMember.Id）
        /// </summary>
        public string NodeId { get; set; } = string.Empty;

        /// <summary>
        /// 节点名称
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 依赖的前置节点 ID 列表（所有前置节点完成后才能执行）
        /// </summary>
        public List<string> Dependencies { get; set; } = new();

        /// <summary>
        /// 条件表达式（可选）：用于条件分支，例如 "result.contains('success')"
        /// </summary>
        public string? Condition { get; set; }

        /// <summary>
        /// 并行组 ID（可选）：相同 ParallelGroup 的节点可并行执行
        /// </summary>
        public string? ParallelGroup { get; set; }

        /// <summary>
        /// 超时时间（秒）
        /// </summary>
        public int? TimeoutSeconds { get; set; }
    }

    /// <summary>
    /// DAG 工作流边配置
    /// </summary>
    public class WorkflowEdge
    {
        /// <summary>
        /// 源节点 ID
        /// </summary>
        public string From { get; set; } = string.Empty;

        /// <summary>
        /// 目标节点 ID
        /// </summary>
        public string To { get; set; } = string.Empty;

        /// <summary>
        /// 边的条件（可选）：满足条件才沿此边流转
        /// </summary>
        public string? Condition { get; set; }

        /// <summary>
        /// 边的优先级（可选）：多条边同时满足时的执行顺序
        /// </summary>
        public int Priority { get; set; } = 0;
    }

    /// <summary>
    /// 条件评估表达式
    /// </summary>
    public class WorkflowCondition
    {
        /// <summary>
        /// 条件类型：contains, equals, regex, custom
        /// </summary>
        public string Type { get; set; } = "contains";

        /// <summary>
        /// 目标字段：result, metadata.key, agent.output
        /// </summary>
        public string Field { get; set; } = "result";

        /// <summary>
        /// 比较值
        /// </summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// 自定义脚本（当 Type = custom 时使用）
        /// </summary>
        public string? Script { get; set; }
    }

    /// <summary>
    /// DAG 工作流配置
    /// </summary>
    public class DAGWorkflowConfig
    {
        /// <summary>
        /// 工作流节点列表
        /// </summary>
        public List<WorkflowNode> Nodes { get; set; } = new();

        /// <summary>
        /// 工作流边列表
        /// </summary>
        public List<WorkflowEdge> Edges { get; set; } = new();

        /// <summary>
        /// 最大并行度（默认 3）
        /// </summary>
        public int MaxParallelism { get; set; } = 3;

        /// <summary>
        /// 全局超时时间（秒）
        /// </summary>
        public int? GlobalTimeoutSeconds { get; set; }

        /// <summary>
        /// 失败处理策略：stop, continue, retry
        /// </summary>
        public string FailureStrategy { get; set; } = "stop";

        /// <summary>
        /// 重试次数（当 FailureStrategy = retry）
        /// </summary>
        public int RetryCount { get; set; } = 0;
    }

    /// <summary>
    /// Agent 成员的 DAG 配置（存储在 AgentMember.ConfigJson）
    /// </summary>
    public class AgentMemberDAGConfig
    {
        /// <summary>
        /// 依赖的前置节点 ID 列表
        /// </summary>
        public List<string>? Dependencies { get; set; }

        /// <summary>
        /// 执行条件
        /// </summary>
        public WorkflowCondition? Condition { get; set; }

        /// <summary>
        /// 并行组 ID
        /// </summary>
        public string? ParallelGroup { get; set; }

        /// <summary>
        /// 节点超时时间（秒）
        /// </summary>
        public int? TimeoutSeconds { get; set; }

        /// <summary>
        /// 失败时是否继续（覆盖全局设置）
        /// </summary>
        public bool? ContinueOnFailure { get; set; }
    }
}
