# 需求文档：DAG 工作流持久化

## 功能概述

本功能为 DAG（有向无环图）ReAct Agent 实现全面的工作流持久化和智能交互系统。该系统支持长时间运行的工作流进行存储、追踪、恢复，并通过自然语言交互进行动态修改，同时保持会话上下文。

**核心架构创新：**
- **App 即工具（App as Tool）**：引入新的 App 类型 `Tool`，使 LlmApp 本身可以作为工具被其他 App/Agent 调用
- **多源工具集成**：统一管理两种工具来源：
  - **App Tool**：从 `AppType = Tool` 的 LlmApp 生成的工具
  - **MCP Tool**：从 MCP 服务器配置解析的工具
- **Todolist 即 DAG**：待办任务列表不是静态列表，而是动态转换为 DAG 工作流执行

**核心能力：**
- **智能任务规划**：基于 LLM 分析用户意图，通过工具调用智能创建待办任务列表
- **动态 DAG 生成**：将 Todolist 转换为实际的 DAG 结构并执行
- **会话绑定的持久化**：所有工作流执行都绑定到会话，确保上下文连续性
- **可视化追踪**：实时可视化 Agent 依赖关系和执行进度
- **容错机制**：暂停、恢复和重试失败的工作流执行
- **AI 驱动的任务管理**：基于 LLM 检测和处理执行过程中的任务修改
- **交互式信息收集**：系统可以暂停并请求用户提供额外输入

---

## 用户故事和需求

### 1. App 即工具（App as Tool）架构

**用户故事**：作为系统架构师，我希望将 LlmApp 本身定义为可重用的工具，以便在其他 App 或 Agent 中调用，实现功能的模块化和复用。

**验收标准：**

1.1 **当** 创建或配置 LlmApp 时，系统 **必须** 支持设置 `AppType` 字段为以下值之一：
   - `Prompt`（默认）：传统的提示词应用
   - `Tool`（新增）：作为工具供其他 App/Agent 调用
   - `Orchestrator`（可选）：专门的编排器应用

1.2 **当** `AppType = Tool` 时，系统 **必须** 自动将该 App 注册为 Semantic Kernel 工具，包含：
   - **工具名称**：`{AppName}` 或用户在配置中自定义的 `toolName`
   - **工具描述**：来自 `LlmApp.Description` 字段
   - **参数 Schema**：**自动从关联的 Prompt 模板中提取参数占位符**
     - 解析 Prompt 模板中的 `{{paramName}}` 占位符
     - 生成 JSON Schema 参数定义
     - 用户可在配置中覆盖参数类型和描述
   - **返回类型**：默认为 `string`（LLM 的响应文本）

1.3 **当** 系统从 Prompt 模板提取参数时，它 **必须**：
   - 识别 Handlebars 风格的占位符：`{{variableName}}`
   - 识别嵌套占位符：`{{object.property}}`
   - 自动推断参数类型（默认为 `string`）
   - 生成基础的 JSON Schema：
     ```json
     {
       "type": "object",
       "properties": {
         "variableName": {
           "type": "string",
           "description": "从 Prompt 模板提取的参数"
         }
       },
       "required": ["variableName"]
     }
     ```

1.4 **当** Tool App 被调用时，系统 **必须**：
   - 将工具调用参数映射到 Prompt 模板的占位符
   - 渲染完整的 Prompt（填充所有参数）
   - 使用 Tool App 配置的 LlmPrompt 和 LlmConfig 执行推理
   - 将 LLM 响应作为工具返回值
   - 支持流式和非流式两种模式

1.5 **当** 配置 Tool App 时，系统 **必须** 在 `ConfigJson` 中支持以下可选字段（用于覆盖自动提取的参数）：
   ```json
   {
     "toolName": "custom_tool_name",
     "toolDescription": "覆盖默认的工具描述",
     "parameterOverrides": {
       "query": {
         "type": "string",
         "description": "自定义参数描述",
         "required": true
       },
       "maxResults": {
         "type": "integer",
         "description": "最大结果数",
         "default": 10,
         "required": false
       }
     },
     "toolReturnType": "string",
     "enableStreaming": false
   }
   ```
   **注意**：如果未提供 `parameterOverrides`，系统将完全依赖从 Prompt 模板自动提取的参数。

1.6 **当** 其他 App/Agent 需要使用 Tool App 时，系统 **必须** 提供工具发现机制：
   - 在 LlmApp 配置 UI 中显示所有 `AppType = Tool` 的应用列表
   - 显示每个工具的参数列表（从 Prompt 模板提取 + 用户覆盖）
   - 允许用户选择并注册这些工具到当前 App 的工具集合
   - 自动生成工具的 Semantic Kernel 插件定义

1.7 **当** Tool App 的 Prompt 模板或配置发生变化时，系统 **必须**：
   - 重新解析 Prompt 模板提取参数
   - 重新生成工具的参数 Schema
   - 验证参数变更是否会破坏现有的工具调用（向后兼容性检查）
   - 通知所有引用该工具的 App（如果可能）
   - 记录工具版本变更日志

**示例场景**：

假设有一个 Tool App 名为 "WebSearchTool"，其 Prompt 模板为：
```
根据用户查询 {{query}} 搜索网络，限制结果数为 {{maxResults}}。
返回格式化的搜索结果。
```

系统自动生成的工具定义：
```json
{
  "name": "WebSearchTool",
  "description": "执行网络搜索并返回格式化结果",
  "parameters": {
    "type": "object",
    "properties": {
      "query": {
        "type": "string",
        "description": "从 Prompt 模板自动提取：用户的搜索查询"
      },
      "maxResults": {
        "type": "string",
        "description": "从 Prompt 模板自动提取：最大结果数"
      }
    },
    "required": ["query", "maxResults"]
  }
}
```

其他 Agent 调用时：
```json
{
  "tool": "WebSearchTool",
  "arguments": {
    "query": "最新的AI技术趋势",
    "maxResults": "5"
  }
}
```

系统执行流程：
1. 接收工具调用和参数
2. 将参数填充到 Prompt 模板：
   ```
   根据用户查询 最新的AI技术趋势 搜索网络，限制结果数为 5。
   返回格式化的搜索结果。
   ```
3. 使用填充后的 Prompt 调用 Tool App 配置的 LLM
4. 返回 LLM 的响应作为工具输出

---

### 2. 多源工具统一管理

**用户故事**：作为开发者，我希望系统能够统一管理来自不同来源的工具（App Tool 和 MCP Tool），并提供一致的调用接口。

**验收标准：**

2.1 **当** 系统启动或配置更新时，系统 **必须** 扫描并注册两种类型的工具：
   - **App Tool**：所有 `AppType = Tool` 且 `IsEnabled = true` 的 LlmApp
   - **MCP Tool**：从 MCP 服务器配置文件解析的所有可用工具

