using System.IO;
using LY.LlmPool.Web;
using LY.LlmPool.Web.Components;
using LY.LlmPool.Web.Components.Account;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services;
using LY.LlmPool.Web.Services.Agents;
using LY.LlmPool.Web.Services.Tools;
using LY.LlmPool.Web.Services.Aggregation;
using LY.LlmPool.Web.Middleware;
using LY.LlmPool.Web.Filters;
using LY.LlmPool.Web.Logging;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Scalar.AspNetCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Runtime.Loader;

var builder = WebApplication.CreateBuilder(args);

// 将诊断日志写入本地文件，便于分析 Activity 链路
var logsDirectory = Path.Combine(builder.Environment.ContentRootPath, "logs");
var activityLogPath = Path.Combine(logsDirectory, "activity-debug.log");
builder.Logging.AddProvider(new FileLoggerProvider(activityLogPath, LogLevel.Debug));

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var sqliteConnectionString = builder.Configuration.GetConnectionString("SqliteConnection");
var sqliteIdentityConnection = builder.Configuration.GetConnectionString("SqliteIdentityConnection") ?? sqliteConnectionString;
var databaseType = builder.Configuration.GetValue<string>("Database:Type") ?? "PostgreSQL";
var useInMemoryDb = Environment.GetEnvironmentVariable("USE_INMEMORY_DB")?.ToLowerInvariant() == "true";

// Add database contexts
if (useInMemoryDb)
{
    builder.Services.AddDbContextFactory<LlmDbContext>(options =>
        options.UseInMemoryDatabase("LlmDbTest"));

    builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
        options.UseInMemoryDatabase("AppDbTest"));
}
else
{
    if (databaseType.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddDbContextFactory<LlmDbContext>(options =>
            options.UseSqlite(sqliteConnectionString, b => b.MigrationsAssembly("LY.LlmPool.DataMigrations.Sqlite"))
                   .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));

        // Use a separate sqlite file for Identity (ApplicationDbContext) so Identity tables are stored separately
        builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
            options.UseSqlite(sqliteIdentityConnection, b => b.MigrationsAssembly("LY.LlmPool.DataMigrations.Sqlite"))
                   .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));
    }
    else
    {
        builder.Services.AddDbContextFactory<LlmDbContext>(options =>
            options.UseNpgsql(connectionString));

        builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString));
    }
}

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// Add Identity services
builder.Services.AddIdentityCore<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = true)
    // 注册角色支持，以便 RoleManager<IdentityRole> 可注入并使用
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

// 确保当创建 ClaimsPrincipal 时会包含角色声明
builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>>();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityUserAccessor>();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

// Add LLM Pool services
builder.Services.AddScoped<LlmPoolService>();
builder.Services.AddScoped<IChatClientService, ChatClientService>();
builder.Services.AddScoped(sp => (ChatClientService)sp.GetRequiredService<IChatClientService>());
builder.Services.AddSingleton<PromptParameterService>(); // Singleton - 无状态服务,可被 Singleton 依赖(包含环境变量替换功能)
builder.Services.AddScoped<CallRecordService>();

// 🎯 Prompt 缓存服务配置 - 使用 Microsoft.Extensions.VectorData 抽象
builder.Services.Configure<LY.LlmPool.Web.Services.PromptCache.PromptCacheOptions>(
    builder.Configuration.GetSection("PromptCache"));
builder.Services.Configure<LY.LlmPool.Web.Services.PromptCache.EmbeddingGeneratorOptions>(
    builder.Configuration.GetSection("EmbeddingGenerator"));

// 注册 Embedding 生成器工厂
builder.Services.AddSingleton<LY.LlmPool.Web.Services.PromptCache.EmbeddingGeneratorFactory>();

// 添加 Embedding Generator 到 DI（供 VectorStore 使用）
builder.Services.AddEmbeddingGenerator(sp =>
{
    var factory = sp.GetRequiredService<LY.LlmPool.Web.Services.PromptCache.EmbeddingGeneratorFactory>();
    return factory.CreateGenerator();
});

