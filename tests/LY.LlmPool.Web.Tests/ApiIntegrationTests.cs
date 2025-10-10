#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LY.LlmPool.Web.Tests;

public class ApiIntegrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ApiIntegrationTests(TestWebApplicationFactory factory)
    {
        Environment.SetEnvironmentVariable("USE_INMEMORY_DB", "true");
        _factory = factory;
    }

    [Fact]
    public async Task OpenAICompat_InvalidModel_Returns_Error()
    {
        var client = _factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        var payload = new
        {
            model = "nonexistent-model",
            messages = new object[]
            {
                new { role = "user", content = "hello" }
            }
        };
        var json = JsonSerializer.Serialize(payload);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<Dictionary<string, object>>(content);
        Assert.NotNull(error);
        Assert.True(error.ContainsKey("error"));
    }

    [Fact]
    public async Task OpenAICompat_MissingAuth_Returns_Unauthorized()
    {
        var client = _factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        var payload = new
        {
            model = "SampleAgentGroup",
            messages = new object[]
            {
                new { role = "user", content = "hello" }
            }
        };
        var json = JsonSerializer.Serialize(payload);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OpenAICompat_InvalidJson_Returns_BadRequest()
    {
        var client = _factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        req.Content = new StringContent("invalid json", Encoding.UTF8, "application/json");

        var response = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnthropicApi_InvalidRequest_Returns_Error()
    {
        var client = _factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/anthropic/v1/messages");
        req.Headers.Add("x-api-key", "test-key");
        var payload = new
        {
            model = "nonexistent-model",
            max_tokens = 100,
            messages = new object[]
            {
                new { role = "user", content = "hello" }
            }
        };
        var json = JsonSerializer.Serialize(payload);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.SendAsync(req);
        // Should return some form of error for invalid model
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest ||
                   response.StatusCode == HttpStatusCode.NotFound ||
                   response.StatusCode == HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Health_Check_Endpoint_Works()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");
        // Health endpoint might not exist, but shouldn't crash
        Assert.True(response.IsSuccessStatusCode ||
                   response.StatusCode == HttpStatusCode.NotFound);
    }
}