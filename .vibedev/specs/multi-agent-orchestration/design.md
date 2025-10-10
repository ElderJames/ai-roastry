# 设计文档：多 Agent 编排框架

## 1. 概述

本文档为“多 Agent 编排框架”功能提供技术设计方案。该设计旨在将现有的 `LlmApp` 扩展为功能强大的自主 Agent，并建立一个灵活的编排层来协调它们完成复杂任务。设计重点在于模块化、可扩展性和与现有 `llm-pool` 体系的无缝集成。

Semantic Kernal 的Agent编排示例，参考： https://github.com/microsoft/semantic-kernel/tree/main/dotnet/samples/GettingStartedWithAgents/Orchestration

## 2. 架构

我们将采用分层架构，将编排逻辑直接集成到 `LlmApp` 中。当一个 `LlmApp` 的类型被配置为 `AgentGroup` 时，它就成为一个编排器。

```mermaid
graph TD
    subgraph "API Layer"
        A[API Endpoint: /v1/apps/{appId}/invoke]
    end

    subgraph "Orchestration Layer"
        B[AgentOrchestratorService]
        C[IOrchestrationStrategy]
        C1[SequentialStrategy]
        C2[GroupChatStrategy]
    end

    subgraph "Agent Layer"
        D[Agent (runtime concept)]
        E[ReActEngine]
        F[ToolExecutor]
    end

    subgraph "Tool Layer"
        G[InternalPluginTool]
        H[McpTool]
        I[ContextExtractorTool]
    end

    subgraph "Service & Data Layer"
        J[ChatClientService]
        K[LlmPoolService]
        L[Database: LlmApp, AgentMember, McpServerConfig, etc.]
    end

    A --> B
    B --> C
    C --> C1
    C --> C2
    B --> D
    D --> E
    E --> F
    E --> J
    F --> G
    F --> H
    F --> I
    I --> J
    H --> L
    D --> K
```

**核心流程**:
1.  调用 `LlmApp` 的 API 端点。
2.  服务层检查 `LlmApp` 的 `AppType`。
3.  如果 `AppType` 是 `AgentGroup`，请求被转发给 `AgentOrchestratorService`。
4.  `AgentOrchestratorService` 加载 `LlmApp` 的编排策略 (`OrchestrationMode`) 和其包含的 `AgentMember` 列表。
5.  对于每个 `AgentMember`，动态构建一个运行时的 `Agent` 实例（包含其 `LlmPrompt`、`LlmConfig` 和工具）。
6.  编排策略开始执行，决定哪个 `Agent` 当前拥有控制权。
7.  被激活的 `Agent` 通过其内部的 `ReActEngine` 开始工作。
8.  `ReActEngine` 调用 `ChatClientService` 与底层 LLM 通信，生成“思考”和“行动”计划。
9.  如果“行动”是调用工具，`ReActEngine` 会请求 `ToolExecutor` 来执行。
10. `ToolExecutor` 根据工具类型执行相应的工具，并返回结果。
11. `ReActEngine` 将工具结果提供给 LLM 进行下一轮“思考”，循环此过程直到 Agent 完成其子任务。
12. `Agent` 将其最终响应返回给编排策略。
13. 编排策略根据规则继续激活下一个 `Agent`，或在满足终止条件时结束流程。
14. `AgentOrchestratorService` 归纳总结最终结果并返回给 API 层。

## 3. 组件和接口

### 3.1. Agent (Runtime Concept)
- **`Agent` (Class)**: 这是一个在运行时动态构建的对象，而不是一个持久化的实体。
  - `Name`: `string` (来自 `AgentMember.Name`)
  - `Instructions`: `string` (来自 `LlmPrompt.Content`)
  - `Config`: `LlmConfig` (来自 `AgentMember.LlmConfig`)
  - `Tools`: `List<IAgentTool>` (该 Agent 可用的工具实例)
  - `InvokeAsync(List<ChatMessage> history)`: `Task<ChatMessage>` - Agent 的主调用方法。

### 3.2. ReActEngine
- **`ReActEngine.cs` (Class)**:
  - `_chatClientService`: `ChatClientService`
  - `_toolExecutor`: `ToolExecutor`
  - `ProcessAsync(Agent agent, List<ChatMessage> history)`: `Task<ChatMessage>` - 执行完整的 ReAct 循环。

### 3.3. 工具 (Tools)
- **`IAgentTool.cs` (Interface)**:
  - `Name`: `string`
  - `Description`: `string`
  - `GetParameters()`: `JsonDocument` (返回 OpenAI Function Calling 格式的参数 schema)
  - `InvokeAsync(JsonDocument arguments)`: `Task<string>` (执行工具并返回字符串结果)

- **`InternalPluginTool.cs` (Class implementing `IAgentTool`)**: 封装内部服务方法的工具。
- **`McpTool.cs` (Class implementing `IAgentTool`)**: 封装对外部 MCP Server 调用的工具。
- **`ContextExtractorTool.cs` (Class implementing `IAgentTool`)**: 实现后台上下文提取和记忆查询。

### 3.4. 编排 (Orchestration)
- **`AgentOrchestratorService.cs` (Class)**:
  - `InvokeAsync(LlmApp app, string userInput)`: `Task<OrchestrationResult>`

- **`IOrchestrationStrategy.cs` (Interface)**:
  - `ExecuteAsync(List<Agent> agents, string userInput, LlmApp settings)`: `Task<OrchestrationResult>`

- **`SequentialStrategy.cs` & `GroupChatStrategy.cs`**: `IOrchestrationStrategy` 的具体实现。