// 配置 SQLite Vector Store
var promptCacheOptions = builder.Configuration.GetSection("PromptCache").Get<LY.LlmPool.Web.Services.PromptCache.PromptCacheOptions>() 
    ?? new LY.LlmPool.Web.Services.PromptCache.PromptCacheOptions();
var vectorStorePath = Path.Combine(AppContext.BaseDirectory, promptCacheOptions.VectorDbPath);
var vectorStoreConnectionString = $"Data Source={vectorStorePath}";

// 只有在配置启用时才注册 Prompt Cache 相关服务
if (promptCacheOptions.Enabled)
{
    // 注册 SQLite Vector Store 和 Collection
    builder.Services.AddSqliteVectorStore(_ => vectorStoreConnectionString);
    builder.Services.AddSqliteCollection<string, LY.LlmPool.Web.Data.Entities.PromptCacheEntry>(
        LY.LlmPool.Web.Data.Entities.PromptCacheEntry.CollectionName,
        vectorStoreConnectionString);

    // 注册 Prompt 缓存服务（使用新的 VectorStore 实现）
    builder.Services.AddSingleton<LY.LlmPool.Web.Services.PromptCache.PromptCacheService>();

    // 初始化向量存储（后台任务）
    builder.Services.AddHostedService<LY.LlmPool.Web.Services.PromptCache.PromptCacheInitializationService>();
}
else
{
    // Prompt Cache 未启用（PromptCache:Enabled = false），跳过注册
    Console.WriteLine("Prompt cache disabled by configuration (PromptCache:Enabled = false). Skipping registration of prompt cache services.");
} 

// 🎯 添加统一的缓存管理服务（Singleton - 管理全局缓存）
builder.Services.AddSingleton<LlmPoolCacheService>();
builder.Services.AddScoped<LY.LlmPool.Web.Services.Agents.AgentOrchestratorService>();
builder.Services.AddScoped<McpServerConfigService>();

// 🎯 添加负载均衡服务（Singleton - 共享状态用于跟踪请求数）
builder.Services.AddSingleton<LY.LlmPool.Web.Services.LoadBalancing.LoadBalancerService>();

// 🎯 添加负载均衡缓存预热后台服务
builder.Services.AddHostedService<LY.LlmPool.Web.Services.LoadBalancing.LoadBalancerCacheWarmupService>();

// Add monitoring and persistence services
builder.Services.AddScoped<LY.LlmPool.Web.Services.Monitoring.ChatExecutionPersistenceService>();
// Using ModelContextProtocol SDK for MCP discovery (no custom SSE client registered)

// Add Tool Metadata Services
// PromptParameterExtractor has been merged into PromptParameterService
builder.Services.AddSingleton<LY.LlmPool.Web.Services.Tools.ToolMetadataService>();

// Configure HybridCache for tool metadata caching
builder.Services.AddHybridCache(options =>
{
    // L1 cache (in-memory) settings
    options.MaximumPayloadBytes = 100 * 1024 * 1024; // 100MB per cache entry
    options.MaximumKeyLength = 1024; // Max key length

    // Default expiration (can be overridden per entry)
    options.DefaultEntryOptions = new Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromHours(1), // Tool metadata expires after 1 hour
        LocalCacheExpiration = TimeSpan.FromMinutes(30) // L1 cache expires faster
    };
});

// Configure JSON serialization for HybridCache to handle cycles
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

// Add distributed cache (Redis) for L2 cache (optional, for production)
// Uncomment and configure when Redis is available:
// builder.Services.AddStackExchangeRedisCache(options =>
// {
//     options.Configuration = builder.Configuration.GetConnectionString("Redis");
//     options.InstanceName = "LlmPool:";
// });

