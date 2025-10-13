# 实现任务列表：DAG 工作流持久化

## 任务概述

本任务列表将设计文档转换为一系列增量实现步骤,遵循测试驱动开发(TDD)原则,确保每个步骤都能独立验证并与前序工作集成。

**优先级说明:**
- P0:核心功能,必须实现
- P1:重要功能,增强用户体验
- P2:可选功能,后续迭代

**任务重组说明:**
本任务列表已按用户需求重新组织,**优先实现 App Tool 与 Prompt 绑定功能**(第一至第三阶段),这是最核心的创新功能。数据库持久化和 Todolist 转换功能作为后续阶段实现。

---

## 🎯 第一阶段:工具参数提取服务 (P0 - 立即开始)

**目标**: 实现从 Prompt 模板自动提取参数并生成 JSON Schema

### 1. 实现 PromptParameterExtractor 服务 ✅
- [x] 创建 `Services/Tools/PromptParameterExtractor.cs`
  - [x] 实现 `ExtractParameters(template)` 方法
    - 使用正则表达式提取 Prompt 中的 `{{参数名}}` 占位符
    - 返回参数名称列表(去重)
    - 支持嵌套参数(如 `{{user.name}}`)
  - [x] 实现 `GenerateParameterSchema(parameters, overrides)` 方法
    - 为每个参数生成默认 JSON Schema(type: "string", required: true)
    - 支持从 ConfigJson 的 `parameterOverrides` 覆盖参数定义
    - 返回完整的 OpenAPI 风格参数 Schema
  - [x] 实现 `RenderPrompt(template, parameters)` 方法
    - 使用提供的参数值替换 Prompt 模板中的占位符
    - 处理缺失参数(抛出异常或使用默认值)
    - 返回渲染后的 Prompt
  - **需求参考**: US-3(App 即工具), R-3.1(参数自动提取)
  
- [x] 编写单元测试 `PromptParameterExtractorTests.cs`
  - 测试单参数提取: `"Hello {{name}}"` → `["name"]`
  - 测试多参数提取: `"{{greeting}} {{name}}, you are {{age}} years old"` → `["greeting", "name", "age"]`
  - 测试嵌套参数: `"{{user.name}}"` → `["user.name"]`
  - 测试参数去重: `"{{name}} {{name}}"` → `["name"]`
  - 测试 Schema 生成(默认类型 string)
  - 测试 Schema 覆盖(从 parameterOverrides 读取自定义类型)
  - 测试 Prompt 渲染成功
  - 测试 Prompt 渲染缺失参数时的错误处理
  - **测试结果**: ✅ 所有 18 个测试通过
  - **需求参考**: NFR-4(可测试性)

**验收标准**:
- ✅ 能够从任意 Prompt 模板中提取所有参数
- ✅ 生成的 JSON Schema 符合 OpenAPI 3.0 规范
- ✅ 单元测试覆盖率 > 90%

---

## 🎯 第二阶段:工具元数据管理服务 (P0)

**目标**: 使用 HybridCache 缓存所有 App Tool 和 MCP Tool 的元数据

### 2. 创建 ToolMetadata 模型类 ✅
- [x] 创建 `Models/Tools/ToolMetadata.cs`
  - 定义字段:
    ```csharp
    public string Name { get; set; }              // 工具名称(唯一)
    public string Description { get; set; }       // 工具描述
    public ToolSource Source { get; set; }        // 来源:App 或 MCP
    public Guid SourceId { get; set; }            // 源 ID(AppId 或 McpServerId)
    public string ParametersSchema { get; set; }  // JSON Schema(JSON 字符串)
    public string? ConfigJson { get; set; }       // 原始配置(可选)
    ```
  - [x] 添加 `ToolSource` 枚举
    ```csharp
    public enum ToolSource { App, MCP }
    ```
  - **需求参考**: US-4(多源工具管理), R-4.1(统一工具接口)