### 3.5. OpenAI 兼容 API 映射
- 统一对外 API 与 OpenAI `chat.completions` 兼容：
  - Endpoint: `POST /v1/chat/completions`
  - 请求体关键字段映射：
    - `model` -> 解析到 `LlmApp`（支持 `Prompt` 与 `AgentGroup` 两类 App 标识）
    - `messages` -> 作为对话历史传入；`user`/`system`/`assistant` 角色保持一致
    - `tools`/`tool_choice` -> 映射到可用工具与策略；当提供时可覆盖默认配置
    - `stream` -> 支持 SSE 流式返回，沿用现有 OpenAI 兼容控制器逻辑
  - 行为：
    - 当 `LlmApp.AppType=Prompt` 时，直接走现有 Prompt 推理路径
    - 当 `LlmApp.AppType=AgentGroup` 时，转到 `AgentOrchestratorService` 执行编排
  - 返回体：遵循 OpenAI `chat.completions` schema（含 `choices`, `usage`, `tool_calls` 等）

## 4. 数据模型

将在 `LY.LlmPool.Web.Data.Entities` 命名空间下修改和新增以下实体：

### 4.1. `LlmApp.cs` (修改)
```csharp
public class LlmApp
{
    // ... 现有字段 ...
    public string Name { get; set; }
    public string Description { get; set; }

    // --- 新增字段 ---
    public AppType AppType { get; set; } = AppType.Prompt;
    public OrchestrationMode? OrchestrationMode { get; set; } // 当 AppType 为 AgentGroup 时有效

    // --- 关系 ---
    // 当 AppType 为 Prompt 时，这两个字段有效
    public string? LlmPromptId { get; set; }
    public virtual LlmPrompt? LlmPrompt { get; set; }
    public string? LlmConfigId { get; set; }
    public virtual LlmConfig? LlmConfig { get; set; }

    // 当 AppType 为 AgentGroup 时，此集合有效
    public virtual ICollection<AgentMember> AgentMembers { get; set; } = new List<AgentMember>();
}

public enum AppType { Prompt, AgentGroup }
public enum OrchestrationMode { Sequential, GroupChat }
```

### 4.2. `AgentMember.cs` (新增)
```csharp
public class AgentMember
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    
    public string Name { get; set; } // Agent 在群组中的名称/身份
    public string Role { get; set; } // 在群聊模式下的角色
    public int Order { get; set; } // 在顺序模式下的执行顺序

    public string LlmAppId { get; set; } // 关联的 LlmApp (AgentGroup 类型)
    public virtual LlmApp LlmApp { get; set; }

    public string LlmPromptId { get; set; } // 定义 Agent 指令的 Prompt
    public virtual LlmPrompt LlmPrompt { get; set; }

    public string LlmConfigId { get; set; } // Agent 使用的 LLM 配置
    public virtual LlmConfig LlmConfig { get; set; }

    public virtual ICollection<AgentTool> AgentTools { get; set; } = new List<AgentTool>();
}
```

### 4.3. `McpServerConfig.cs` (不变)
```csharp
public class McpServerConfig
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; }
    public string Url { get; set; }
    public string Description { get; set; }
    
    [Column(TypeName = "jsonb")]
    public string? SchemaCacheJson { get; set; } // 缓存工具定义
}
```

### 4.4. `AgentTool.cs` (修改)
```csharp
public class AgentTool
{
    public string AgentMemberId { get; set; } // 关联到 AgentMember
    public virtual AgentMember AgentMember { get; set; }

    public string ToolId { get; set; } // 工具的唯一标识符
    public ToolType ToolType { get; set; } // Enum: Internal, Mcp
}

public enum ToolType { Internal, Mcp }
```
**注意**: `AgentTool.ToolId` 对于内部工具，可以是方法的唯一名称（如 `MyPlugin.GetWeather`）；对于 MCP 工具，可以是 `McpServerConfig` 的 ID。

## 5. 错误处理

- **工具调用失败**: `ToolExecutor` 在执行工具时，如果发生异常（如网络错误、反序列化失败），应捕获异常并将其信息格式化为一段文本返回给 `ReActEngine`。Agent 应能理解这是一个错误信息，并在下一轮“思考”中决定如何处理（例如，重试、更换工具或向用户报告失败）。
- **编排循环**: `AgentOrchestratorService` 必须实现对最大交互轮次的检查，防止无限循环。达到限制后，应强制终止流程并返回一个包含错误信息的结果。
- **配置错误**: 在加载 Agent 或编排流程时，如果发现配置不完整（如 Agent 关联的 App 不存在），应抛出可识别的配置异常。

## 6. 测试策略

- **单元测试**:
  - `ReActEngine`: 模拟 `ChatClientService` 和 `ToolExecutor` 的返回，测试其能否正确解析 LLM 的意图（调用工具 vs. 回复）并执行相应操作。
  - `SequentialStrategy` / `GroupChatStrategy`: 模拟 `Agent` 的返回，测试编排逻辑是否正确（如调用顺序、历史记录维护）。
  - `McpTool`: 模拟 `HttpClient`，测试其能否正确构建 MCP 请求并解析响应。
- **集成测试**:
  - 创建一个包含多个 Mock Agent 的 `AgentGroup`，通过 `AgentOrchestratorService` 调用，端到端地测试整个编排流程。
  - 创建一个真实的、简单的 MCP Server，并配置 `McpServerConfig`，测试 `McpTool` 的实际调用能力。
- **UI 测试**: (手动)
  - 在管理界面创建、修改和删除 `Agent`、`AgentGroup` 和 `McpServerConfig`，验证数据持久化和界面交互的正确性。
