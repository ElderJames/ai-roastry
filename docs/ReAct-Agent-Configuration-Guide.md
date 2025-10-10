# ReAct Agent 配置与测试指南

## 概述

本文档介绍如何在 LY.LlmPool 系统中配置和测试 ReAct（Reasoning and Acting）Agent 应用。ReAct 是一种将推理（Reasoning）和行动（Acting）结合的 AI Agent 模式，通过多个专业 Agent 的协作来完成复杂任务。

**系统支持的编排模式**：
- ✅ **Sequential（顺序）**：Agent 按序执行，前一个输出作为后一个输入
- ✅ **GroupChat（多轮对话）**：多个 Agent 按角色优先级轮流发言
- ✅ **DAG（有向无环图）**：支持依赖关系、条件分支、并行执行的复杂工作流

## 体系结构总览（DAG + ReAct）

为了帮助快速理解 DAG + ReAct 的实现方式，下面从代码与配置两个角度给出关键要点：

1. **核心服务分层**
   - `Services/Agents/AgentOrchestratorService` 会根据 `LlmApp.OrchestrationMode` 选择 `SequentialStrategy`、`GroupChatStrategy` 或 `DAGStrategy`，因此只要应用类型设为 `AgentGroup` 并指定 `OrchestrationMode = OrchestrationMode.DAG`，就会进入 DAG 执行路径。
   - `DAGStrategy` 负责真正的节点编排：解析 `LlmApp.ConfigJson` 中的 `DAGWorkflow`（全局并行度 / 超时 / 失败策略）与每个 `AgentMember.ConfigJson` 中的节点依赖、条件、超时等，然后拓扑排序、分层并发执行。

2. **数据存储模型**
   - 应用实体 `LlmApp`（`Data/Entities/LlmApp.cs`）保存整体配置与关联的 `AgentMembers`；成员实体 `AgentMember` 则引用 Prompt、模型配置以及可选的 DAG 节点配置 `ConfigJson`。
   - 向导组件 `AgentGroupWizard` 生成的成员列表会在 `AppList` 页面保存时写入数据库，并把可读的 Prompt/模型名称映射回 GUID，确保后端能够关联正确资源。

3. **运行时上下文**
   - DAG 执行过程中，`WorkflowExecutionContext` 会缓存用户原始消息、每个节点的输出结果以及最新的全局上下文；后续节点可以通过依赖列表直接获取前序节点输出。
   - 条件判断采用 `WorkflowCondition`，支持 `contains`、`equals`、`regex` 等类型，并允许引用 `result`（最新全局结果）或 `node.{nodeId}.output`（指定节点输出）。

4. **ReAct 角色落位**
   - Planner、Researcher、Aggregator、Reviewer 等角色以 `AgentMember` 的形式出现，通过 Prompt 来限定角色能力，通过 DAG 依赖控制执行顺序：Planner → 多个 Researcher 并行 → Aggregator 汇总 → Reviewer 审核。
   - 结合 DAG 的并行与条件能力，可以扩展更多职能（如 RiskAnalyzer、Tool Executor），并通过 `ParallelGroup` 控制哪些节点并发执行。

理解上述结构后，只需准备好 Prompt、模型配置以及 DAG 节点依赖，即可在向导或脚本中快速拼装一个 ReAct 应用。

## ReAct 工作原理

ReAct 模式的核心思想：
1. **Planner（规划者）**：分析用户请求，制定执行计划，分解任务步骤
2. **Reviewer（审查者）**：审查 Planner 的输出，验证计划的合理性，提供反馈和改进建议
3. **Executor（执行者）**：根据计划执行具体任务，调用工具获取信息或完成操作
4. **编排模式**：
   - **Sequential**：按顺序执行 Agent，前一个 Agent 的输出作为后一个 Agent 的上下文
   - **DAG**：支持复杂依赖关系、条件分支、并行执行

### Sequential 工作流程
```
用户请求 → Planner (制定计划) → Reviewer (审查计划) → 最终输出
```

### DAG 工作流程
```
用户请求
   ↓
Planner (制定计划)
   ↓
   ├─→ Researcher1 (并行)  ─┐
   ├─→ Researcher2 (并行)  ─┤
   └─→ Researcher3 (并行)  ─┘
          ↓
   Aggregator (汇总结果)
          ↓
   Reviewer (审查) → 最终输出
```