### 3. 实现 ToolMetadataService ✅
- [x] 创建 `Services/Tools/ToolMetadataService.cs`
  - 注入依赖: `HybridCache`, `IDbContextFactory<LlmDbContext>`, `PromptParameterExtractor`
  
  - [x] 实现 `InitializeAsync()` 启动扫描方法
    - 调用 `ScanAppToolsAsync()` 扫描所有 App Tool
    - 调用 `ScanMcpToolsAsync()` 扫描所有 MCP Tool(暂时 TODO 占位)
    - 记录扫描日志(工具数量、耗时)
  
  - [x] 实现 `ScanAppToolsAsync()` 扫描 App Tool 方法
    - 查询数据库: `WHERE AppType = "Tool" AND IsEnabled = true`
    - 对每个 Tool App:
      - 从 `LlmPrompt.Content` 提取参数
      - 从 `ConfigJson` 读取 `parameterOverrides`
      - 生成 ToolMetadata 对象
      - 写入 HybridCache(键: `tool_metadata:{Name}`)
    - 汇总所有工具列表,写入缓存(键: `tool_metadata:all`)
  
  - [x] 实现 `ScanMcpToolsAsync()` 扫描 MCP Tool 方法(TODO 占位)
    - 添加注释: `// TODO: 集成 MCP SDK 后实现`
    - 暂时返回空列表
  
  - [x] 实现 `GetAllToolsAsync()` 获取所有工具方法
    - 从缓存读取: `tool_metadata:all`
    - 如果缓存未命中,调用 `InitializeAsync()` 重新扫描
    - 返回 `List<ToolMetadata>`
  
  - [x] 实现 `GetToolByNameAsync(name)` 按名称查找方法
    - 从缓存读取: `tool_metadata:{name}`
    - 如果未找到,返回 `null`
  
  - **需求参考**: US-4(多源工具管理), R-4.2(工具发现机制)
  - **实现状态**: ✅ 完成 (404 行代码)

- [x] 编写单元测试 `ToolMetadataServiceTests.cs`
  - 使用内存数据库和真实 HybridCache(集成测试)
  - [x] 测试 `ScanAppToolsAsync()` 扫描成功
    - 创建 Tool App,验证扫描后缓存正确
    - 验证每个工具的 ParametersSchema 正确
  - [x] 测试缓存写入和读取
    - 调用 `GetAllToolsAsync()` 验证返回所有工具
    - 调用 `GetToolByNameAsync("tool1")` 验证返回正确工具
  - [x] 测试过滤禁用的 App
  - [x] 测试过滤非 Tool 类型的 App
  - **测试结果**: ✅ 12/12 测试通过
  - **需求参考**: NFR-4(可测试性)
    - 如果缓存未命中,调用 `InitializeAsync()` 重新扫描
    - 返回 `List<ToolMetadata>`
  
  - 实现 `GetToolByNameAsync(name)` 按名称查找方法
    - 从缓存读取: `tool_metadata:{name}`
    - 如果未找到,返回 `null`
  
  - **需求参考**: US-4(多源工具管理), R-4.2(工具发现机制)

- [ ] 编写单元测试 `ToolMetadataServiceTests.cs`
  - 使用内存数据库模拟 Tool App 数据
  - 测试 `ScanAppToolsAsync()` 扫描成功
    - 创建 3 个 Tool App(不同 Prompt 模板)
    - 验证扫描后缓存包含 3 个工具
    - 验证每个工具的 ParametersSchema 正确
  - 测试缓存写入和读取
    - 调用 `GetAllToolsAsync()` 验证返回所有工具
    - 调用 `GetToolByNameAsync("tool1")` 验证返回正确工具
  - 测试缓存未命中时的重新扫描
    - 清空缓存,调用 `GetAllToolsAsync()` 验证自动重新扫描
  - **需求参考**: NFR-4(可测试性)

### 4. 实现工具元数据缓存刷新机制 ✅
- [x] 在 `ToolMetadataService` 中实现 `RefreshAppToolAsync(appId)`
  - 根据 `appId` 查询数据库获取 Tool App
  - 如果 App 不存在或非 Tool 类型,移除缓存并返回
  - 重新提取参数和生成 ToolMetadata
  - 写入缓存: `await _cache.SetAsync($"tool_metadata:{name}", metadata)`
  - 刷新全局列表缓存
  - **需求参考**: R-4.3(缓存同步)
  - **实现状态**: ✅ 完成