// Add new granular services
builder.Services.AddScoped<ModelTypeService>();
builder.Services.AddScoped<ConfigService>();
builder.Services.AddScoped<EndpointService>();
builder.Services.AddScoped<PromptService>();
builder.Services.AddScoped<AppService>();
builder.Services.AddScoped<AgentService>();
builder.Services.AddScoped<ExampleAppService>();
builder.Services.AddScoped<ToolProviderService>();
builder.Services.AddScoped<ChatClientFactory>();
// Register MCP server service
builder.Services.AddScoped<IMcpServerService, McpServerService>(); 
// Register MCP inspector service
builder.Services.AddScoped<IMcpInspectorService, McpInspectorService>();

// 🎯 注册 Activity 追踪配置
builder.Services.Configure<LY.LlmPool.Web.Services.Telemetry.ActivityTracingOptions>(
    builder.Configuration.GetSection("ActivityTracing"));
 
// 🎯 注册 Activity 追踪数据仓储
builder.Services.AddScoped<LY.LlmPool.Web.Repositories.IActivityTraceRepository,
    LY.LlmPool.Web.Repositories.ActivityTraceRepository>();

// 🎯 注册追踪记录服务（Trace Record Service）
builder.Services.AddScoped<LY.LlmPool.Web.Services.Telemetry.ITraceRecordService,
    LY.LlmPool.Web.Services.Telemetry.TraceRecordService>();

// 🎯 注册 Activity 追踪持久化后台服务
builder.Services.AddSingleton<LY.LlmPool.Web.Services.Telemetry.ActivityTracePersistenceService>();
builder.Services.AddHostedService<LY.LlmPool.Web.Services.Telemetry.ActivityTracePersistenceService>(
    sp => sp.GetRequiredService<LY.LlmPool.Web.Services.Telemetry.ActivityTracePersistenceService>());

// 🎯 注册 OpenTelemetry Activity 追踪服务
builder.Services.AddSingleton<LY.LlmPool.Web.Services.Telemetry.ActivityTraceService>();
builder.Services.AddSingleton<LY.LlmPool.Web.Services.Telemetry.OtlpTraceParser>();

// 🎯 配置 OpenTelemetry - 让 LlmPool 成为简易版 OTLP Collector
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService("LlmPool")
        .AddAttributes(new Dictionary<string, object>
        {
            ["service.version"] = "1.0.0",
            ["deployment.environment"] = builder.Environment.EnvironmentName
        }))
    .WithTracing(tracing =>
    {
        tracing
            // 🎯 监听 MCP SDK 的 ActivitySource (关键!)
            .AddSource("Experimental.ModelContextProtocol")
            // 监听 Microsoft.Extensions.AI
            .AddSource("Microsoft.Extensions.AI")
            .AddSource("Experimental.Microsoft.Extensions.AI")
            // 监听 LlmPool 自定义 ActivitySource
            .AddSource("LlmPool.*")
            // 🎯 监听 HttpClient 的 ActivitySource (用于追踪 HTTP 请求)
            .AddSource("System.Net.Http")
            // 监听 ASP.NET Core 和 HttpClient
            .AddAspNetCoreInstrumentation(options =>
            {
                options.RecordException = true;
                options.Filter = context =>
                {
                    // 过滤掉健康检查和静态资源请求
                    var path = context.Request.Path.Value ?? "";
                    return !path.StartsWith("/_blazor") && 
                           !path.StartsWith("/health") &&
                           !path.StartsWith("/_framework");
                };
            })
            .AddHttpClientInstrumentation(options =>
            {
                options.RecordException = true;
                
                // 🎯 过滤不需要记录的 HTTP 请求
                options.FilterHttpRequestMessage = (httpRequestMessage) =>
                {
                    var url = httpRequestMessage.RequestUri?.ToString() ?? string.Empty;
                    
                    // 过滤掉 trace exporter 的 HTTP 请求（避免循环追踪）
                    if (url.Contains("/v1/traces/json") || url.Contains("/traces"))
                    {
                        return false;
                    }
                    
                    // 过滤掉内部健康检查等请求
                    if (url.Contains("/health") || url.Contains("/_blazor"))
                    {
                        return false;
                    }
                    
                    return true;
                };
                
                // 🎯 自定义 Activity 显示名称
                options.EnrichWithHttpRequestMessage = (activity, httpRequestMessage) =>
                {
                    var path = httpRequestMessage.RequestUri?.PathAndQuery ?? string.Empty;
                    
                    // 为调用上游 LLM 的请求设置更友好的名称
                    if (path.Contains("/chat/completions"))
                    {
                        activity.DisplayName = "upstream.llm.chat";
                    }
                };
            })
            // 🎯 使用自定义 Exporter 将数据导出到 ActivityTraceService (内存存储)
            .AddProcessor(sp => new OpenTelemetry.SimpleActivityExportProcessor(
                new LY.LlmPool.Web.Services.Telemetry.InMemoryActivityExporter(
                    sp.GetRequiredService<LY.LlmPool.Web.Services.Telemetry.ActivityTraceService>(),
                    sp.GetRequiredService<ILogger<LY.LlmPool.Web.Services.Telemetry.InMemoryActivityExporter>>()
                )
            ));
            // 💡 可选: 同时导出到外部 OTLP Collector (Jaeger/Tempo/Grafana)
            // .AddOtlpExporter(options =>
            // {
            //     options.Endpoint = new Uri("http://localhost:4317");
            // });
    });