2.2 **当** 注册工具时，系统 **必须** 为每个工具创建统一的元数据结构：
   ```csharp
   public class ToolMetadata
   {
       public string Id { get; set; }              // 工具唯一标识符
       public string Name { get; set; }            // 工具名称
       public string Description { get; set; }     // 工具描述
       public ToolSource Source { get; set; }      // App | MCP
       public string SourceId { get; set; }        // AppId 或 McpServerId
       public JsonDocument ParametersSchema { get; set; } // JSON Schema
       public bool SupportsStreaming { get; set; }
       public DateTime RegisteredAt { get; set; }
   }
   
   public enum ToolSource { App, MCP }
   ```

2.3 **当** Agent 调用工具时，系统 **必须** 根据 `ToolSource` 路由到不同的执行器：
   - **App Tool**：调用对应的 LlmApp 执行推理
   - **MCP Tool**：通过 MCP 协议调用远程工具

2.4 **当** 查询可用工具列表时，系统 **必须** 提供过滤和搜索功能：
   - 按工具来源过滤（App / MCP / All）
   - 按关键词搜索（名称、描述）
   - 按标签或类别过滤（如果配置了标签）

2.5 **当** 多个工具具有相同名称时，系统 **必须**：
   - 发出警告日志
   - 使用命名空间前缀区分：`app:{appId}:{toolName}` 或 `mcp:{serverId}:{toolName}`
   - 在工具发现 API 中明确标注冲突

2.6 **当** 持久化工具配置时，系统 **必须** 在 LlmApp 的 `ConfigJson` 中存储注册的工具列表：
   ```json
   {
     "registeredTools": [
       {
         "toolId": "app:weather-tool",
         "source": "App",
         "sourceId": "weather-app-001",
         "enabledAt": "2025-10-10T10:00:00Z"
       },
       {
         "toolId": "mcp:filesystem:read_file",
         "source": "MCP",
         "sourceId": "mcp-filesystem-server",
         "enabledAt": "2025-10-10T10:05:00Z"
       }
     ]
   }
   ```

---

### 3. Todolist 转换为 DAG 执行

**用户故事**：作为用户，我希望当 Agent 创建待办任务列表时，系统能够将其转换为实际的 DAG 工作流并执行，而不仅仅是展示静态列表。

**验收标准：**

3.1 **当** `create_workflow_todolist` 工具成功创建待办列表后，系统 **必须**：
   - 解析 `suggestedTasks` 中的依赖关系
   - 构建有向无环图（DAG）结构
   - 将每个任务映射到对应的 Agent 或 Tool App

3.2 **当** 构建 DAG 时，系统 **必须** 执行以下映射逻辑：
   ```
   Todolist Task → DAG Node
   
   对于每个 task：
   - task.taskId → node.nodeId
   - task.assignedAgentRole → 
       * 如果匹配 LlmApp.AgentMembers 中的角色 → 使用该 AgentMember
       * 如果匹配某个 Tool App 的名称 → 创建调用该 Tool App 的节点
       * 否则 → 报错：无法映射任务到 Agent/Tool
   - task.dependencies → node.dependencies
   - task.parallelGroup → 识别可并行执行的节点组
   ```

3.3 **当** Todolist 包含需要调用 Tool App 的任务时，系统 **必须**：
   - 创建一个特殊的 AgentMember 包装器，其行为是调用 Tool App
   - 将任务的输入参数传递给 Tool App
   - 将 Tool App 的输出作为节点的输出

3.4 **当** 用户确认执行待办列表时，系统 **必须**：
   - 将转换后的 DAG 结构存储在 `WorkflowExecution.ConfigJson` 中
   - 将原始 Todolist 存储在 `WorkflowExecution.TodolistJson` 中
   - 使用 DAGStrategy 执行转换后的工作流

3.5 **当** 执行过程中，系统 **必须** 维护 Todolist 任务状态与 NodeExecution 状态的同步：
   ```
   Todolist Task Status ←→ NodeExecution Status
   - pending ←→ Pending
   - in_progress ←→ Running
   - completed ←→ Completed
   - failed ←→ Failed
   - skipped ←→ Skipped
   ```

3.6 **当** 用户通过 `modify_workflow_tasks` 修改待办列表时，系统 **必须**：
   - 重新解析 Todolist
   - 重新构建 DAG 结构
   - 验证新的 DAG 仍然有效（无循环、所有任务可映射）
   - 暂停当前执行，应用新的 DAG，继续执行

3.7 **当** Todolist 中包含 `requiresUserInput: true` 的任务时，系统 **必须**：
   - 在对应节点执行前设置状态为 `AwaitingInput`
   - 暂停工作流并向用户请求输入
   - 将用户输入作为节点的额外参数传递

3.8 **当** 转换 Todolist 为 DAG 时遇到错误，系统 **必须** 返回清晰的错误信息：
   - "任务 '{taskId}' 的 assignedAgentRole '{role}' 无法映射到任何 Agent 或 Tool App"
   - "任务 '{taskId}' 依赖于不存在的任务 '{dependencyId}'"
   - "检测到循环依赖：{taskId1} → {taskId2} → {taskId1}"
   - "任务 '{taskId}' 标记为并行组 '{group}'，但该组中的其他任务有依赖冲突"

**示例场景：Todolist 到 DAG 的完整转换**

假设用户请求："帮我研究并对比 .NET 8 和 .NET 9 的性能差异"

**步骤 1：Agent 创建 Todolist**
```json
{
  "suggestedTasks": [
    {
      "taskId": "task-1",
      "title": "任务规划",
      "assignedAgentRole": "planner",
      "dependencies": [],
      "requiresUserInput": false
    },
    {
      "taskId": "task-2",
      "title": "搜索 .NET 8 信息",
      "assignedAgentRole": "WebSearchTool",  // 这是一个 Tool App
      "dependencies": ["task-1"],
      "parallelGroup": "research"
    },
    {
      "taskId": "task-3",
      "title": "搜索 .NET 9 信息",
      "assignedAgentRole": "WebSearchTool",
      "dependencies": ["task-1"],
      "parallelGroup": "research"
    },
    {
      "taskId": "task-4",
      "title": "性能对比分析",
      "assignedAgentRole": "analyst",
      "dependencies": ["task-2", "task-3"]
    },
    {
      "taskId": "task-5",
      "title": "生成报告",
      "assignedAgentRole": "reporter",
      "dependencies": ["task-4"]
    }
  ]
}
```

**步骤 2：系统转换为 DAG 结构**
```csharp
// 生成的 DAG 结构（存储在 WorkflowExecution.ConfigJson）
var dagConfig = new
{
    Nodes = new[]
    {
        new { NodeId = "task-1", AgentMemberId = "planner-agent-id", Type = "Agent" },
        new { NodeId = "task-2", ToolAppId = "websearch-tool-id", Type = "ToolApp", 
              Parameters = new { query = "{{task-1.output}}" } },
        new { NodeId = "task-3", ToolAppId = "websearch-tool-id", Type = "ToolApp", 
              Parameters = new { query = "{{task-1.output}}" } },
        new { NodeId = "task-4", AgentMemberId = "analyst-agent-id", Type = "Agent",
              InputSources = new[] { "task-2", "task-3" } },
        new { NodeId = "task-5", AgentMemberId = "reporter-agent-id", Type = "Agent",
              InputSources = new[] { "task-4" } }
    },
    ParallelGroups = new Dictionary<string, string[]>
    {
        { "research", new[] { "task-2", "task-3" } }
    },
    ExecutionOrder = new[] { "task-1", "task-2|task-3", "task-4", "task-5" }
};
```