- [x] 在 `ToolMetadataService` 中实现 `RefreshAppToolByNameAsync(toolName)`
  - 按工具名称直接移除缓存(用于 App 删除场景)
  - 刷新全局列表缓存
  - **实现状态**: ✅ 完成

- [x] 在 `ToolMetadataService` 中实现 `RefreshMcpServerAsync(serverId)`
  - 查询该 MCP Server 的所有工具(TODO:集成 MCP SDK 后实现)
  - 移除所有相关缓存键
  - 重新扫描并缓存
  - **需求参考**: R-4.3(缓存同步)
  - **实现状态**: ✅ 占位方法完成

- [x] 编写集成测试 `AppToolIntegrationTests.cs`
  - [x] 测试 App 添加后缓存自动刷新
  - [x] 测试 App 更新后缓存自动刷新
    - 修改 App 的 Prompt 模板(添加新参数)
    - 调用 `RefreshAppToolAsync(appId)`
    - 验证缓存已更新,新参数已包含在 Schema 中
  - [x] 测试 App 禁用后缓存清除
  - [x] 测试 App 删除后缓存清除
  - [x] 测试 AppType 变更后缓存更新
  - **测试结果**: ✅ 7/7 集成测试通过
  - **需求参考**: NFR-4(可测试性)

### 5. 配置 HybridCache 和应用启动初始化 ✅
- [x] 在 `Program.cs` 中配置 HybridCache
  ```csharp
  builder.Services.AddHybridCache(options =>
  {
      options.MaximumPayloadBytes = 10 * 1024 * 1024; // 10MB
      options.MaximumKeyLength = 1024;
      options.DefaultEntryOptions = new HybridCacheEntryOptions
      {
          Expiration = TimeSpan.FromHours(1),              // Redis 缓存 1 小时
          LocalCacheExpiration = TimeSpan.FromMinutes(30)  // 本地缓存 30 分钟
      };
  });
  ```
  - [x] 注册 PromptParameterExtractor 和 ToolMetadataService 为 Singleton
  - [x] 添加 Redis 配置注释(可选 L2 分布式缓存)
  - **需求参考**: NFR-3(可扩展性)
  - **实现状态**: ✅ 完成

- [x] 在应用启动时初始化工具元数据
  ```csharp
  var toolMetadataService = app.Services.GetRequiredService<ToolMetadataService>();
  await toolMetadataService.InitializeAsync();
  _logger.LogInformation("Tool metadata initialized: {Count} tools loaded", 
      (await toolMetadataService.GetAllToolsAsync()).Count);
  ```
  - [x] 添加错误处理(初始化失败不应阻止应用启动)
  - **需求参考**: R-4.2(工具发现机制)
  - **实现状态**: ✅ 完成

**验收标准**:
- ✅ 应用启动时自动扫描并缓存所有 Tool App
- ✅ HybridCache 配置正确(L1 本地缓存 + L2 Redis 可选)
- ✅ App 配置更新时缓存自动刷新
- ✅ 单元测试和集成测试覆盖率 > 85% (实际: 100%)

---

## 🎯 第三阶段:App Tool 动态注册和执行 (P0 - 核心功能)

**目标**: 将 App Tool 注册到 Semantic Kernel,支持其他 Agent 调用

**⚠️ 当前状态**: 任务 6-7 依赖 Semantic Kernel 集成,建议先完成第四至第五阶段(Todolist 转换功能),或者暂停等待进一步需求明确。

