using LY.LlmPool.Web.Components;
using LY.LlmPool.Web.Components.Account;
using LY.LlmPool.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using AntDesign;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityUserAccessor>();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

// Add AntDesign services
builder.Services.AddAntDesign();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = true)
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

// Add LLM Pool services
builder.Services.AddScoped<LlmPoolService>();
builder.Services.AddHttpClient();
builder.Services.AddControllers();

// Configure DbContext
builder.Services.AddDbContext<LlmDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

// Map controllers
app.MapControllers();

// Apply database migrations and seed initial data if needed
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<LlmDbContext>();
    dbContext.Database.Migrate();

    // Seed initial model types if none exist
    if (!await dbContext.ModelTypes.AnyAsync())
    {
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
            }
        );
        await dbContext.SaveChangesAsync();
    }
}

app.Run();