**步骤 3：执行流程**
```
1. 执行 task-1 (Planner Agent)
   → 输出：需要收集 .NET 8 和 .NET 9 的性能指标数据
   
2. 并行执行 task-2 和 task-3 (WebSearchTool)
   task-2: 调用 WebSearchTool，参数 { query: ".NET 8 性能指标" }
   task-3: 调用 WebSearchTool，参数 { query: ".NET 9 性能指标" }
   
3. 执行 task-4 (Analyst Agent)
   → 输入：task-2 和 task-3 的输出
   → 输出：详细的性能对比分析
   
4. 执行 task-5 (Reporter Agent)
   → 输入：task-4 的输出
   → 输出：格式化的对比报告
```

**步骤 4：状态同步**
```
Todolist 显示：
✅ task-1: 任务规划 (completed)
✅ task-2: 搜索 .NET 8 信息 (completed)
✅ task-3: 搜索 .NET 9 信息 (completed)
⏳ task-4: 性能对比分析 (in_progress)
⚪ task-5: 生成报告 (pending)

NodeExecution 记录：
- node_execution(task-1): Status=Completed
- node_execution(task-2): Status=Completed
- node_execution(task-3): Status=Completed
- node_execution(task-4): Status=Running
- node_execution(task-5): Status=Pending
```

3.8 **当** 查询工作流详情时，系统 **必须** 同时返回：
   - 原始 Todolist（用户友好的任务列表）
   - 转换后的 DAG 结构（技术性的执行图）
   - 两者之间的映射关系

3.9 **当** Todolist 转换为 DAG 失败时（例如循环依赖、无法映射角色），系统 **必须**：
   - 返回详细的错误信息
   - 指出具体哪个任务或依赖关系有问题
   - 建议修复方案
   - 不创建 WorkflowExecution 记录

---

### 4. 智能待办任务列表创建

**用户故事**：作为用户，我希望当我发起复杂的 DAG 工作流请求时，大模型能够自动分析我的意图和对话历史，智能决定是否需要创建待办任务列表，并通过工具调用来生成结构化的任务计划。

**验收标准：**

1.1 **当** 用户通过 `/v1/chat/completions` 发起请求时，**若** 该请求涉及启用 DAG 模式的 LlmApp，系统 **必须** 让编排器 Agent 首先分析用户意图。

1.2 **当** 编排器 Agent 分析用户请求时，它 **必须** 考虑以下因素来决定是否需要创建待办任务列表：
   - 请求的复杂度（是否涉及多个步骤或子任务）
   - 对话历史中是否已有相关工作流正在执行
   - 用户是否明确要求任务分解（例如 "帮我规划一下步骤"）
   - 当前 DAG 配置的 Agent 数量和依赖复杂度

1.3 **当** Agent 决定需要创建待办任务列表时，它 **必须** 调用 `create_workflow_todolist` 工具，参数包括：
   ```json
   {
     "sessionId": "当前会话 ID",
     "userRequest": "用户的原始请求",
     "suggestedTasks": [
       {
         "taskId": "task-1",
         "title": "任务标题",
         "description": "任务描述",
         "assignedAgentRole": "planner | researcher | summarizer 等",
         "dependencies": ["task-0"],
         "estimatedComplexity": "low | medium | high",
         "requiresUserInput": false
       }
     ]
   }
   ```

1.4 **当** `create_workflow_todolist` 工具被调用时，系统 **必须**：
   - 验证建议的任务列表与当前 DAG 配置的 Agent 角色是否匹配
   - 检测任务依赖关系是否形成有效的 DAG（无循环）
   - 将待办列表序列化为 JSON 并存储在 `WorkflowExecution.todolist_json` 字段中
   - 返回格式化的任务计划供用户确认

1.5 **当** 待办列表创建成功后，Agent **必须** 向用户呈现任务计划：
   ```
   我为您的请求制定了以下任务计划：
   
   1. 📋 任务规划 (Planner)
      - 分析需求并制定详细步骤
   
   2. 🔍 信息收集 (Researcher x3) - 并行执行
      - 研究任务 A：[描述]
      - 研究任务 B：[描述]
      - 研究任务 C：[描述]
   
   3. 📊 结果汇总 (Summarizer)
      - 整合所有研究结果
   
   是否开始执行？或者需要调整任务？
   ```

1.6 **当** 用户确认任务计划后，系统 **必须**：
   - 将 `WorkflowExecution.Status` 设置为 `Running`
   - 根据待办列表中的 `assignedAgentRole` 和 `dependencies` 动态生成执行计划
   - 按拓扑顺序开始执行节点

1.7 **若** 用户请求调整任务（例如 "添加性能测试步骤"），Agent **必须**：
   - 调用 `modify_workflow_tasks` 工具更新待办列表
   - 重新验证 DAG 有效性
   - 更新 `WorkflowExecution.todolist_json`
   - 向用户确认修改后的计划

1.8 **若** Agent 分析后认为不需要创建待办列表（例如简单查询、单步骤任务），它 **可以** 直接开始执行而不调用 `create_workflow_todolist`。

1.9 **当** 待办列表中的任务标记为 `requiresUserInput: true` 时，系统 **必须**：
   - 在执行到该任务前暂停
   - 向用户请求所需信息
   - 将用户响应追加到节点的输入上下文中

1.10 **当** 查询工作流详情时，`get_workflow_details` 工具 **必须** 在响应中包含原始待办列表，以便用户可以对照计划查看执行进度。

---

### 5. 工作流执行持久化

**用户故事**：作为系统管理员，我希望所有 DAG 工作流执行都持久化到数据库，以便追踪执行历史、审计系统行为并从故障中恢复。

**验收标准：**

5.1 **当** 用户通过 `/v1/chat/completions` API 发起 DAG 工作流执行（`model` 指向启用 DAG 的 LlmApp）时，**则** 系统 **必须** 在开始执行前在数据库中创建 `WorkflowExecution` 记录。

5.2 **当** 创建 `WorkflowExecution` 记录时，系统 **必须** 存储以下字段：
   - `Id`（UUID，主键）
   - `AppId`（UUID，外键指向 `llm_apps`）
   - `SessionId`（字符串，来自请求的会话标识符）
   - `Status`（枚举：Pending, Running, Paused, Completed, Failed, Cancelled）
   - `StartedAt`（时间戳）
   - `CompletedAt`（可为空的时间戳）
   - `UserMessagesJson`（JSONB，原始用户消息）
   - `TodolistJson`（JSONB，可为空，存储待办任务列表）
   - `FinalResult`（文本，可为空）
   - `ErrorMessage`（文本，可为空）
   - `CreatedAt`、`UpdatedAt`（时间戳）

5.3 **当** 工作流执行状态发生变化（例如 Running → Completed）时，系统 **必须** 实时更新 `Status` 字段和 `UpdatedAt` 时间戳。