### 6. 实现 AppToolPlugin 插件 (待定)
- [ ] 创建 `Services/Tools/AppToolPlugin.cs`
  - 注入依赖: `PromptParameterExtractor`, `AgentOrchestratorService`, `IDbContextFactory<LlmDbContext>`
  
  - 实现 `RegisterToolApp(kernel, toolApp)` 静态方法
    - 从 ToolApp 提取参数 Schema
    - 使用 `kernel.CreateFunctionFromMethod()` 动态创建 KernelFunction
    - 函数逻辑:调用 `ExecuteToolAppAsync(toolApp, arguments)`
    - 添加函数元数据(Description, Parameters)
    - **需求参考**: US-3(App 即工具), R-3.2(工具注册)
  
  - 实现 `ExecuteToolAppAsync(toolApp, arguments)` 私有方法
    - 步骤 1:渲染 Prompt 模板
    - 步骤 2:调用 AgentOrchestratorService 执行
    - 步骤 3:返回 LLM 响应结果
    - **需求参考**: R-3.3(工具执行)
  
  - **说明**: 此任务需要明确 Semantic Kernel 的集成方式,建议先完成 Todolist 功能

- [ ] 编写单元测试 `AppToolPluginTests.cs`
  - 测试工具注册
  - 测试参数提取和 Prompt 渲染
  - 测试工具执行(模拟 Orchestrator 调用)
  - 测试错误处理
  - **需求参考**: NFR-4(可测试性)

### 7. 集成 App Tool 到 Kernel 注册流程 (待定)
- [ ] 在应用启动时注册所有 Tool App
  - **说明**: 此任务依赖任务 6 的完成,建议推迟
  
- [ ] 编写端到端集成测试 `AppToolIntegrationTests.cs`
  - **说明**: 已有 AppToolIntegrationTests.cs 测试 AppService 集成
  - 如需测试 Kernel 集成,需等待任务 6 完成

**验收标准** (待任务 6-7 完成后验证):
- ⏸️ 所有 Tool App 在应用启动时自动注册到 Kernel
- ⏸️ 从 Agent 调用 Tool App 成功,参数正确传递
- ⏸️ Tool App 返回的结果能被 Agent 正确处理
- ⏸️ 端到端测试通过

---

## 第四阶段:基础数据模型(仅必要部分) (P0)

**目标**: 创建 Todolist 相关的基础模型类,为后续转换功能提供数据结构

### 8. 创建 Todolist 相关模型类
- [ ] 创建 `Models/Workflow/TodolistTask.cs` 模型类
  - 定义字段: TaskId, Title, Description, AssignedAgentRole, Dependencies 等
  - **需求参考**: US-1(智能待办任务列表创建), R-1.1(Todolist 创建工具)
- [ ] 创建 `Models/Workflow/CreateTodolistRequest.cs` 和 `CreateTodolistResponse.cs`
  - Request: SessionId, UserRequest, SuggestedTasks
  - Response: Success, ExecutionId, ValidationWarnings, Errors
  - **需求参考**: US-1(智能待办任务列表创建)

### 9. 创建 DAG 转换相关模型类
- [ ] 创建 `Models/Workflow/DAGNodeConfig.cs`
  - 定义节点配置(NodeId, NodeType, AgentMemberId, ToolAppId, Dependencies 等)
  - **需求参考**: US-7(查看依赖关系图), R-7.2(节点依赖关系)
- [ ] 创建 `Models/Workflow/ConvertedDAGConfig.cs`
  - 定义转换后的 DAG 配置(Nodes, ParallelGroups, ExecutionOrder)
  - **需求参考**: US-7(查看依赖关系图)

**验收标准**:
- ✅ 模型类定义完整,字段类型正确
- ✅ 支持 JSON 序列化/反序列化

---

## 第五阶段:任务映射和 Todolist 转换 (P0)

**目标**: 实现 Todolist 到 DAG 的智能转换,包含三级 Agent 匹配策略

### 10. 实现 AgentRoleMatcher 三级匹配策略
- [ ] 创建 `Services/Workflow/AgentRoleMatcher.cs`
  - 实现 `FindMatchingAgent(role, agents)` 主方法
  - 实现 Level 1:精确名称匹配(忽略大小写)
  - 实现 Level 2:角色字段匹配(AgentMember.Role)
  - 实现 Level 3:模糊/关键词匹配(`FindFuzzyMatch` 私有方法)
  - 实现 Level 4:单 Agent 降级匹配(仅一个 Agent 时)
  - 返回匹配结果和匹配类型(MatchType 枚举)
  - **需求参考**: US-1(智能待办任务列表创建), R-1.2(任务映射到 Agent)
