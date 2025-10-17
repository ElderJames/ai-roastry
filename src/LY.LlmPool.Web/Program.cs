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
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
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
    builder.Services.AddDbContextFactory<LlmDbContext>(options =>
        options.UseNpgsql(connectionString));

    builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
        options.UseNpgsql(connectionString));
}

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// Add Identity services
builder.Services.AddIdentityCore<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = true)
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

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
builder.Services.AddScoped<LY.LlmPool.Web.Services.Agents.AgentOrchestratorService>();
builder.Services.AddScoped<McpServerConfigService>();

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
    options.MaximumPayloadBytes = 10 * 1024 * 1024; // 10MB per cache entry
    options.MaximumKeyLength = 1024; // Max key length

    // Default expiration (can be overridden per entry)
    options.DefaultEntryOptions = new Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromHours(1), // Tool metadata expires after 1 hour
        LocalCacheExpiration = TimeSpan.FromMinutes(30) // L1 cache expires faster
    };
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

// 🎯 注册 OpenTelemetry Activity 追踪服务
builder.Services.AddSingleton<LY.LlmPool.Web.Services.Telemetry.ActivityTraceService>();

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
.AddHttpMessageHandler<ActivityPropagationHandler>() // 🔑 传播 Activity Context
.AddHttpMessageHandler<LoggingHttpHandler>();

// Named HttpClient for upstream LLM calls (tests can override it)
builder.Services.AddHttpClient("UpstreamLlm")
    .AddHttpMessageHandler<LoggingHttpHandler>();

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

if (!useInMemoryDb && Environment.GetEnvironmentVariable("APPLY_MIGRATIONS")?.ToLower() == "true")
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    dbContext.Database.Migrate();
    var llmDbContext = scope.ServiceProvider.GetRequiredService<LlmDbContext>();
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
//app.UseAuthentication();
app.UseAntiforgery();
//app.UseAuthorization();

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
//using (var scope = app.Services.CreateScope())
//{
//    var dbContext = scope.ServiceProvider.GetRequiredService<LlmDbContext>();
//    dbContext.Database.Migrate();

//    // Seed initial model types if none exist
//    if (!await dbContext.ModelTypes.AnyAsync())
//    {
//        dbContext.ModelTypes.AddRange(
//            new LlmModelType
//            {
//                Name = "OpenAI",
//                Description = "OpenAI Compatible Models",
//                Icon = "thunderbolt",
//                DefaultEndpoint = "https://api.openai.com"
//            },
//            new LlmModelType
//            {
//                Name = "DeepSeek",
//                Description = "DeepSeek Models",
//                Icon = "robot",
//                DefaultEndpoint = "https://api.deepseek.com"
//            },
//            new LlmModelType
//            {
//                Name = "Qwen",
//                Description = "Qwen Models",
//                Icon = "cloud",
//                DefaultEndpoint = "https://dashscope.aliyuncs.com"
//            },
//            new LlmModelType
//            {
//                Name = "Ollama",
//                Description = "Ollama Local Models",
//                Icon = "laptop",
//                DefaultEndpoint = "http://localhost:11434"
//            }
//        );
//        await dbContext.SaveChangesAsync();
//    }
//}

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
