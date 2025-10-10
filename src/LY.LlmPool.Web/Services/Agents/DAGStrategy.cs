using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;

namespace LY.LlmPool.Web.Services.Agents;

/// <summary>
/// DAG（有向无环图）编排策略：支持依赖关系、条件分支、并行执行的工作流
/// </summary>
public class DAGStrategy : IOrchestrationStrategy
{
    public async Task<string> ExecuteAsync(
        LlmApp app,
        IEnumerable<ChatMessage> userMessages,
        Func<LlmConfig, List<ChatMessage>, Task<ChatResponse>> sendMessage,
        Func<LlmConfig, List<ChatMessage>, IAsyncEnumerable<string>> sendStreamingMessage,
        Func<string, string?, int, string, bool, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        if (app.AgentMembers == null || app.AgentMembers.Count == 0)
        {
            return "No agents configured.";
        }

        // 解析 DAG 配置
        var dagConfig = ParseDAGConfig(app);
        var members = app.AgentMembers.ToDictionary(m => m.Id, m => m);

        // 构建节点依赖图
        var nodeConfigs = ParseNodeConfigs(app.AgentMembers);
        var graph = BuildDependencyGraph(nodeConfigs, members);

        // 拓扑排序验证 DAG
        if (!ValidateDAG(graph, out var sortedNodes))
        {
            return "ERROR: Workflow contains cycles or invalid dependencies.";
        }

        // 执行上下文
        var context = new WorkflowExecutionContext
        {
            UserMessages = userMessages.ToList(),
            NodeResults = new ConcurrentDictionary<string, NodeExecutionResult>(),
            GlobalContext = string.Empty
        };

        // 执行工作流
        await ExecuteWorkflowAsync(sortedNodes, graph, members, dagConfig, context, sendMessage, sendStreamingMessage, onProgress, ct);

        // 汇总最终结果
        return AggregateFinalResult(context);
    }

    private DAGWorkflowConfig ParseDAGConfig(LlmApp app)
    {
        var config = new DAGWorkflowConfig();

        if (app.Config != null && app.Config.TryGetValue("DAGWorkflow", out var dagObj) && dagObj is JsonElement je)
        {
            try
            {
                config = JsonSerializer.Deserialize<DAGWorkflowConfig>(je.GetRawText()) ?? config;
            }
            catch { }
        }

        return config;
    }

    private Dictionary<string, AgentMemberDAGConfig> ParseNodeConfigs(ICollection<AgentMember> members)
    {
        var configs = new Dictionary<string, AgentMemberDAGConfig>();

        foreach (var member in members)
        {
            var config = new AgentMemberDAGConfig();

            if (!string.IsNullOrWhiteSpace(member.ConfigJson))
            {
                try
                {
                    config = JsonSerializer.Deserialize<AgentMemberDAGConfig>(member.ConfigJson) ?? config;
                }
                catch { }
            }

            configs[member.Id] = config;
        }

        return configs;
    }

    private Dictionary<string, List<string>> BuildDependencyGraph(
        Dictionary<string, AgentMemberDAGConfig> nodeConfigs,
        Dictionary<string, AgentMember> members)
    {
        var graph = new Dictionary<string, List<string>>();

        foreach (var memberId in members.Keys)
        {
            if (nodeConfigs.TryGetValue(memberId, out var config) && config.Dependencies != null)
            {
                graph[memberId] = config.Dependencies.Where(d => members.ContainsKey(d)).ToList();
            }
            else
            {
                graph[memberId] = new List<string>();
            }
        }

        return graph;
    }

    private bool ValidateDAG(Dictionary<string, List<string>> graph, out List<string> sortedNodes)
    {
        sortedNodes = new List<string>();
        var inDegree = graph.Keys.ToDictionary(k => k, k => 0);

        // 计算入度
        foreach (var deps in graph.Values)
        {
            foreach (var dep in deps)
            {
                if (inDegree.ContainsKey(dep))
                {
                    inDegree[dep]++;
                }
            }
        }

        // 拓扑排序（Kahn's Algorithm）
        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var visited = 0;

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            sortedNodes.Add(node);
            visited++;

            if (graph.TryGetValue(node, out var dependencies))
            {
                foreach (var dep in dependencies)
                {
                    inDegree[dep]--;
                    if (inDegree[dep] == 0)
                    {
                        queue.Enqueue(dep);
                    }
                }
            }
        }