// Add HTTP client factory
builder.Services.AddHttpClient();

// Enable HTTP request/response logging (including JSON bodies)
builder.Services.AddHttpLogging(options =>
{
    options.LoggingFields = HttpLoggingFields.Request |
                            HttpLoggingFields.RequestHeaders |
                            HttpLoggingFields.RequestBody |
                            HttpLoggingFields.Response |
                            HttpLoggingFields.ResponseHeaders;
    options.RequestBodyLogLimit = 1024 * 1024; // 1MB
    options.ResponseBodyLogLimit = 1024 * 1024; // 1MB
    options.MediaTypeOptions.AddText("application/json");
    options.MediaTypeOptions.AddText("text/plain");
});

// Named HttpClient for LlmPool API with configurable BaseUrl and HttpContext fallback
builder.Services.AddTransient<LoggingHttpHandler>();
builder.Services.AddTransient<ActivityPropagationHandler>();
builder.Services.AddHttpClient("LlmPoolApi", (sp, http) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var baseUrl = cfg["LlmPool:BaseUrl"]; // e.g. "https://your-host/v1"
    var server = sp.GetRequiredService<IServer>();
    var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses.Where(x => !x.StartsWith("https")) ?? [];

    if (addresses.Any())
    {
        var address = addresses.First();
        var port = new Uri(address).Port;
        // 构建基于当前请求的URL
        baseUrl = $"http://localhost:{port}/v1";
    }

    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        var ctx = accessor.HttpContext;
        if (ctx != null)
        {
            baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/v1";
        }
    }
    if (!string.IsNullOrWhiteSpace(baseUrl))
    {
        http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    }
    http.Timeout = TimeSpan.FromMinutes(10);
    http.DefaultRequestHeaders.Accept.Clear();
    http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
    if (!http.DefaultRequestHeaders.Contains("Accept-Charset"))
    {
        http.DefaultRequestHeaders.Add("Accept-Charset", "utf-8");
    }
})
// 🔴 移除 ActivityPropagationHandler - HttpClient 已内置 Activity 传播
// .AddHttpMessageHandler<ActivityPropagationHandler>() 
.AddHttpMessageHandler<LoggingHttpHandler>();