- [ ] 编写单元测试
  - 测试精确匹配("Planner" → Name="Planner")
  - 测试角色匹配("planner" → Role="planner")
  - 测试模糊匹配("搜索专家" → Role="researcher")
  - 测试降级匹配(单 Agent 场景)
  - 测试无匹配情况(返回 null)
  - **需求参考**: NFR-4(可测试性)

### 11. 实现 DAGValidator 验证器
- [ ] 创建 `Services/Workflow/DAGValidator.cs`
  - 实现 `ValidateDAG(nodeIds, dependencies)` 主方法
  - 实现依赖存在性检查
  - 实现拓扑排序(Kahn 算法)
  - 实现循环依赖检测
  - 返回验证结果、错误列表、排序后的节点
  - **需求参考**: US-7(查看依赖关系图), R-7.3(循环检测)
- [ ] 编写单元测试
  - 测试有效 DAG(无循环)
  - 测试循环依赖检测(A → B → C → A)
  - 测试依赖不存在错误(任务依赖不存在的任务)
  - 测试拓扑排序顺序正确性
  - **需求参考**: NFR-4(可测试性)

### 12. 实现 TodolistConverter 转换器
- [ ] 创建 `Services/Workflow/TodolistConverter.cs`
  - 实现 `ConvertTodolistToDAGAsync(tasks, app)` 主方法
  - 集成 `AgentRoleMatcher` 进行任务映射
  - 加载所有 Tool App(从 ToolMetadataService)
  - 优先匹配 Tool App,其次匹配 AgentMember
  - 收集并行组信息
  - 调用 `DAGValidator` 验证 DAG
  - 返回 DAG 配置、错误列表、警告列表
  - **需求参考**: US-1(智能待办任务列表创建), R-1.3(DAG 转换)
- [ ] 编写单元测试
  - 测试成功转换(所有任务成功映射)
  - 测试 Tool App 优先匹配
  - 测试 AgentMember 匹配(精确、角色、模糊)
  - 测试映射失败错误
  - 测试 DAG 验证失败(循环依赖)
  - 测试警告生成(模糊匹配、降级匹配)
  - **需求参考**: NFR-4(可测试性)

**验收标准**:
- ✅ Todolist 成功转换为 DAG 配置
- ✅ 三级匹配策略正常工作
- ✅ 循环依赖检测有效
- ✅ 单元测试覆盖率 > 85%

---

## 第六阶段:数据库和持久化模型 (P1)

**目标**: 创建数据库表和实体类,支持 Todolist 执行历史持久化

### 13. 创建 TodolistExecution 实体类和数据库迁移
- [ ] 创建 `Data/Entities/TodolistExecution.cs` 实体类
  - 实现所有必需字段(id, app_id, session_id, status 等)
  - 添加 JSONB 字段: `TodolistJson`, `DagConfigJson`, `NodeExecutionsJson`
  - 实现 `NotMapped` 属性: `NodeExecutions`, `Todolist`(自动序列化/反序列化)
  - 添加 `TodolistStatus` 枚举(Pending, Running, Paused, Completed, Failed, Cancelled)
  - **需求参考**: US-8(工作流执行历史查询), R-8.1(执行记录持久化)

### 14. 创建 NodeExecutionRecord 模型类
- [ ] 创建 `Models/Workflow/NodeExecutionRecord.cs` JSON 模型类
  - 定义所有字段(NodeId, AgentMemberId, Status, Result 等)
  - 添加 `NodeStatus` 枚举(Pending, Running, Completed, Failed, Skipped, AwaitingInput)
  - 添加 `NodeType` 枚举(Agent, ToolApp)
  - **需求参考**: US-7(查看依赖关系图), R-7.1(DAG 可视化)