5.4 **当** 工作流执行成功完成时，系统 **必须** 将最终聚合结果存储在 `FinalResult` 字段中，并设置 `CompletedAt` 时间戳。

5.5 **当** 工作流执行失败时，系统 **必须** 将错误消息存储在 `ErrorMessage` 字段中，设置 `Status` 为 `Failed`，并设置 `CompletedAt` 时间戳。

5.6 **若** 请求中不包含 `SessionId`，系统 **必须** 返回 HTTP 400 错误，错误消息为 "sessionId is required for DAG workflow execution"。

---

### 6. Agent 节点执行追踪

**用户故事**：作为调试复杂工作流的开发者，我希望查看每个 Agent 节点的详细执行状态和输出，以便识别哪个具体步骤失败或产生了意外结果。

**验收标准：**

6.1 **当** DAG 工作流开始执行某个 Agent 节点（AgentMember）时，系统 **必须** 在数据库中创建 `NodeExecution` 记录。

6.2 **当** 创建 `NodeExecution` 记录时，系统 **必须** 存储以下字段：
   - `Id`（UUID，主键）
   - `WorkflowExecutionId`（UUID，外键指向 `workflow_executions`）
   - `NodeId`（字符串，匹配 `AgentMember.Id`）
   - `AgentName`（字符串，来自 `AgentMember.Name`）
   - `AgentRole`（字符串，来自 `AgentMember.Role`）
   - `Status`（枚举：Pending, Running, Completed, Failed, Skipped, AwaitingInput）
   - `InputMessagesJson`（JSONB，包含依赖项的输入消息）
   - `Output`（文本，可为空）
   - `ErrorMessage`（文本，可为空）
   - `StartedAt`（时间戳）
   - `CompletedAt`（可为空的时间戳）
   - `ExecutionOrder`（整数，步骤序列号）
   - `Dependencies`（JSONB 数组，依赖的节点 ID 列表）
   - `CreatedAt`、`UpdatedAt`（时间戳）

6.3 **当** 节点执行开始时，系统 **必须** 设置 `Status` 为 `Running` 并记录 `StartedAt`。

6.4 **当** 节点执行成功完成时，系统 **必须** 设置 `Status` 为 `Completed`，存储输出，并记录 `CompletedAt`。

6.5 **当** 节点执行失败时，系统 **必须** 设置 `Status` 为 `Failed`，存储错误消息，并记录 `CompletedAt`。

6.6 **当** 由于条件不满足跳过节点时，系统 **必须** 设置 `Status` 为 `Skipped`，并将原因存储在 `Output` 中。

6.7 **当** 存储来自 LLM 的流式输出时，系统 **必须** 累积所有分块，并仅在流完成后存储完整输出（避免部分数据）。

6.8 **当** 节点对应的是 Tool App 调用时，系统 **必须** 在 `NodeExecution` 中额外存储：
   - `ToolAppId`：被调用的 Tool App 的 ID
   - `ToolCallParameters`：传递给 Tool App 的参数（JSONB）

---

### 7. 基于 Agent 工具调用的工作流管理

**用户故事**：作为最终用户，我希望通过聊天界面使用自然语言与工作流执行交互，并让 Agent 自动调用适当的工具来查询、暂停、恢复或修改工作流。

**验收标准：**

7.1 **当** 在 LlmApp 中配置工具时，系统 **必须** 支持以下工作流管理工具的注册：
   - `create_workflow_todolist` - 创建待办任务列表
   - `get_workflow_history` - 查询工作流执行历史
   - `get_workflow_details` - 获取详细执行信息
   - `pause_workflow` - 暂停运行中的工作流
   - `resume_workflow` - 恢复暂停的工作流
   - `retry_failed_nodes` - 重试失败的节点
   - `modify_workflow_tasks` - 添加/删除/修改 Agent 节点
   - `get_dependency_graph` - 获取 DAG 结构可视化

7.2 **当** 用户在 LlmApp 配置中启用 DAG 编排模式时，系统 **应该** 默认提供上述工作流管理工具的注册选项（用户可选择性启用）。

7.3 **当** 用户发送类似 "显示执行历史" 的消息时，编排器 Agent **必须**：
   - 通过 LLM 推理检测意图
   - 调用 `get_workflow_history` 工具，参数为 `{ "sessionId": "<当前会话>", "limit": 10 }`
   - 将工具响应格式化为人类可读的消息
   - 将格式化的响应返回给用户

4.4 **当** 用户发送类似 "上一次工作流发生了什么？" 的消息时，Agent **必须**：
   - 调用 `get_workflow_history` 找到最近的执行
   - 使用执行 ID 调用 `get_workflow_details`
   - 总结执行结果，包括哪些节点成功/失败

4.5 **当** Agent 调用 `get_workflow_history` 工具时，工具 **必须** 接受以下参数：
   - `sessionId`（可选，默认为当前会话）
   - `appId`（可选，默认为当前应用）
   - `status`（可选过滤器："running"、"completed"、"failed"、"paused"）
   - `limit`（可选，默认 10，最大 50）
   - `offset`（可选，用于分页）

4.6 **当** Agent 调用 `get_workflow_details` 工具时，工具 **必须** 返回：
   - 完整的 `WorkflowExecution` 记录（包括 `TodolistJson` 字段）
   - 按 `ExecutionOrder` 排序的 `NodeExecution` 记录数组
   - 节点计数摘要（总数、已完成、失败、跳过、运行中）
   - 依赖图结构（节点和边）
   - 原始待办任务列表（如果存在）

4.7 **当** 工具返回大量输出（>2000 字符）时，Agent **必须** 智能地总结数据，而不是向用户倾倒原始 JSON。

---

### 5. 通过工具调用实现依赖图可视化

**用户故事**：作为工作流设计者，我希望询问 Agent "显示工作流结构" 并获得可视化表示，以便理解执行流程。

**验收标准：**

4.1 **当** 用户询问 "显示工作流图" 或 "任务结构是什么？" 时，Agent **必须** 调用 `get_dependency_graph` 工具。

4.2 **当** 使用 `executionId` 调用 `get_dependency_graph` 工具时，它 **必须** 返回：
   - **Nodes**：对象数组，包含 `{ nodeId, agentName, status, executionOrder, outputSummary (截断至 100 字符) }`
   - **Edges**：对象数组，包含 `{ from: nodeId, to: nodeId }` 表示依赖关系
   - **Levels**：按拓扑层级分组节点的数组的数组
   - **ParallelGroups**：用于可视化的 `{ groupName: [nodeIds] }` 映射

4.3 **当** Agent 收到图结构时，它 **必须** 将其格式化为：
   - 使用 ASCII 艺术或 Markdown 的文本表示（例如 Mermaid 图表语法）
   - 或返回 UI 可渲染为交互式图表的 JSON

4.4 **当** 以文本格式渲染图表时，Agent **必须** 使用状态指示符：
   - ✅ 表示已完成的节点
   - ⏳ 表示运行中的节点
   - ❌ 表示失败的节点
   - ⏸️ 表示暂停的节点
   - ⚪ 表示待处理的节点