// Named HttpClient for upstream LLM calls (tests can override it)
builder.Services.AddHttpClient("UpstreamLlm")
    .AddHttpMessageHandler<LoggingHttpHandler>()
    // 🎯 添加 Polly 重试策略
    .AddStandardResilienceHandler(options =>
    {
        // 重试策略配置
        options.Retry.MaxRetryAttempts = 10;  // 最多重试 10 次
        options.Retry.Delay = TimeSpan.FromSeconds(1);  // 基础延迟 1 秒
        options.Retry.BackoffType = Polly.DelayBackoffType.Exponential;  // 指数退避 (1s, 2s, 4s)
        options.Retry.UseJitter = true;  // 添加抖动，避免雷鸣群效应
        
        // 超时配置 (必须先配置，因为熔断器采样窗口要 >= 2倍的单次超时)
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);  // 单次尝试超时 120 秒
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(300);  // 总超时 300 秒 (包括重试)
        
        // 🎯 禁用熔断器 - 避免 "circuit is open" 异常中断服务
        // 如果需要熔断功能，建议在上游服务端处理，而不是在客户端
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(240);  
        options.CircuitBreaker.FailureRatio = 1.0;  // 设置为 100%，实际上禁用熔断
        options.CircuitBreaker.MinimumThroughput = int.MaxValue;  // 设置极高阈值，实际上禁用熔断
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(1);  // 即使熔断也快速恢复
    });

// Add Ant Design
builder.Services.AddAntDesign();

// Add HttpContext accessor
builder.Services.AddHttpContextAccessor();

// Add Razor Pages and MVC
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

builder.Services.AddControllers();

// Add email sender
builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

// ���ӽ���������
builder.Services.AddHealthChecks();

//builder.Services.AddSingleton<DataServiceMcp>();

builder.Services.AddSingleton<McpClientsFactory>();
builder.Services.AddHostedService<McpClientStartupService>();



var app = builder.Build();

// 🎯 立即初始化 ActivityTraceService，确保 ActivityListener 在第一个 Activity 创建前就已注册
var activityTraceService = app.Services.GetRequiredService<LY.LlmPool.Web.Services.Telemetry.ActivityTraceService>();
app.Logger.LogInformation("ActivityTraceService 已初始化，ActivityListener 已注册");

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (!useInMemoryDb)
{
    // Ensure the external migrations assembly can be resolved at runtime.
    // Sometimes EF tries to load the assembly by name; explicitly loading it
    // from the application's base directory makes it available to the default
    // AssemblyLoadContext and prevents a FileNotFoundException.
    var migrationsDllPath = Path.Combine(AppContext.BaseDirectory, "LY.LlmPool.DataMigrations.Sqlite.dll");
    if (File.Exists(migrationsDllPath))
    {
        try
        {
            AssemblyLoadContext.Default.LoadFromAssemblyPath(migrationsDllPath);
            builder.Logging.AddConsole();
            // Using app isn't available yet here; use the builder's logger indirectly
        }
        catch (Exception ex)
        {
            // Swallow and log later after app is built; don't prevent startup here.
        }
    }
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    dbContext.Database.Migrate();
    var llmDbContext = scope.ServiceProvider.GetRequiredService<LlmDbContext>();
    
    // 抑制迁移警告，因为我们知道模型是一致的
    llmDbContext.Database.Migrate();
}

//app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

// Add routing middleware
app.UseRouting();

// 🎯 AOP: Activity 上下文中间件 - 自动捕获和传播 Activity
// 必须在路由之后、Controller 之前
app.UseActivityContext();

// HTTP request/response logging
app.UseHttpLogging();

// Add authentication & authorization
app.UseAuthentication();
app.UseAntiforgery();
app.UseAuthorization();

// Configure OpenAPI
app.MapOpenApi();
app.MapScalarApiReference();

// Map endpoints
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

// Map controllers - this needs to be after UseRouting and before UseEndpoints
app.MapControllers();

