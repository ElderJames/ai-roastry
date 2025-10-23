using LY.LlmPool.Web.Services.Telemetry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;

namespace LY.LlmPool.Web.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Ensure we use in-memory DB for tests
        Environment.SetEnvironmentVariable("USE_INMEMORY_DB", "true");

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ActivityTracePersistenceService>(); 
            // Override the named HttpClient used for upstream LLM calls
            services.AddHttpClient("UpstreamLlm")
                .AddHttpMessageHandler(() => new FakeUpstreamMessageHandler());

            // Also override the named HttpClient used by ChatClientService to call local OpenAI-compatible endpoints
            services.AddHttpClient("LlmPoolApi")
                .AddHttpMessageHandler(() => new FakeUpstreamMessageHandler());
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Make sure we run in Development environment for verbose errors if needed
        builder.UseEnvironment(Environments.Development);
        return base.CreateHost(builder);
    }
}