## 编排模式详解

### 1. Sequential（顺序编排）

**特点**：
- Agent 按 Order 字段依次执行
- 每个 Agent 接收前一个 Agent 的输出作为上下文
- 适合线性流程，如 Planner → Reviewer

**配置示例**：无需额外配置，只需设置 Agent 的 Order 字段。

### 2. GroupChat（多轮对话）

**特点**：
- 多个 Agent 按角色优先级轮流发言
- 支持多轮交互，直到达到最大轮次或遇到 FINAL 标记
- 适合需要多方协商的场景

**配置示例**：
```json
{
  "GroupChatMaxRounds": 3,
  "GroupChatStopOnFinal": true,
  "GroupChatRoleOrder": "planner,researcher,moderator",
  "GroupChatContextLimit": 50
}
```

### 3. DAG（有向无环图工作流）⭐

**特点**：
- 支持复杂的依赖关系：某个 Agent 可依赖多个前置 Agent 的输出
- 支持条件分支：根据执行结果决定是否执行某个 Agent
- 支持并行执行：相同 ParallelGroup 的 Agent 可同时执行
- 自动拓扑排序验证 DAG 有效性

**适用场景**：
- 复杂的多步骤工作流
- 需要并行处理的任务（如多个 Researcher 同时搜索）
- 需要条件判断的场景（如根据 Planner 输出决定执行路径）

**核心配置项**（存储在 `AgentMember.ConfigJson`）：
```json
{
  "Dependencies": ["agent-id-1", "agent-id-2"],
  "Condition": {
    "Type": "contains",
    "Field": "result",
    "Value": "success"
  },
  "ParallelGroup": "research-team",
  "TimeoutSeconds": 300,
  "ContinueOnFailure": false
}
```

**配置字段说明**：
- `Dependencies`：依赖的前置节点 ID 列表
- `Condition`：执行条件（可选）
  - `Type`: contains, equals, regex, custom
  - `Field`: result, node.{nodeId}.output
  - `Value`: 比较值
- `ParallelGroup`：并行组 ID，相同组的节点可并行执行
- `TimeoutSeconds`：节点超时时间（秒）
- `ContinueOnFailure`：失败时是否继续

**应用级 DAG 配置**（存储在 `LlmApp.Config["DAGWorkflow"]`）：
```json
{
  "DAGWorkflow": {
    "MaxParallelism": 3,
    "GlobalTimeoutSeconds": 600,
    "FailureStrategy": "stop",
    "RetryCount": 0
  }
}
```

## DAG 工作流示例

### 场景：复杂项目规划与执行

**任务**：用户要求规划一个电商项目，需要多方面调研并汇总分析。

**工作流设计**：
```
用户请求
   ↓
Planner (制定调研方向)
   ↓
   ├─→ TechResearcher (技术栈调研, ParallelGroup=research)
   ├─→ MarketResearcher (市场分析, ParallelGroup=research)
   └─→ CompetitorResearcher (竞品分析, ParallelGroup=research)
          ↓
   Aggregator (汇总调研结果)
          ↓
   RiskAnalyzer (风险评估, 条件: 如果 Aggregator 发现问题)
          ↓
   Reviewer (最终审查) → 输出
```

**Agent 成员配置**：

1. **Planner** (Order=1)
   ```json
   {
     "Dependencies": [],
     "ParallelGroup": null
   }
   ```

2. **TechResearcher** (Order=2)
   ```json
   {
     "Dependencies": ["planner-id"],
     "ParallelGroup": "research-team",
     "TimeoutSeconds": 180
   }
   ```

3. **MarketResearcher** (Order=3)
   ```json
   {
     "Dependencies": ["planner-id"],
     "ParallelGroup": "research-team",
     "TimeoutSeconds": 180
   }
   ```

4. **CompetitorResearcher** (Order=4)
   ```json
   {
     "Dependencies": ["planner-id"],
     "ParallelGroup": "research-team",
     "TimeoutSeconds": 180
   }
   ```

5. **Aggregator** (Order=5)
   ```json
   {
     "Dependencies": ["tech-researcher-id", "market-researcher-id", "competitor-researcher-id"],
     "TimeoutSeconds": 120
   }
   ```