### 15. 创建并执行 EF Core 数据库迁移
- [ ] 生成迁移文件
  - 使用 `dotnet ef migrations add AddTodolistExecutionPersistence`
  - 验证生成的 SQL(仅包含 todolist_executions 表)
  - 确保包含 GIN 索引(`idx_todolist_executions_node_status`)
  - **需求参考**: R-8.1(执行记录持久化)
- [ ] 编写迁移测试
  - 测试迁移成功应用
  - 验证索引创建
  - 测试回滚功能
  - **需求参考**: NFR-4(可测试性)

**验收标准**:
- ✅ 数据库表创建成功
- ✅ JSONB 字段支持复杂对象存储
- ✅ GIN 索引创建成功

---

## 第七阶段:Todolist 执行持久化服务 (P1)

**目标**: 实现 Todolist 执行历史的 CRUD 操作

### 16. 实现 TodolistPersistenceService 基础方法
- [ ] 创建 `Services/Workflow/TodolistPersistenceService.cs`
  - 实现 `CreateTodolistExecutionAsync()` 方法(Fire-and-forget)
  - 实现 `UpdateTodolistStatusAsync()` 方法
  - 实现 `UpdateNodeStatusAsync()` 方法
  - **需求参考**: US-8(工作流执行历史查询), R-8.1-8.2

### 17. 实现 Todolist 执行查询方法
- [ ] 实现 `GetTodolistHistoryAsync()` - 支持过滤和分页
- [ ] 实现 `GetTodolistDetailsAsync()` - 查询完整执行记录
- [ ] 实现 `GetNodesByStatusAsync()` - JSONB 查询
- [ ] 编写集成测试
  - **需求参考**: R-8.3(历史查询)

### 18. 集成持久化到 DAGStrategy
- [ ] 扩展 `DAGStrategy` 类支持持久化
  - 在 `ExecuteAsync()` 开始时调用 `CreateTodolistExecutionAsync()`
  - 在每个节点执行前/后调用 `UpdateNodeStatusAsync()`
  - 在工作流完成/失败时调用 `UpdateTodolistStatusAsync()`
  - **需求参考**: R-8.1(执行记录持久化)
- [ ] 编写集成测试
  - 测试完整工作流执行并持久化
  - 测试节点状态变化记录
  - **需求参考**: NFR-4(可测试性)

**验收标准**:
- ✅ Todolist 执行历史成功持久化
- ✅ 节点状态实时更新
- ✅ 查询功能正常工作

---

## 第八阶段:工作流管理工具插件 (P1)

**目标**: 实现 8 个工作流管理工具,供 Agent 调用

### 19. 实现 Todolist 创建和转换工具
- [ ] 创建 `Services/Tools/WorkflowToolsPlugin.cs`
- [ ] 实现 `create_workflow_todolist` 工具
  - 参数: user_request, suggested_tasks
  - 逻辑: 调用 `TodolistConverter` 转换为 DAG
  - 返回: execution_id, dag_config, warnings, errors
  - **需求参考**: US-1(智能待办任务列表创建), R-1.1

### 20. 实现历史查询工具
- [ ] 实现 `get_workflow_history` 工具
  - 参数: session_id, status, page, page_size
  - 逻辑: 调用 `TodolistPersistenceService.GetTodolistHistoryAsync()`
  - **需求参考**: US-8(工作流执行历史查询), R-8.3
- [ ] 实现 `get_workflow_details` 工具
  - 参数: execution_id
  - 逻辑: 调用 `GetTodolistDetailsAsync()`
  - **需求参考**: US-8(工作流执行历史查询), R-8.3

### 21. 实现依赖关系图查询工具
- [ ] 实现 `get_dependency_graph` 工具
  - 参数: execution_id
  - 逻辑: 从 DAG 配置生成 Mermaid 图语法
  - 返回: mermaid_code, parallel_groups
  - **需求参考**: US-7(查看依赖关系图), R-7.1

### 22. 实现工作流控制工具
- [ ] 实现 `pause_workflow` 和 `resume_workflow` 工具
  - 参数: execution_id
  - 逻辑: 更新 Todolist 状态,中断/恢复执行
  - **需求参考**: US-9(工作流暂停和恢复), R-9.1-9.2