4.5 **当** 图表表示当前正在运行的工作流时，工具 **必须** 通过查询最新的 `NodeExecution` 记录来包含实时状态更新。

---

### 5. 通过 Agent 工具调用实现暂停和恢复

**用户故事**：作为运行长时间工作流的用户，我希望说 "暂停工作流" 或 "恢复执行"，并让 Agent 自动处理。

**验收标准：**

5.1 **当** 用户发送类似 "暂停当前工作流" 或 "停止执行" 的消息时，Agent **必须**：
   - 检测暂停意图
   - 调用 `pause_workflow` 工具，参数为 `{ "executionId": "<当前执行 ID>" }`
   - 向用户确认："工作流已暂停。当前运行的节点将完成，但不会启动新节点。"

5.2 **当** 调用 `pause_workflow` 工具时，它 **必须**：
   - 在工作流执行上下文中设置取消标志
   - 将 `WorkflowExecution.Status` 更新为 `Paused`
   - 允许当前运行的节点优雅地完成
   - 防止新节点启动

5.3 **当** 用户发送类似 "恢复工作流" 或 "继续" 的消息时，Agent **必须**：
   - 检测恢复意图
   - 调用 `resume_workflow` 工具，参数为 `{ "executionId": "<暂停的执行 ID>", "retryFailed": false }`
   - 向用户确认："正在恢复工作流，从中断处继续..."

5.4 **当** 调用 `resume_workflow` 工具时，它 **必须**：
   - 验证工作流状态为 `Paused`
   - 从 `NodeExecution` 记录加载已完成的节点 ID
   - 从待处理节点继续执行（遵循依赖关系）
   - 将 `WorkflowExecution.Status` 更新为 `Running`

5.5 **当** 恢复时，工具 **必须** 跳过已完成的节点，仅执行待处理/失败的节点（如果 `retryFailed=true`）。

5.6 **当** 用户询问 "我可以恢复执行吗？" 时，Agent **必须**：
   - 检查当前会话中是否有暂停的工作流
   - 如果有，确认并询问："是的，我找到了一个 [时间戳] 的暂停工作流。我应该恢复它吗？"
   - 如果没有，回答："此会话中未找到暂停的工作流。"

5.7 **若** 自工作流暂停以来 `LlmApp.AgentMembers` 配置已更改，`resume_workflow` 工具 **必须** 返回错误："无法恢复：工作流配置已更改。"

---

### 6. 通过 Agent 工具调用重试失败节点

**用户故事**：作为 DevOps 工程师，我希望说 "重试失败的步骤"，并让 Agent 智能地仅重试失败的节点，而不重新运行成功的节点。

**验收标准：**

6.1 **当** 用户发送类似 "重试失败的节点" 或 "再试一次" 的消息时，Agent **必须**：
   - 检测重试意图
   - 识别会话中最近失败的工作流
   - 调用 `retry_failed_nodes` 工具，参数为 `{ "executionId": "<失败的执行 ID>" }`
   - 确认："正在重试 [X] 个失败节点：[节点名称列表]"

6.2 **当** 调用 `retry_failed_nodes` 工具时，它 **必须**：
   - 识别所有 `Status = Failed` 的 `NodeExecution` 记录
   - 将其 `Status` 重置为 `Pending`
   - 清除之前的 `ErrorMessage`
   - 按拓扑顺序重新执行它们
   - 保留成功节点的输出

6.3 **当** 失败的节点依赖于另一个失败的节点时，工具 **必须** 按正确的依赖顺序重试两者。

6.4 **当** 所有失败的节点重试成功时，工具 **必须** 将 `WorkflowExecution.Status` 更新为 `Completed` 并返回成功消息。

6.5 **当** 重试后任何节点再次失败时，工具 **必须** 保持 `WorkflowExecution.Status` 为 `Failed` 并报告哪些节点失败。

6.6 **当** 用户询问 "出了什么问题？" 时，Agent **必须**：
   - 查询失败的节点执行
   - 总结错误消息
   - 建议可能的修复（通过基于错误模式的 LLM 推理）

---

### 7. 通过工具调用实现 AI 驱动的任务修改

**用户故事**：作为与对话式 AI 交互的用户，我希望通过自然语言（例如 "添加性能测试步骤"）在执行过程中修改工作流任务，并让 Agent 自动更新任务列表。

**验收标准：**

7.1 **当** 用户发送类似 "在研究员之后添加性能测试 Agent" 的消息时，Agent **必须**：
   - 通过 LLM 推理检测任务修改意图
   - 提取修改详情：
     - 操作：add/remove/modify
     - 节点信息：名称、角色、依赖项、并行组
   - 使用结构化参数调用 `modify_workflow_tasks` 工具
   - 如果修改复杂，请求确认

7.2 **当** 使用操作 "add" 调用 `modify_workflow_tasks` 工具时，它 **必须** 接受参数：
   ```json
   {
     "action": "add",
     "nodeConfig": {
       "agentName": "PerformanceTester",
       "role": "运行性能基准测试",
       "dependencies": ["researcher-1"],
       "parallelGroup": null,
       "llmConfigId": "gpt-4o-mini",
       "promptId": "perf-test-prompt"
     },
     "insertAfter": "researcher-1" // 可选的定位提示
   }
   ```

7.3 **当** 使用操作 "remove" 调用 `modify_workflow_tasks` 工具时，它 **必须**：
   - 接受 `{ "action": "remove", "nodeId": "researcher-3" }`
   - 验证删除此节点不会破坏依赖关系
   - 如果其他节点依赖它，询问用户："节点 X 依赖于 Y。我应该删除依赖关系还是取消？"

7.4 **当** 使用操作 "modify" 调用 `modify_workflow_tasks` 工具时，它 **必须**：
   - 接受 `{ "action": "modify", "nodeId": "planner", "updates": { "role": "新的角色描述" } }`
   - 更新 `AgentMember` 配置
   - 重新验证 DAG

7.5 **当** 在活动工作流期间执行修改时，工具 **必须**：
   - 暂停当前执行
   - 应用修改到 `LlmApp.AgentMembers`（或创建新的工作流变体）
   - 重建依赖图
   - 验证循环
   - 使用更新的工作流恢复执行
   - 在 `WorkflowExecution` 元数据中记录修改事件

7.6 **若** 修改会在 DAG 中创建循环，工具 **必须** 返回错误："无效修改：会在 [X] 和 [Y] 之间创建循环依赖。"

7.7 **当** 成功应用修改时，Agent **必须** 确认：
   - "已添加 PerformanceTester 节点。它将在 Researcher-1 完成后执行。"
   - 或显示更新后的图结构

7.8 **当** 用户询问 "计划了什么任务？" 时，Agent **必须**：
   - 为当前或最近的执行调用 `get_dependency_graph`
   - 用自然语言总结任务列表
   - 显示每个任务的执行状态

7.9 **重要**：任务修改是 **会话隔离** 的，因为每个工作流执行本身就绑定到特定会话。修改仅影响当前会话中的工作流执行，不会影响其他会话或原始 `LlmApp` 配置（除非用户明确确认永久保存）。