6. **RiskAnalyzer** (Order=6)
   ```json
   {
     "Dependencies": ["aggregator-id"],
     "Condition": {
       "Type": "contains",
       "Field": "result",
       "Value": "risk"
     },
     "TimeoutSeconds": 120
   }
   ```

7. **Reviewer** (Order=7)
   ```json
   {
     "Dependencies": ["aggregator-id", "risk-analyzer-id"],
     "ContinueOnFailure": true
   }
   ```

### DAG 执行流程

1. **用户请求** → 系统接收
2. **拓扑排序** → 验证 DAG 无环，生成执行顺序
3. **分层执行**：
   - Level 0: Planner（无依赖）
   - Level 1: TechResearcher, MarketResearcher, CompetitorResearcher（并行执行，依赖 Planner）
   - Level 2: Aggregator（依赖三个 Researcher）
   - Level 3: RiskAnalyzer（条件执行，依赖 Aggregator）
   - Level 4: Reviewer（依赖 Aggregator 和 RiskAnalyzer）
4. **结果汇总** → 返回最终输出

## 配置步骤

### 1. 前置准备

确保系统中已配置：
- ✅ **模型配置**：至少一个可用的 LLM 配置（如 Qwen3-235B）
- ✅ **提示词配置**：为 Planner 和 Reviewer 角色创建专用 prompt

### 2. 创建 Prompt（提示词）

#### 2.1 Planner Prompt 示例

```
Name: ReAct-Planner
Description: 任务规划专家，负责分解用户请求并制定执行计划

Content:
你是一个任务规划专家（Planner）。你的职责是：

1. **理解用户需求**：仔细分析用户的请求，识别关键目标和约束条件
2. **任务分解**：将复杂任务分解为清晰的、可执行的步骤序列
3. **制定计划**：
   - 为每个步骤标注序号
   - 说明每步的目的和预期结果
   - 考虑步骤之间的依赖关系
4. **输出格式**：
   ```
   ## 任务分析
   [简要说明对用户请求的理解]

   ## 执行计划
   1. [步骤1描述] - 目的：[说明]
   2. [步骤2描述] - 目的：[说明]
   ...

   ## 预期结果
   [说明完成后的期望状态]
   ```

请基于用户的请求制定详细的执行计划。
```

#### 2.2 Reviewer Prompt 示例

```
Name: ReAct-Reviewer
Description: 方案审查专家，负责评估和优化计划

Content:
你是一个方案审查专家（Reviewer）。你的职责是：

1. **审查计划**：仔细检查 Planner 提供的执行计划
2. **评估质量**：从以下维度评估：
   - ✅ **完整性**：是否涵盖所有必要步骤
   - ✅ **可行性**：每个步骤是否实际可执行
   - ✅ **合理性**：步骤顺序是否逻辑清晰
   - ✅ **风险识别**：是否存在潜在问题或遗漏
3. **提供反馈**：
   - 指出计划中的优点
   - 明确指出问题和改进空间
   - 提供具体的优化建议
4. **输出格式**：
   ```
   ## 计划评估

   ### 优点
   - [列出计划的亮点]

   ### 问题与风险
   - [列出发现的问题]

   ### 改进建议
   1. [具体建议1]
   2. [具体建议2]

   ### 最终评价
   [总体评价：通过/需修改/不可行]
   ```

请对上述计划进行专业审查。
```

### 3. 使用 AgentGroup 向导创建应用

#### 3.1 启动向导

1. 访问 **应用管理** 页面
2. 点击 **AgentGroup 向导** 按钮
3. 或在创建应用时选择 Type = "AgentGroup"，然后点击 **使用 AgentGroup 向导配置**

#### 3.2 配置成员

**步骤 1：选择编排模式**
- 编排模式：选择 **Sequential**（顺序执行）、**GroupChat**（多轮对话）或 **DAG**（有向无环图）
- 对于 ReAct 模式，Sequential 适合简单流程，DAG 适合复杂工作流
- GroupChat 需要配置最大轮次、角色顺序等参数

**步骤 2：添加 Agent 成员**