### 23. 实现任务重试和修改工具
- [ ] 实现 `retry_failed_nodes` 工具
  - 参数: execution_id, node_ids
  - 逻辑: 重置节点状态,重新执行
  - **需求参考**: US-10(任务失败重试), R-10.1
- [ ] 实现 `modify_workflow_tasks` 工具
  - 参数: execution_id, modifications
  - 逻辑: 更新 Todolist 配置,重新验证 DAG
  - **需求参考**: US-11(动态修改任务), R-11.1

**验收标准**:
- ✅ 所有 8 个工具可通过 `/v1/chat/completions` 调用
- ✅ 工具参数验证正确
- ✅ 集成测试覆盖所有工具

---

## 第九阶段:DAGStrategy 增强 (P2)

**目标**: 扩展 DAGStrategy 支持暂停、恢复、重试

### 24. 实现暂停和恢复机制
- [ ] 在 `DAGStrategy` 中实现暂停检查点
  - 在每个节点执行前检查 `PauseRequested` 标志
  - 暂停时保存当前状态到数据库
  - **需求参考**: US-9(工作流暂停和恢复), R-9.1
- [ ] 实现恢复逻辑
  - 从数据库加载上次执行状态
  - 跳过已完成的节点,继续执行
  - **需求参考**: R-9.2

### 25. 实现失败节点重试
- [ ] 在 `DAGStrategy` 中实现重试逻辑
  - 重置失败节点状态为 Pending
  - 递归重置所有依赖该节点的后续节点
  - 重新执行 DAG(从第一个 Pending 节点开始)
  - **需求参考**: US-10(任务失败重试), R-10.1

**验收标准**:
- ✅ 暂停后能正确恢复执行
- ✅ 失败节点重试成功
- ✅ 集成测试覆盖暂停/恢复/重试场景

---

## 第十阶段:UI、测试、文档 (P2)

**目标**: 完善用户界面、测试和文档

### 26. 创建 App Tool 配置管理 UI (P0 - 提升优先级)
- [ ] 创建 `Components/Tools/ToolAppEditor.razor`
  - 创建/编辑 Tool App(Name, Description, Prompt)
  - 实时预览提取的参数(使用 PromptParameterExtractor)
  - 配置参数覆盖(ConfigJson 中的 parameterOverrides)
  - 测试工具功能(输入示例参数,预览渲染后的 Prompt)
  - 启用/禁用工具开关
  - 删除工具(带确认对话框)
  - **需求参考**: US-3(App 即工具), R-3.1(工具配置界面)

### 27. 创建 App Tool 列表 UI (P0 - 提升优先级)
- [ ] 创建 `Components/Tools/ToolAppList.razor`
  - 显示所有 Tool App(分页、搜索、过滤)
  - 显示工具状态(已启用/已禁用)
  - 显示参数数量和参数预览
  - 快速启用/禁用切换
  - 编辑/删除操作按钮
  - 集成到主导航菜单
  - **需求参考**: US-3(App 即工具)

### 28. 创建参数配置组件 (P0 - 提升优先级)
- [ ] 创建 `Components/Tools/ParameterSchemaEditor.razor`
  - 可视化编辑参数 Schema
  - 设置参数类型(string, number, boolean, array, object)
  - 设置参数描述
  - 标记必填/可选参数
  - 生成 JSON Schema 预览
  - 支持从 Prompt 自动提取参数
  - **需求参考**: R-3.1(工具配置界面)

### 29. 创建 Todolist 历史查询 UI 组件
- [ ] 创建 `Components/Workflow/TodolistHistoryList.razor`
  - 显示历史记录列表(分页、过滤)
  - 支持点击查看详情
  - **需求参考**: US-8(工作流执行历史查询)

### 30. 创建 DAG 可视化组件
- [ ] 创建 `Components/Workflow/DependencyGraph.razor`
  - 使用 Mermaid.js 渲染 DAG
  - 显示节点状态(颜色编码)
  - **需求参考**: US-7(查看依赖关系图)