// Debug endpoint - only in development
if (app.Environment.IsDevelopment())
{
    app.MapGet("/debug/check-tool-data", async (
        IDbContextFactory<LlmDbContext> dbFactory,
        ToolMetadataService toolMetadataService) =>
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        
        var result = new
        {
            ToolApps = await db.Apps
                .Where(a => a.AppType == "Tool")
                .Select(a => new { a.Id, a.Name, a.IsEnabled })
                .ToListAsync(),
                
            CachedTools = (await toolMetadataService.GetAllToolsAsync())
                .Select(t => new { t.Name, Source = t.Source.ToString(), t.SourceId })
                .ToList(),
                
            PromptTools = await db.PromptTools
                .Include(pt => pt.Prompt)
                .Select(pt => new 
                { 
                    pt.PromptId, 
                    PromptName = pt.Prompt != null ? pt.Prompt.Name : null,
                    pt.ToolId, 
                    ToolType = pt.ToolType.ToString(),
                    FormattedValue = (pt.ToolType == ToolType.Internal ? "App" : "MCP") + ":" + pt.ToolId
                })
                .ToListAsync(),
                
            MissingApps = await db.PromptTools
                .Where(pt => pt.ToolType == ToolType.Internal)
                .Where(pt => !db.Apps.Any(a => a.Id == pt.ToolId))
                .Select(pt => new { pt.PromptId, pt.ToolId })
                .ToListAsync()
        };
        
        return Results.Json(result);
    });
}

// Debug: returns current principal claims and roles (for troubleshooting authentication)
app.MapGet("/debug/whoami", async (HttpContext ctx, UserManager<ApplicationUser> userManager, Microsoft.Extensions.Options.IOptions<IdentityOptions> identityOptions) =>
{
    var user = ctx.User;
    List<string>? rolesFromDb = null;
    if (user.Identity?.IsAuthenticated == true)
    {
        var u = await userManager.GetUserAsync(user);
        if (u != null)
        {
            rolesFromDb = (await userManager.GetRolesAsync(u)).ToList();
        }
    }

    var payload = new
    {
        IsAuthenticated = user.Identity?.IsAuthenticated == true,
        AuthenticationType = user.Identity?.AuthenticationType,
        Name = user.Identity?.Name,
        Claims = user.Claims.Select(c => new { c.Type, c.Value }).ToList(),
        RolesInPrincipal = user.Claims.Where(c => c.Type == identityOptions.Value.ClaimsIdentity.RoleClaimType).Select(c => c.Value).ToList(),
        RolesFromDb = rolesFromDb
    };
    return Results.Json(payload);
});