---

### 8. 交互式信息收集

**用户故事**：作为用户，我希望系统在需要更多信息时暂停执行并询问澄清问题（例如 "要比较哪些 .NET 版本？"），使工作流更具交互性和上下文感知。

**验收标准：**

8.1 **当** Agent 节点检测到需要额外的用户输入（通过 LLM 分析其提示/上下文）时，系统 **必须**：
   - 暂停工作流执行
   - 将节点 `Status` 设置为 `AwaitingInput`
   - 通过聊天界面向用户发送问题
   - 将问题存储在 `NodeExecution.Output` 中，带有特殊标记（例如 `[QUESTION]`）

8.2 **当** 用户回答问题时，系统 **必须**：
   - 将答案附加到节点的输入上下文
   - 将节点 `Status` 设置回 `Running`
   - 使用丰富的上下文恢复该节点的执行

8.3 **当** 节点等待输入时，系统 **必须** 允许其他独立节点（在不同的并行组中）继续执行。

8.4 **当** 用户在可配置的超时时间内（默认：5 分钟）未响应时，系统 **必须**：
   - 将节点 `Status` 设置为 `Failed`，错误为 "等待用户输入超时"
   - 如果 `ContinueOnFailure=true`，则继续工作流，节点标记为失败

8.5 **当** 多个节点同时需要输入时，系统 **必须** 将问题排队，并一次向用户呈现一个，以避免让用户不知所措。

---

### 9. 执行历史 UI 集成

**用户故事**：作为用户，我希望聊天界面内联显示工作流执行信息（而不是单独的页面），以便我可以无缝地与工作流交互，而无需离开对话。

**验收标准：**

9.1 **当** Agent 调用工作流管理工具（`get_workflow_history`、`get_workflow_details`、`get_dependency_graph`）时，聊天 UI **必须** 检测工具响应类型。

9.2 **当** 工具响应包含工作流执行列表时，UI **必须** 将其渲染为：
   - 可折叠的执行列表，包含：
     - 执行开始时间（相对时间，例如 "2 分钟前"）
     - 工作流名称（`LlmApp.Name`）
     - 状态徽章（✅ 已完成、⏳ 运行中、❌ 失败、⏸️ 已暂停）
     - 进度条（例如 "5/8 个节点已完成"）
     - 可点击链接以展开详细信息

9.3 **当** 工具响应包含依赖图时，UI **必须**：
   - 将其渲染为交互式 SVG 图（使用 D3.js、Mermaid 或类似工具）
   - 使用颜色编码：绿色=已完成，蓝色=运行中，红色=失败，灰色=待处理
   - 显示依赖关系的箭头
   - 显示带有 Agent 名称的节点标签
   - 允许点击节点以在模态框中查看详细输出

9.4 **当** 工作流当前正在执行时，UI **必须**：
   - 在聊天顶部显示实时进度指示器
   - 每 2-3 秒自动刷新节点状态（通过轮询或 SSE）
   - 实时流式传输 Agent 输出

9.5 **当** 内联显示节点输出时，UI **必须**：
   - 折叠超过 500 字符的输出，带有 "显示更多" 按钮
   - 如果输出包含代码，则进行语法高亮
   - 提供 "复制" 按钮以便于复制

9.6 **当** Agent 建议操作（例如 "我应该重试失败的节点吗？"）时，UI **必须** 渲染快捷操作按钮：
   - [是，重试] [否，取消]
   - 点击按钮发送相应的消息以继续对话

9.7 **当** Agent 返回 Mermaid 图表语法（例如用于依赖可视化）时，UI **必须** 使用 mermaid.js 自动将其渲染为交互式图表。

---

### 10. 工具注册和发现

**用户故事**：作为后端开发者，我希望工作流管理工具能够自动注册并被 Agent 发现，以便 LLM 可以根据用户意图适当地调用它们。

**验收标准：**

10.1 **当** 在 LlmApp 配置中定义工具时，系统 **必须** 支持在 Semantic Kernel 插件注册表中注册以下工作流管理工具：
   - `WorkflowManagementPlugin.create_workflow_todolist`
   - `WorkflowManagementPlugin.get_workflow_history`
   - `WorkflowManagementPlugin.get_workflow_details`
   - `WorkflowManagementPlugin.get_dependency_graph`
   - `WorkflowManagementPlugin.pause_workflow`
   - `WorkflowManagementPlugin.resume_workflow`
   - `WorkflowManagementPlugin.retry_failed_nodes`
   - `WorkflowManagementPlugin.modify_workflow_tasks`

10.2 **当** 用户在 LlmApp 中启用 DAG 编排模式时，系统 **应该** 在 UI 配置向导中默认提供这些工具的注册选项（用户可按需选择启用）。

10.3 **当** 定义每个工具时，系统 **必须** 提供：
   - 清晰、详细的 `description` 字段，供 LLM 理解何时使用
   - 定义良好的参数架构，包括类型和描述
   - 描述中的示例用法

10.4 **当** 为启用 DAG 的应用生成 Agent 提示时，系统 **必须** 包含工具使用指南：
   ```markdown
   你可以使用工作流管理工具。使用它们帮助用户：
   - 创建任务计划：create_workflow_todolist（分析用户请求后智能决定是否需要）
   - 查询执行历史：get_workflow_history
   - 暂停/恢复工作流：pause_workflow、resume_workflow
   - 重试失败：retry_failed_nodes
   - 修改任务：modify_workflow_tasks
   
   在进行破坏性更改之前，始终与用户确认。
   对于复杂的多步骤请求，优先创建待办任务列表供用户确认后再执行。
   ```

10.5 **当** LLM 调用工作流工具时，系统 **必须**：
   - 根据架构验证工具参数
   - 执行工具函数
   - 返回结构化的 JSON 响应
   - 优雅地处理错误并返回用户友好的错误消息

10.6 **当** 工具需要 `sessionId` 时，系统 **必须** 自动从当前对话上下文注入它（用户无需提供）。

10.7 **当** 工具需要 `executionId` 且用户未指定时，系统 **必须** 默认为当前会话中最近的执行。

10.8 **当** 存在多个工作流执行且用户意图模糊（例如 "恢复工作流"）时，Agent **必须** 请求澄清：
   - "我找到了 2 个暂停的工作流。你想恢复哪一个？"
   - 列出带有时间戳和简要描述的执行

---

### 11. 性能和可扩展性

**用户故事**：作为系统架构师，我希望持久层能够处理高吞吐量场景而不降低工作流执行性能，以便系统在负载下保持响应。

**验收标准：**

11.1 **当** 持久化工作流和节点执行记录时，系统 **必须** 使用异步数据库写入（即发即弃）以避免阻塞执行流程。

11.2 **当** 节点完成执行时，系统 **必须** 在 100 毫秒内更新其 `NodeExecution` 记录（不包括网络延迟）。

11.3 **当** 存储大型输出（>1MB）时，系统 **必须** 截断或压缩数据，并在执行日志中提供警告。

11.4 **当** 查询包含许多记录的执行历史时，系统 **必须** 实现分页，默认页面大小为 20，最大页面大小为 100。

