using LY.LlmPool.Web.Components;
using LY.LlmPool.Web.Components.Account;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

// Add database contexts
builder.Services.AddDbContextFactory<LlmDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

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
builder.Services.AddScoped<ChatClientService>();
builder.Services.AddScoped<PromptParameterService>();
builder.Services.AddScoped<CallRecordService>();

// Add HTTP client factory
builder.Services.AddHttpClient();

// Named HttpClient for LlmPool API with configurable BaseUrl and HttpContext fallback
builder.Services.AddTransient<LoggingHttpHandler>();
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (Environment.GetEnvironmentVariable("APPLY_MIGRATIONS")?.ToLower() == "true")
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

app.MapHealthChecks("/health");

app.Run();

public partial class Program { }