// Seed minimal data when using InMemory DB so UI dropdowns have items
if (useInMemoryDb)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<LlmDbContext>>();
        using var db = dbFactory.CreateDbContext();

        if (!db.ModelTypes.Any())
        {
            var mtId = Guid.NewGuid().ToString("N");
            db.ModelTypes.Add(new LlmModelType
            {
                Id = mtId,
                Name = "OpenAI",
                Description = "OpenAI Compatible Models",
                Icon = "thunderbolt",
                DefaultEndpoint = "https://api.openai.com",
                CreatedAt = DateTime.UtcNow
            });
            db.SaveChanges();
        }

        var modelTypeId = db.ModelTypes.Select(x => x.Id).First();

        if (!db.Configs.Any())
        {
            db.Configs.Add(new LlmConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Local-OpenAI-Compatible",
                Description = "Sample config for tests",
                ModelTypeId = modelTypeId,
                BaseUrl = "https://api.openai.com",
                ApiKey = "test-key",
                Model = "gpt-4o-mini",
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        if (!db.Prompts.Any())
        {
            db.Prompts.AddRange(
                new LlmPrompt
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "Planner",
                    Description = "Plan tasks and break down goals",
                    Content = "You are a planning agent. Create step-by-step plans.",
                    CreateTime = DateTime.UtcNow,
                    UpdateTime = DateTime.UtcNow,
                    Version = 1
                },
                new LlmPrompt
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "Researcher",
                    Description = "Search and summarize information",
                    Content = "You are a research agent. Find, cite, and summarize.",
                    CreateTime = DateTime.UtcNow,
                    UpdateTime = DateTime.UtcNow,
                    Version = 1
                }
            );
            db.SaveChanges();
        }

        // Seed a sample AgentGroup app with two members and one internal tool
        if (!db.Apps.Any(a => a.AppType == "AgentGroup" && a.Name == "SampleAgentGroup"))
        {
            var cfg = db.Configs.AsQueryable().FirstOrDefault();
            var pPlanner = db.Prompts.AsQueryable().FirstOrDefault(p => p.Name == "Planner");
            var pResearcher = db.Prompts.AsQueryable().FirstOrDefault(p => p.Name == "Researcher");
            if (cfg != null && pPlanner != null && pResearcher != null)
            {
                var appId = Guid.NewGuid().ToString("N");
                var agentApp = new LlmApp
                {
                    Id = appId,
                    Name = "SampleAgentGroup",
                    Description = "InMemory sample agent group for smoke tests",
                    AppType = "AgentGroup",
                    OrchestrationMode = OrchestrationMode.Sequential,
                    IsEnabled = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.Apps.Add(agentApp);
                db.SaveChanges();

                var m1 = new AgentMember
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "Planner",
                    Role = "planner",
                    Order = 1,
                    LlmAppId = agentApp.Id!,
                    LlmPromptId = pPlanner.Id!,
                    LlmConfigId = cfg.Id!
                };
                var m2 = new AgentMember
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "Researcher",
                    Role = "researcher",
                    Order = 2,
                    LlmAppId = agentApp.Id!,
                    LlmPromptId = pResearcher.Id!,
                    LlmConfigId = cfg.Id!
                };
                db.AgentMembers.AddRange(m1, m2);
                db.SaveChanges();

                // Note: Tools are now bound to Prompts, not AgentMembers
                // See PromptTool entity for tool bindings
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[InMemory Seed] Failed: {ex}");
    }
}

// Apply database migrations and seed initial data if needed
if (!useInMemoryDb)
{
    using (var scope = app.Services.CreateScope())
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<LlmDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        
        try
        {
            logger.LogInformation($"Applying database migrations for {databaseType}...");
            dbContext.Database.Migrate();
            logger.LogInformation("Database migrations applied successfully");

            // Seed initial model types if none exist
            if (!await dbContext.ModelTypes.AnyAsync())
            {
                logger.LogInformation("Seeding initial model types...");
                dbContext.ModelTypes.AddRange(
                    new LlmModelType
                    {
                        Name = "OpenAI",
                        Description = "OpenAI Compatible Models",
                        Icon = "thunderbolt",
                        DefaultEndpoint = "https://api.openai.com"
                    },
                    new LlmModelType
                    {
                        Name = "DeepSeek",
                        Description = "DeepSeek Models",
                        Icon = "robot",
                        DefaultEndpoint = "https://api.deepseek.com"
                    },
                    new LlmModelType
                    {
                        Name = "Qwen",
                        Description = "Qwen Models",
                        Icon = "cloud",
                        DefaultEndpoint = "https://dashscope.aliyuncs.com"
                    },
                    new LlmModelType
                    {
                        Name = "Ollama",
                        Description = "Ollama Local Models",
                        Icon = "laptop",
                        DefaultEndpoint = "http://localhost:11434"
                    }
                );
                await dbContext.SaveChangesAsync();
                logger.LogInformation("Initial model types seeded successfully");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to apply migrations or seed data for {databaseType}");
        }
    }
}

// Initialize Tool Metadata Cache on startup
using (var scope = app.Services.CreateScope())
{
    var toolMetadataService = scope.ServiceProvider.GetRequiredService<LY.LlmPool.Web.Services.Tools.ToolMetadataService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        logger.LogInformation("Initializing Tool Metadata Cache...");
        await toolMetadataService.InitializeAsync();
        logger.LogInformation("Tool Metadata Cache initialized successfully");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to initialize Tool Metadata Cache");
        // Non-critical, continue startup
    }
}

app.MapHealthChecks("/health");

app.Run();

public partial class Program { }