11.5 **当** 数据库暂时不可用时，系统 **必须** 在内存中缓存执行状态，并每 5 秒重试持久化，最多尝试 5 次，然后使工作流失败。

---

### 12. 数据保留和清理

**用户故事**：作为数据库管理员，我希望旧的工作流执行记录能够自动清理，以便存储成本保持可管理。

**验收标准：**

12.1 **当** 系统配置了保留策略（例如 "保留执行记录 30 天"）时，系统 **必须** 运行每日后台作业以删除超过阈值的记录。

12.2 **当** 删除 `WorkflowExecution` 记录时，系统 **必须** 级联删除所有关联的 `NodeExecution` 记录。

12.3 **当** 用户通过 API 明确将执行标记为 "重要" 时，系统 **必须** 将其排除在自动清理之外。

12.4 **当** 未配置保留策略时，系统 **必须** 无限期保留所有记录（默认行为）。

---

## 工作流管理工具规范

### 工具 1：`create_workflow_todolist`

**描述**：基于用户请求和对话历史，智能创建工作流待办任务列表。该工具由编排器 Agent 在分析用户意图后调用，生成结构化的任务计划供用户确认。

**参数**：
```json
{
  "sessionId": "string (必需) - 当前会话 ID",
  "userRequest": "string (必需) - 用户的原始请求文本",
  "conversationContext": "string (可选) - 相关对话历史摘要",
  "suggestedTasks": [
    {
      "taskId": "string (必需) - 任务唯一标识符，如 'task-1'",
      "title": "string (必需) - 任务简短标题",
      "description": "string (必需) - 任务详细描述",
      "assignedAgentRole": "string (必需) - 负责的 Agent 角色，如 'planner' | 'researcher' | 'summarizer'",
      "dependencies": ["string (可选) - 依赖的任务 ID 列表"],
      "estimatedComplexity": "string (可选) - 任务复杂度：'low' | 'medium' | 'high'",
      "requiresUserInput": "boolean (可选) - 是否需要用户输入，默认 false",
      "parallelGroup": "string (可选) - 并行组名称，同组任务可并行执行"
    }
  ]
}
```

**返回**：
```json
{
  "success": true,
  "workflowExecutionId": "uuid - 新创建的工作流执行 ID",
  "message": "已为您创建包含 5 个任务的工作流计划",
  "todolist": {
    "tasks": [
      {
        "taskId": "task-1",
        "title": "任务规划",
        "description": "分析需求并制定详细步骤",
        "assignedAgentRole": "planner",
        "dependencies": [],
        "status": "pending",
        "estimatedComplexity": "medium"
      }
    ],
    "totalTasks": 5,
    "parallelGroups": ["research"],
    "estimatedDuration": "预计 5-10 分钟"
  },
  "formattedPlan": "markdown 格式的任务计划文本，供用户查看",
  "validationWarnings": [
    "警告：任务 X 依赖于任务 Y，但 Y 未定义"
  ]
}
```

**错误响应**：
```json
{
  "success": false,
  "error": "Invalid DAG: cycle detected between task-2 and task-5",
  "validationErrors": ["具体的验证错误列表"]
}
```

---

### 工具 2：`get_workflow_history`

**描述**：检索工作流执行列表，可选按会话、应用或状态过滤。

**参数**：
```json
{
  "sessionId": "string (可选) - 按会话 ID 过滤，默认为当前会话",
  "appId": "string (可选) - 按 LlmApp ID 过滤",
  "status": "string (可选) - 按状态过滤：running、completed、failed、paused",
  "limit": "integer (可选) - 最大结果数，默认 10，最大 50",
  "offset": "integer (可选) - 分页偏移量"
}
```

**返回**：
```json
{
  "executions": [
    {
      "id": "uuid",
      "appName": "string",
      "status": "string",
      "startedAt": "ISO8601 时间戳",
      "completedAt": "ISO8601 时间戳或 null",
      "nodeCount": { "total": 5, "completed": 3, "failed": 1, "running": 1, "pending": 0 },
      "duration": "00:05:23"
    }
  ],
  "total": 42
}
```

---

### 工具 3：`get_workflow_details`

**描述**：检索特定工作流执行的详细信息，包括所有节点执行和原始待办任务列表。

**参数**：
```json
{
  "executionId": "string (必需) - 工作流执行 ID，或 'latest' 表示最近的"
}
```

**返回**：
```json
{
  "execution": {
    "id": "uuid",
    "appName": "string",
    "status": "string",
    "startedAt": "ISO8601",
    "completedAt": "ISO8601 或 null",
    "finalResult": "string 或 null",
    "errorMessage": "string 或 null",
    "todolist": { /* 原始待办任务列表，如果存在 */ }
  },
  "nodes": [
    {
      "nodeId": "string",
      "agentName": "string",
      "agentRole": "string",
      "status": "string",
      "executionOrder": 1,
      "dependencies": ["nodeId1", "nodeId2"],
      "inputSummary": "string (截断的)",
      "output": "string",
      "errorMessage": "string 或 null",
      "startedAt": "ISO8601",
      "completedAt": "ISO8601 或 null",
      "duration": "00:00:15"
    }
  ]
}
```

---

### 工具 4：`get_dependency_graph`

**描述**：检索工作流执行的 DAG 结构以进行可视化。

**参数**：
```json
{
  "executionId": "string (可选) - 执行 ID，或 'latest'，默认为会话中最近的"
}
```

**返回**：
```json
{
  "nodes": [
    {
      "id": "planner",
      "name": "TaskPlanner",
      "status": "completed",
      "order": 1,
      "outputSummary": "创建了 3 个研究任务..."
    }
  ],
  "edges": [
    { "from": "planner", "to": "researcher-1" }
  ],
  "levels": [
    ["planner"],
    ["researcher-1", "researcher-2", "researcher-3"],
    ["summarizer"]
  ],
  "parallelGroups": {
    "research": ["researcher-1", "researcher-2", "researcher-3"]
  }
}
```

---

### 工具 5：`pause_workflow`

**描述**：暂停运行中的工作流执行。

**参数**：
```json
{
  "executionId": "string (可选) - 执行 ID，默认为会话中当前运行的工作流"
}
```

**返回**：
```json
{
  "success": true,
  "message": "工作流已暂停。当前运行的节点将完成。",
  "pausedNodeIds": ["researcher-2", "researcher-3"]
}
```

---

### 工具 6：`resume_workflow`

**描述**：恢复暂停的工作流执行。

**参数**：
```json
{
  "executionId": "string (可选) - 执行 ID，默认为最近暂停的工作流",
  "retryFailed": "boolean (可选) - 是否重试失败的节点，默认 false"
}
```

**返回**：
```json
{
  "success": true,
  "message": "工作流已恢复。从节点继续：Researcher-2",
  "pendingNodeIds": ["researcher-2", "summarizer"]
}
```

---

### 工具 7：`retry_failed_nodes`

**描述**：重试工作流执行中的所有失败节点。

**参数**：
```json
{
  "executionId": "string (可选) - 执行 ID，默认为最近失败的工作流"
}
```