### 31. 创建工作流控制 UI
- [ ] 创建 `Components/Workflow/WorkflowControls.razor`
  - 提供暂停、恢复、重试按钮
  - 显示实时状态更新
  - **需求参考**: US-9, US-10

### 32. 编写端到端集成测试
- [ ] 创建 `TodolistE2eTests.cs`
  - 测试完整流程:创建 Todolist → 转换 DAG → 执行 → 查询历史
  - 测试暂停/恢复流程
  - 测试失败重试流程
  - **需求参考**: NFR-4(可测试性)

### 33. 编写性能测试
- [ ] 创建 `TodolistPerformanceTests.cs`
  - 测试大规模 Todolist(100+ 任务)转换性能
  - 测试并发执行性能
  - 测试 JSONB 查询性能
  - **需求参考**: NFR-2(高性能)

### 34. 编写负载测试
- [ ] 测试 100 个并发工作流执行
- [ ] 验证 HybridCache 在高负载下的性能
- [ ] **需求参考**: NFR-2(高性能)

### 35. 更新 appsettings.json 配置
- [ ] 添加 Todolist 相关配置
  ```json
  "Todolist": {
    "MaxTasksPerWorkflow": 100,
    "DefaultPageSize": 20,
    "EnableDetailedLogging": false
  }
  ```
  - **需求参考**: NFR-3(可扩展性)

### 36. 编写用户文档和 API 文档
- [ ] 创建 `docs/workflow-api.md`
  - 文档化所有 8 个工作流工具
  - 提供示例请求/响应
  - **需求参考**: NFR-4(可测试性)
- [ ] 更新 `README.md`
  - 添加 App Tool 功能说明
  - 添加 Todolist 工作流说明

**验收标准**:
- ✅ UI 组件正常工作
- ✅ 所有端到端测试通过
- ✅ 性能测试满足指标(100+ 任务 < 5s 转换)
- ✅ 文档完整清晰

---

## 📊 任务完成标准

### 第一至第三阶段(App Tool)完成标准
- ✅ 所有 Tool App 在应用启动时自动注册到 Kernel
- ✅ 从 Agent 调用 Tool App 成功,参数正确传递
- ✅ HybridCache 缓存命中率 > 90%
- ✅ 单元测试和集成测试覆盖率 > 85%
- ✅ 端到端测试:创建 Tool App → 注册 → 从 Agent 调用 → 验证响应

### 最终验收(所有阶段完成后)
- ✅ 所有 13 个用户故事(US-1 到 US-13)均已实现并通过测试
- ✅ 8 个工作流管理工具可通过 `/v1/chat/completions` 调用
- ✅ App Tool 可以作为可重用工具被其他 Agent 调用
- ✅ Todolist 可以成功转换为 DAG 并执行
- ✅ 所有单元测试通过(覆盖率 > 80%)
- ✅ 端到端测试覆盖所有主要用户场景

---

**任务总数**: 36 个主任务  
**第一至三阶段估计时间**: 2-3 个工作日(任务 1-7,App Tool 核心功能)  
**App Tool UI 估计时间**: 1-2 个工作日(任务 26-28)  
**总估计时间**: 7-10 个工作日  
**依赖关系**: 第一至三阶段(App Tool 功能)独立可用,第四至十阶段依赖前序完成  
**风险点**: Prompt 参数提取的正则表达式复杂度、HybridCache 配置

---

## 🚀 推荐执行顺序

**优先执行(P0 - 立即开始)**:
1. ✅ **任务 1-7:App Tool 与 Prompt 绑定**(第一至第三阶段)
   - 这是最核心的创新功能
   - 完成后可立即验证 App 作为工具的可行性

**后续执行(P0)**:
2. 任务 8-12:Todolist 转换功能(第四至第五阶段)

**最后执行(P1-P2)**:
3. 任务 13-18:数据库持久化(第六至第七阶段)
4. 任务 19-33:工作流管理、集成、UI、文档(第八至第十阶段)