        return visited == graph.Count; // 所有节点都访问则无环
    }

    private async Task ExecuteWorkflowAsync(
        List<string> sortedNodes,
        Dictionary<string, List<string>> graph,
        Dictionary<string, AgentMember> members,
        DAGWorkflowConfig dagConfig,
        WorkflowExecutionContext context,
        Func<LlmConfig, List<ChatMessage>, Task<ChatResponse>> sendMessage,
        Func<LlmConfig, List<ChatMessage>, IAsyncEnumerable<string>> sendStreamingMessage,
        Func<string, string?, int, string, bool, Task>? onProgress,
        CancellationToken ct)
    {
        var nodeConfigs = ParseNodeConfigs(members.Values.ToList());
        var completed = new HashSet<string>();
        var step = 0;

        // 按拓扑顺序分层执行
        var levels = GroupIntoLevels(sortedNodes, graph);

        foreach (var level in levels)
        {
            // 同一层的节点可并行执行
            var parallelGroups = GroupByParallelGroup(level, nodeConfigs);

            foreach (var group in parallelGroups)
            {
                var tasks = group.Select(nodeId => ExecuteNodeAsync(
                    nodeId,
                    members[nodeId],
                    nodeConfigs[nodeId],
                    context,
                    completed,
                    sendMessage,
                    sendStreamingMessage,
                    onProgress,
                    ++step,
                    ct
                ));

                // 限制并行度
                var semaphore = new SemaphoreSlim(dagConfig.MaxParallelism);
                await Task.WhenAll(tasks.Select(async task =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        await task;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }
        }
    }

    private async Task ExecuteNodeAsync(
        string nodeId,
        AgentMember member,
        AgentMemberDAGConfig config,
        WorkflowExecutionContext context,
        HashSet<string> completed,
        Func<LlmConfig, List<ChatMessage>, Task<ChatResponse>> sendMessage,
        Func<LlmConfig, List<ChatMessage>, IAsyncEnumerable<string>> sendStreamingMessage,
        Func<string, string?, int, string, bool, Task>? onProgress,
        int step,
        CancellationToken ct)
    {
        // 检查依赖是否都已完成
        if (config.Dependencies != null && !config.Dependencies.All(d => completed.Contains(d)))
        {
            return;
        }

        // 评估条件
        if (config.Condition != null && !EvaluateCondition(config.Condition, context))
        {
            context.NodeResults[nodeId] = new NodeExecutionResult
            {
                NodeId = nodeId,
                Status = "skipped",
                Output = "Condition not met"
            };
            return;
        }

        // 检查配置
        if (member.LlmConfig == null)
        {
            context.NodeResults[nodeId] = new NodeExecutionResult
            {
                NodeId = nodeId,
                Status = "error",
                Error = "Missing LlmConfig"
            };
            return;
        }

        try
        {
            // 构建消息
            var messages = new List<ChatMessage>();

            if (member.LlmPrompt != null && !string.IsNullOrWhiteSpace(member.LlmPrompt.Content))
            {
                messages.Add(new ChatMessage { Role = "system", Content = member.LlmPrompt.Content });
            }

            // 用户消息 + 依赖节点的输出
            var userContent = string.Join("\n", context.UserMessages.Select(m => m.Content ?? ""));
            if (config.Dependencies != null && config.Dependencies.Any())
            {
                var depOutputs = config.Dependencies
                    .Where(d => context.NodeResults.ContainsKey(d))
                    .Select(d => $"[{context.NodeResults[d].NodeId}]: {context.NodeResults[d].Output}");
                userContent += "\n\n[Dependencies Output]\n" + string.Join("\n", depOutputs);
            }

            if (!string.IsNullOrWhiteSpace(context.GlobalContext))
            {
                userContent += "\n\n[Global Context]\n" + context.GlobalContext;
            }

            messages.Add(new ChatMessage { Role = "user", Content = userContent });

            // 执行带超时
            var timeout = config.TimeoutSeconds.HasValue
                ? TimeSpan.FromSeconds(config.TimeoutSeconds.Value)
                : TimeSpan.FromMinutes(5);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            // 使用流式输出
            var outputBuilder = new System.Text.StringBuilder();
            var success = true;
            string? errorMessage = null;

            try
            {
                await foreach (var chunk in sendStreamingMessage(member.LlmConfig, messages).WithCancellation(cts.Token))
                {
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        outputBuilder.Append(chunk);

                        // 通知进度 - 不添加前缀,让 UI 层处理显示
                        if (onProgress != null)
                        {
                            await onProgress(member.Name ?? "", member.Role, step, chunk, false);
                        }
                    }
                }
            }
            catch (Exception streamEx)
            {
                success = false;
                errorMessage = streamEx.Message;
            }

            var output = success ? outputBuilder.ToString() : $"ERROR: {errorMessage}";

            context.NodeResults[nodeId] = new NodeExecutionResult
            {
                NodeId = nodeId,
                Status = success ? "success" : "error",
                Output = output,
                Error = success ? null : errorMessage
            };

            // 更新全局上下文
            if (success)
            {
                context.GlobalContext = output;
            }

            completed.Add(nodeId);

            // 通知最终完成
            if (onProgress != null && success)
            {
                await onProgress(member.Name ?? "", member.Role, step, string.Empty, true);
            }
        }
        catch (Exception ex)
        {
            context.NodeResults[nodeId] = new NodeExecutionResult
            {
                NodeId = nodeId,
                Status = "error",
                Error = ex.Message
            };

            if (config.ContinueOnFailure != true)
            {
                throw;
            }
        }
    }

    private bool EvaluateCondition(WorkflowCondition condition, WorkflowExecutionContext context)
    {
        var fieldValue = condition.Field switch
        {
            "result" => context.GlobalContext,
            _ when condition.Field.StartsWith("node.") => GetNodeOutput(condition.Field, context),
            _ => ""
        };

        return condition.Type switch
        {
            "contains" => fieldValue.Contains(condition.Value, StringComparison.OrdinalIgnoreCase),
            "equals" => string.Equals(fieldValue, condition.Value, StringComparison.OrdinalIgnoreCase),
            "regex" => System.Text.RegularExpressions.Regex.IsMatch(fieldValue, condition.Value),
            _ => true
        };
    }

    private string GetNodeOutput(string field, WorkflowExecutionContext context)
    {
        // field format: "node.{nodeId}.output"
        var parts = field.Split('.');
        if (parts.Length >= 2 && context.NodeResults.TryGetValue(parts[1], out var result))
        {
            return result.Output ?? "";
        }
        return "";
    }

    private List<List<string>> GroupIntoLevels(List<string> sortedNodes, Dictionary<string, List<string>> graph)
    {
        var levels = new List<List<string>>();
        var nodeLevel = new Dictionary<string, int>();

        foreach (var node in sortedNodes)
        {
            var maxDepLevel = 0;
            if (graph.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps)
                {
                    if (nodeLevel.TryGetValue(dep, out var depLevel))
                    {
                        maxDepLevel = Math.Max(maxDepLevel, depLevel + 1);
                    }
                }
            }

            nodeLevel[node] = maxDepLevel;

            while (levels.Count <= maxDepLevel)
            {
                levels.Add(new List<string>());
            }

            levels[maxDepLevel].Add(node);
        }

        return levels;
    }

    private List<List<string>> GroupByParallelGroup(List<string> nodes, Dictionary<string, AgentMemberDAGConfig> configs)
    {
        var groups = new Dictionary<string, List<string>>();
        var defaultGroup = new List<string>();

        foreach (var node in nodes)
        {
            if (configs.TryGetValue(node, out var config) && !string.IsNullOrWhiteSpace(config.ParallelGroup))
            {
                if (!groups.ContainsKey(config.ParallelGroup))
                {
                    groups[config.ParallelGroup] = new List<string>();
                }
                groups[config.ParallelGroup].Add(node);
            }
            else
            {
                defaultGroup.Add(node);
            }
        }

        var result = groups.Values.ToList();
        if (defaultGroup.Any())
        {
            result.Insert(0, defaultGroup);
        }

        return result;
    }

    private string AggregateFinalResult(WorkflowExecutionContext context)
    {
        var successNodes = context.NodeResults.Values.Where(r => r.Status == "success").ToList();
        if (!successNodes.Any())
        {
            return "No successful node executions.";
        }

        // 返回最后一个成功节点的输出
        return successNodes.Last().Output ?? "";
    }

    private class WorkflowExecutionContext
    {
        public List<ChatMessage> UserMessages { get; set; } = new();
        public ConcurrentDictionary<string, NodeExecutionResult> NodeResults { get; set; } = new();
        public string GlobalContext { get; set; } = string.Empty;
    }

    private class NodeExecutionResult
    {
        public string NodeId { get; set; } = string.Empty;
        public string Status { get; set; } = "pending";
        public string? Output { get; set; }
        public string? Error { get; set; }
    }
}