第一个成员（Planner）：
- **序号**：1
- **名称**：Planner（可空，自动填充为 "Agent 1"）
- **角色**：planner
- **Prompt**：选择 "ReAct-Planner"
- **模型配置**：选择可用的模型（如 "Qwen3-235B"）

第二个成员（Reviewer）：
- **序号**：2
- **名称**：Reviewer（可空，自动填充为 "Agent 2"）
- **角色**：reviewer
- **Prompt**：选择 "ReAct-Reviewer"
- **模型配置**：选择可用的模型（如 "Qwen3-235B"）

**步骤 3：确认创建**
- 检查成员信息
- 点击 **确认** 完成向导配置

#### 3.3 保存应用

向导完成后返回应用编辑页面：
1. **Name**：输入应用名称（如 "react-demo-agent-group"）
2. **Type**：AgentGroup（已自动设置）
3. **Description**：输入描述（如 "ReAct agent group demo with planner and reviewer"）
4. **Prompt**：可选，应用级别的 prompt
5. **Configuration**：选择模型配置或端点配置
6. **Enabled**：勾选启用
7. 点击 **保存**

### 4. 代码实现细节

#### 4.1 关键修复

在向导集成过程中，我们修复了一个关键问题：

**问题**：AgentGroupWizard 返回的成员数据中，`LlmPromptId` 和 `LlmConfigId` 字段存储的是显示名称（Name）而非 GUID，导致保存时违反外键约束。

**解决方案**（AppList.razor）：

```csharp
private async Task HandleWizardCreated(AgentGroupWizardResult? result)
{
    _showWizardDialog = false;
    if (result == null) return;

    // 重新加载 prompt 和 config 数据
    await LoadSelectDataAsync();

    _members = result.Members ?? new List<AgentMember>();

    // 规范化 ID：将名称映射回 GUID
    foreach (var member in _members)
    {
        // 处理 LlmConfigId
        if (string.IsNullOrWhiteSpace(member.LlmConfigId))
        {
            member.LlmConfigId = null;
        }
        else if (!_llm_configs.Any(c => c.Id == member.LlmConfigId))
        {
            // 不是有效 ID，尝试按名称查找
            var config = _llm_configs.FirstOrDefault(c => 
                string.Equals(c.Name, member.LlmConfigId, StringComparison.OrdinalIgnoreCase));
            member.LlmConfigId = config?.Id;
        }

        // 处理 LlmPromptId（同理）
        if (string.IsNullOrWhiteSpace(member.LlmPromptId))
        {
            member.LlmPromptId = null;
        }
        else if (!_prompts.Any(p => p.Id == member.LlmPromptId))
        {
            var prompt = _prompts.FirstOrDefault(p => 
                string.Equals(p.Name, member.LlmPromptId, StringComparison.OrdinalIgnoreCase));
            member.LlmPromptId = prompt?.Id;
        }
    }

    // ... 继续处理
}
```

#### 4.2 数据库结构

**AgentMember 实体**：
```csharp
public class AgentMember
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Role { get; set; }
    public int Order { get; set; }
    
    public string LlmAppId { get; set; }          // 所属应用
    public string? LlmPromptId { get; set; }      // 提示词（FK）
    public string? LlmConfigId { get; set; }      // 模型配置（FK）
    
    // 导航属性
    public virtual LlmApp LlmApp { get; set; }
    public virtual LlmPrompt? LlmPrompt { get; set; }
    public virtual LlmConfig? LlmConfig { get; set; }
}
```

## 测试方法

### 1. UI 测试（推荐）

1. **启动应用**
   ```bash
   cd src/LY.LlmPool.Web
   dotnet run
   ```

2. **访问测试页面**
   - 打开浏览器：http://localhost:5071/app-list
   - 找到 "react-demo-agent-group" 应用
   - 点击 **Test** 按钮

3. **输入测试查询**
   
   示例 1：简单任务规划
   ```
   帮我规划一个周末学习计划，我想学习 Python 编程基础
   ```

   预期流程：
   - Planner 分析需求，制定学习计划（包括时间分配、主题安排等）
   - Reviewer 审查计划，提出优化建议（如增加实践环节、调整难度等）

   示例 2：复杂项目规划
   ```
   我要开发一个在线书店系统，需要什么技术栈和开发步骤？
   ```

   预期流程：
   - Planner 分析需求，列出技术选型、开发阶段、关键功能模块
   - Reviewer 评估可行性，指出潜在技术风险，建议改进架构设计