**返回**：
```json
{
  "success": true,
  "message": "正在重试 2 个失败节点：Researcher-1、Summarizer",
  "retriedNodeIds": ["researcher-1", "summarizer"]
}
```

---

### 工具 8：`modify_workflow_tasks`

**描述**：在工作流中添加、删除或修改 Agent 节点。

**参数**：
```json
{
  "action": "add | remove | modify",
  "nodeId": "string (remove/modify 时必需)",
  "nodeConfig": {
    "agentName": "string",
    "role": "string",
    "dependencies": ["nodeId1"],
    "parallelGroup": "string 或 null",
    "llmConfigId": "string",
    "promptId": "string",
    "timeout": "integer (可选)"
  },
  "insertAfter": "string (可选) - 定位新节点的提示"
}
```

**返回**：
```json
{
  "success": true,
  "message": "已添加 PerformanceTester 节点。它将在 Researcher-1 完成后执行。",
  "updatedGraph": { /* 与 get_dependency_graph 相同的结构 */ }
}
```

**重要说明**：修改是会话隔离的，仅影响当前会话的工作流执行。

---

## API 端点（核心执行保持不变）

主要入口点保持为：

```
POST /v1/chat/completions
{
  "model": "dag-app-id",
  "messages": [...],
  "sessionId": "conv-abc-123"  // DAG 应用必需
}
```

所有工作流管理操作都通过对话中的工具调用执行，而不是通过单独的 REST 端点。

---

## 非功能性需求

### NFR-1：安全性
- 所有会话 ID 必须根据当前用户的上下文进行验证（如果实现了身份验证）
- 数据库查询必须使用参数化语句以防止 SQL 注入

### NFR-2：可观察性
- 所有工作流状态转换（Pending → Running → Completed）必须发出结构化日志
- 执行指标（持续时间、节点计数、失败率）必须可通过应用程序洞察进行跟踪

### NFR-3：可扩展性
- 持久层必须支持未来扩展（例如向执行添加自定义元数据）
- API 应对端点进行版本控制（例如 `/api/v1/workflows`）以允许向后兼容的更改

### NFR-4：可测试性
- 所有持久化操作必须可模拟以进行单元测试
- 集成测试必须使用真实数据库验证端到端工作流执行

---

## 超出范围

以下内容明确 **不包括** 在此功能中：

- 多租户支持（所有执行都是全局或用户范围的，而不是组织范围的）
- 工作流的实时协作编辑（多个用户同时修改同一工作流）
- 工作流版本控制（跟踪 `LlmApp` 配置随时间的变化）
- 工作流执行导出/导入到外部格式（JSON/CSV）
- 高级分析仪表板（执行趋势、随时间的成功率）

---

## 成功标准

当满足以下条件时，此功能将被视为成功实现：

1. ✅ 上述所有 EARS 需求都有相应的自动化测试
2. ✅ 完整的工作流执行可以通过工具调用持久化、查询、暂停、恢复和重试
3. ✅ **App 即工具**：至少 3 个 Tool App 成功注册并可被其他 App 调用
4. ✅ **参数自动提取**：系统能够从 Prompt 模板自动提取参数并生成正确的工具 Schema
5. ✅ **多源工具管理**：App Tool 和 MCP Tool 可以统一发现和调用
6. ✅ **Todolist 转 DAG**：智能创建的待办列表能够成功转换为 DAG 并执行
7. ✅ UI 显示具有依赖图可视化的功能性执行历史（内联于聊天界面）
8. ✅ 系统可以在 5 个测试用例中至少 4 个中正确检测是否需要创建待办列表
9. ✅ 性能基准显示与非持久化执行相比开销 <10%
10. ✅ 文档已更新，包含工具规范、API 示例和架构图

---

## 问题和澄清

在进入设计阶段之前，请确认：

1. **会话 ID 来源**：`sessionId` 应该由客户端（例如前端聊天 UI）生成，还是在对 `/v1/chat/completions` 的首次请求时由服务器端生成？
   - **建议**：客户端生成，以确保跨请求的一致性

2. **工具调用模型**：哪些 LLM 模型应支持此功能的工具调用？
   - **建议**：GPT-4、GPT-4o、GPT-4o-mini、Claude 3.5 Sonnet（所有支持函数调用的模型）

3. **提示模板**：我们应该为 DAG 应用创建包含工作流管理指南的默认系统提示，还是让用户完全自定义？
   - **澄清**：按需配置。在 LlmApp 配置中提供默认模板选项，但允许完全自定义

4. **任务修改持久化**：当 Agent 通过 `modify_workflow_tasks` 修改工作流任务时，它应该：
   - **A.** 永久更新原始 `LlmApp.AgentMembers`
   - **B.** 创建会话特定的工作流变体（按会话隔离）
   - **C.** 两者（在永久更改之前请求用户确认）
   - **✅ 已澄清**：**选项 B** - 任务修改是会话隔离的，因为工作流执行本身绑定到会话。修改仅影响当前会话，不修改原始 LlmApp 配置（除非用户明确确认永久保存）

5. **保留策略**：工作流执行的默认保留期应该是多少？
   - **建议**：30 天

6. **节点输出大小限制**：单个节点输出在截断/压缩之前的最大可接受大小是多少？
   - **建议**：10MB

7. **实时更新**：为了在 UI 中显示实时工作流进度，我们应该使用：
   - **A.** 轮询（更简单，到处都可以工作）
   - **B.** Server-Sent Events (SSE) - 更适合流式传输
   - **C.** WebSocket（双向，更复杂）
   - **建议**：先实现 **A（轮询）**，后续可升级到 **B（SSE）**

8. **身份验证**：是否有现有的用户身份验证系统应集成以进行会话/执行访问控制？
   - **建议**：如果存在，集成；否则，假设会话是公共的，仅按 sessionId 隔离

9. **待办任务列表创建时机**：Agent 应该在什么情况下调用 `create_workflow_todolist` 工具？
   - **A.** 仅当用户明确要求（例如 "帮我规划步骤"）
   - **B.** 当请求涉及 3+ 个 Agent 节点或复杂依赖关系时自动创建
   - **C.** 对所有 DAG 工作流都默认创建待办列表
   - **建议**：**选项 B** - 让 LLM 基于请求复杂度、Agent 数量、对话历史智能决定。简单单步骤任务可跳过待办列表直接执行。

10. **待办任务与实际执行的同步**：如果用户在执行过程中修改任务，系统应该：
    - **A.** 仅更新待办列表 JSON，不影响实际执行
    - **B.** 同时更新待办列表和实际执行计划
    - **C.** 暂停执行，更新计划，请求用户确认后重新开始
    - **建议**：**选项 B** - 待办列表和执行计划保持同步，修改通过 `modify_workflow_tasks` 工具同时影响两者

---

**文档版本**：2.0（中文）  
**最后更新**：2025-10-10  
**状态**：草稿 - 等待审核  
**主要更新**：
- v2.0: 新增 App 即工具架构、多源工具管理、Todolist 转 DAG 执行
- v1.0: 初始版本，基础工作流持久化和工具调用管理