### 2. API 测试

使用 OpenAI 兼容 API 调用：

```bash
curl -X POST http://localhost:5071/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "react-demo-agent-group",
    "messages": [
      {
        "role": "user",
        "content": "帮我规划一个3天的北京旅游行程"
      }
    ],
    "stream": false
  }'
```

### 3. 单元测试

参考 `tests/LY.LlmPool.Web.Tests/AgentGroupE2eTests.cs`：

```csharp
[Fact]
public async Task ReActAgentGroup_Should_Execute_Sequential_Flow()
{
    // Arrange
    var app = await CreateReActAgentGroup();
    
    // Act
    var result = await ExecuteAgentGroup(app.Id, "制定学习计划");
    
    // Assert
    Assert.Contains("计划", result);
    Assert.Contains("审查", result);
}
```

## 预期输出示例

### 用户输入
```
帮我规划一个周末学习 Python 编程的计划
```

### Planner 输出
```
## 任务分析
用户希望在周末（2天）学习 Python 编程基础，需要一个结构化、可执行的学习计划。

## 执行计划
1. **周六上午（3小时）**：Python 环境搭建 + 基础语法
   - 安装 Python 3.x 和 VS Code
   - 学习变量、数据类型、运算符
   - 目的：建立开发环境，掌握基本语法

2. **周六下午（3小时）**：控制流程和函数
   - if/else、for/while 循环
   - 函数定义和调用
   - 目的：理解程序流程控制

3. **周日上午（3小时）**：数据结构
   - 列表、元组、字典、集合
   - 目的：掌握 Python 核心数据结构

4. **周日下午（3小时）**：实战项目
   - 编写一个简单的猜数字游戏
   - 目的：巩固所学知识，培养编程思维

## 预期结果
完成 12 小时学习后，能够编写简单的 Python 程序，理解基本编程概念。
```

### Reviewer 输出
```
## 计划评估

### 优点
- ✅ 时间分配合理，每天 6 小时适合初学者
- ✅ 内容循序渐进，从环境到语法到实践
- ✅ 包含实战项目，有助于知识巩固

### 问题与风险
- ⚠️ 缺少休息时间安排，连续学习可能导致疲劳
- ⚠️ 没有配置学习资源（教程、文档链接）
- ⚠️ 实战项目难度未明确，可能超出初学者能力

### 改进建议
1. **增加休息**：每 1.5 小时休息 15 分钟
2. **推荐资源**：
   - 官方教程：python.org/tutorial
   - 练习平台：leetcode.com/problemset
3. **调整项目**：猜数字游戏难度适中，建议补充需求文档
4. **添加检查点**：每个阶段结束后做小测试

### 最终评价
✅ **通过（需小幅调整）**
计划整体可行，补充上述建议后可执行性更强。
```

## 常见问题 (FAQ)

### Q1: 为什么 Agent 成员保存失败？

**A**: 常见原因是外键约束违反：
- 确保 `LlmPromptId` 和 `LlmConfigId` 是有效的 GUID，而非显示名称
- 检查数据库中是否存在对应的 Prompt 和 Config 记录
- 查看错误日志中的 FK constraint 详情

### Q2: 如何查看 Agent 执行日志？

**A**: 
1. 访问 **请求日志** 页面
2. 筛选 app_name = "react-demo-agent-group"
3. 查看每个 Agent 的输入输出和执行时间

### Q3: Planner 和 Reviewer 的输出格式不一致怎么办？

**A**: 
- 在 Prompt 中明确要求输出格式（使用 Markdown 标题）
- 可以在应用级别添加后处理逻辑统一格式
- 使用 System Message 强化格式要求

### Q4: 如何配置 DAG 工作流？

**A**: 
1. **选择编排模式**：在向导中选择 "DAG"
2. **配置成员依赖**：
   ```json
   {
     "Dependencies": ["前置agent-id-1", "前置agent-id-2"]
   }
   ```
3. **设置并行组**：相同 ParallelGroup 的 Agent 可并行执行
4. **添加条件**：根据执行结果决定是否执行
5. **应用级配置**：设置最大并行度、全局超时等

详见 [DAG 工作流示例](#dag-工作流示例)。

### Q5: 如何扩展到更多 Agent？

**A**: 
1. 在向导中继续添加成员（如 Executor、Monitor）
2. Sequential 模式：调整 Order 字段控制执行顺序
3. GroupChat 模式：添加到角色列表并配置优先级
4. **DAG 模式**：配置依赖关系和并行组，系统自动编排

### Q6: DAG 模式支持哪些高级特性？

**A**:
- ✅ **依赖管理**：一个 Agent 可依赖多个前置 Agent
- ✅ **并行执行**：相同 ParallelGroup 的 Agent 并行运行
- ✅ **条件分支**：支持 contains, equals, regex 条件判断
- ✅ **超时控制**：节点级和全局级超时
- ✅ **失败处理**：stop, continue, retry 策略
- ✅ **拓扑排序**：自动检测环和优化执行顺序

### Q7: 性能优化建议？

**A**: 
1. **模型选择**：Planner 用大模型，Reviewer 可用小模型
2. **缓存策略**：对常见问题缓存 Planner 输出
3. **并行执行**：使用 DAG 模式并行执行独立任务（推荐⭐）
4. **Stream 模式**：启用流式输出提升用户体验
5. **超时控制**：合理设置节点超时避免长时间等待
6. **并行度限制**：DAG 模式中设置 MaxParallelism 避免资源耗尽

## 技术栈

- **后端框架**：ASP.NET Core 9.0 + Blazor Server
- **数据库**：PostgreSQL (EF Core)
- **UI 框架**：Ant Design Blazor
- **LLM 接口**：OpenAI Compatible API
- **编排引擎**：AgentOrchestratorService

## ReAct DAG 演示页面

系统内置了一个完整的 ReAct DAG 演示应用，展示 Planner → Researcher (并行) → Aggregator → Reviewer 的典型工作流。

### 访问演示页面

1. **启动应用**
   ```bash
   cd src/LY.LlmPool.Web
   dotnet run
   ```

2. **打开浏览器访问**
   - URL: http://localhost:5071/react-dag-demo
   - 或在侧边栏点击 **ReAct DAG 演示**

### 演示页面功能

✅ **实时执行**：真实调用 Agent App，观察 DAG 编排过程  
✅ **模拟模式**：无需真实 LLM 也可体验完整流程  
✅ **执行轨迹**：时间线展示每个 Agent 的输出和执行时间  
✅ **最终总结**：显示 Reviewer 审核后的完整结论  
✅ **API 示例**：展示如何通过 OpenAI-compatible API 调用

### 通过 API 调用演示应用

演示应用注册名称为 `ReAct-DAG-Demo`，可通过标准 OpenAI-compatible API 调用：

#### 非流式调用

```bash
curl -X POST http://localhost:5071/api/openai/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "ReAct-DAG-Demo",
    "messages": [
      {
        "role": "user",
        "content": "帮我评估一个跨境电商 SaaS 的技术方案与市场可行性"
      }
    ],
    "stream": false
  }'
```

#### 流式调用（推荐）⭐

```bash
curl -X POST http://localhost:5071/api/openai/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "ReAct-DAG-Demo",
    "messages": [
      {
        "role": "user",
        "content": "帮我评估一个跨境电商 SaaS 的技术方案与市场可行性"
      }
    ],
    "stream": true
  }'
```

流式调用会返回 Server-Sent Events (SSE)，可实时查看每个 Agent 的执行进度：
```
data: {"id":"...","choices":[{"delta":{"content":"[Planner] ## 任务分析..."}}]}
data: {"id":"...","choices":[{"delta":{"content":"[TechResearcher] ### 技术评估..."}}]}
data: {"id":"...","choices":[{"delta":{"content":"[MarketResearcher] ### 市场洞察..."}}]}
data: {"id":"...","choices":[{"delta":{"content":"[Aggregator] ### 关键结论..."}}]}
data: {"id":"...","choices":[{"delta":{"content":"[Reviewer] ## 评估总结..."}}]}
data: [DONE]
```

### Python SDK 调用示例

使用 `openai` Python 包调用：

```python
from openai import OpenAI

client = OpenAI(
    base_url="http://localhost:5071/api/openai",
    api_key="not-needed"  # 如果未启用认证
)

response = client.chat.completions.create(
    model="ReAct-DAG-Demo",
    messages=[
        {
            "role": "user",
            "content": "帮我评估一个跨境电商 SaaS 的技术方案与市场可行性"
        }
    ],
    stream=True
)

for chunk in response:
    if chunk.choices[0].delta.content:
        print(chunk.choices[0].delta.content, end='', flush=True)
```

### 演示应用架构

演示应用由 `ExampleAppService` 自动创建和维护，包含以下 Agent 成员：

1. **Planner** (planner)
   - 职责：分析用户需求，拆解子任务
   - 依赖：无（Level 0）

2. **TechResearcher** (researcher.tech) 
   - 职责：评估技术架构选型、集成难度
   - 依赖：Planner
   - 并行组：`research`

3. **MarketResearcher** (researcher.market)
   - 职责：分析市场机会、竞品、客群
   - 依赖：Planner
   - 并行组：`research`（与 TechResearcher 并行执行）

4. **Aggregator** (aggregator)
   - 职责：汇总多个 Researcher 结果并提炼洞察
   - 依赖：TechResearcher + MarketResearcher

5. **Reviewer** (reviewer)
   - 职责：审查完整方案，给出最终建议
   - 依赖：Aggregator
   - 输出：`FINAL:` 开头的结论

### 自定义演示应用

演示应用代码位于 `Services/ExampleAppService.cs`，你可以：

1. **修改 Prompt**：调整各 Agent 的角色定义
2. **调整依赖**：改变 DAG 工作流结构
3. **增加成员**：添加新的 Researcher 或分析器
4. **配置参数**：修改并行度、超时时间等

示例：添加 RiskAnalyzer
```csharp
new()
{
    Id = riskAnalyzerId,
    LlmAppId = app.Id!,
    Name = "RiskAnalyzer",
    Role = "risk.analyzer",
    Order = 5,
    LlmPromptId = promptMap["ReAct-RiskAnalyzer"],
    LlmConfigId = configId,
    ConfigJson = JsonSerializer.Serialize(new AgentMemberDAGConfig
    {
        Dependencies = new List<string> { aggregatorId },
        Condition = new WorkflowCondition 
        { 
            Type = "contains", 
            Field = "result", 
            Value = "风险" 
        },
        TimeoutSeconds = 180
    })
}
```

## 相关文档

- [AgentGroup 编排模式详解](./AgentGroup-Orchestration.md)
- [Prompt 设计最佳实践](./Prompt-Best-Practices.md)
- [API 参考文档](./API-Reference.md)

## 更新日志

- **2025-10-10**:
  - **新增 ReAct DAG 演示页面** (`/react-dag-demo`)：
    - 可视化展示 Planner → Researcher(并行) → Aggregator → Reviewer 工作流
    - 支持真实 LLM 调用和模拟模式双模式
    - 实时执行轨迹展示（Timeline 组件）
    - 集成 API 调用示例和端点说明
  - **新增 ExampleAppService**：
    - 自动创建/更新演示 Agent App
    - 管理 Prompt 创建和 DAG 配置
    - 支持通过 OpenAI-compatible API 外部调用
  - **文档增强**：
    - 添加演示应用架构说明
    - 补充 API 调用示例（cURL、Python SDK）
    - 提供自定义演示应用的指导

- **2025-10-09**: 
  - 初始版本，完成 ReAct Agent 配置和测试指南
  - 修复了向导成员 ID 映射问题
  - 添加了完整的配置步骤和示例代码
  - **新增 DAG 工作流支持**：
    - 扩展 OrchestrationMode 枚举，添加 DAG 模式
    - 创建 WorkflowModels.cs 定义 DAG 配置模型
    - 实现 DAGStrategy.cs 编排策略（拓扑排序、依赖解析、并行执行、条件分支）
    - 增强 AgentMember 实体支持 DAG 配置（ConfigJson 存储）
    - 更新文档添加 DAG 工作流配置说明和示例

---

**作者**: LY.LlmPool 开发团队  
**最后更新**: 2025-10-10
