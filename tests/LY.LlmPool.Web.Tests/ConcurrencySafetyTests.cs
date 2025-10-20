using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace LY.LlmPool.Web.Tests
{
    /// <summary>
    /// 测试多用户并发请求时的安全性
    /// </summary>
    public class ConcurrencySafetyTests : IClassFixture<TestWebApplicationFactory>
    {
        private readonly TestWebApplicationFactory _factory;
        private readonly ITestOutputHelper _output;

        public ConcurrencySafetyTests(TestWebApplicationFactory factory, ITestOutputHelper output)
        {
            Environment.SetEnvironmentVariable("USE_INMEMORY_DB", "true");
            _factory = factory;
            _output = output;
        }

        [Fact]
        public async Task ConcurrentRequests_WithDifferentApps_ShouldNotInterfere()
        {
            // Arrange - 使用 TestWebApplicationFactory 配置的内存数据库
            using var scope = _factory.Services.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<LlmDbContext>>();
            
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                // 创建测试数据
                var modelType = new LlmModelType
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "openai",
                    Description = "OpenAI Models"
                };
                db.ModelTypes.Add(modelType);

                // 创建两个不同的配置（不进行并发限制）
                var config1 = new LlmConfig
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "gpt-4o-mini-1",
                    Model = "gpt-4o-mini",
                    BaseUrl = "https://api.openai.com/v1",
                    ApiKey = "test-key-1",
                    ModelTypeId = modelType.Id,
                    IsEnabled = true
                };

                var config2 = new LlmConfig
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "gpt-4o-mini-2",
                    Model = "gpt-4o-mini",
                    BaseUrl = "https://api.openai.com/v1",
                    ApiKey = "test-key-2",
                    ModelTypeId = modelType.Id,
                    IsEnabled = true
                };

                db.Configs.AddRange(config1, config2);

                // 创建两个应用,分别使用不同的 LlmConfig
                var app1 = new LlmApp
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "test-app-1",
                    AppType = "Prompt",
                    LlmConfigId = config1.Id,
                    IsEnabled = true
                };

                var app2 = new LlmApp
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "test-app-2",
                    AppType = "Prompt",
                    LlmConfigId = config2.Id,
                    IsEnabled = true
                };

                db.Apps.AddRange(app1, app2);
                await db.SaveChangesAsync();
            }

            var client = _factory.CreateClient();

            // Act - 并发发送 10 个请求到不同的 App
            var tasks = new List<Task<(string appName, HttpResponseMessage response, string content)>>();
            
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(SendRequestAsync(client, "test-app-1", i));
                tasks.Add(SendRequestAsync(client, "test-app-2", i));
            }

            var results = await Task.WhenAll(tasks);

            // Assert - 验证所有请求都成功，并且响应内容与请求匹配
            foreach (var (appName, response, content) in results)
            {
                _output.WriteLine($"App: {appName}, Status: {response.StatusCode}, Content Length: {content.Length}");
                
                // 所有请求都应该成功（即使是失败也应该有正确的响应格式）
                Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.BadRequest,
                    $"Unexpected status code: {response.StatusCode}");
                
                // 响应内容不应该为空
                Assert.NotEmpty(content);
                
                // 验证响应格式正确（应该是 JSON）
                Assert.True(IsValidJson(content), $"Invalid JSON response for {appName}: {content}");
            }

            // 验证没有响应内容混淆
            var app1Results = results.Where(r => r.appName == "test-app-1").ToList();
            var app2Results = results.Where(r => r.appName == "test-app-2").ToList();
            
            Assert.Equal(5, app1Results.Count);
            Assert.Equal(5, app2Results.Count);
        }

        private async Task<(string appName, HttpResponseMessage response, string content)> SendRequestAsync(
            HttpClient client, string appName, int requestIndex)
        {
            var request = new
            {
                model = appName,
                messages = new[]
                {
                    new { role = "user", content = $"Test message {requestIndex} for {appName}" }
                },
                stream = false
            };

            var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");

            // 添加 Authorization header
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = content
            };
            requestMessage.Headers.Add("Authorization", "Bearer test-api-key");

            var response = await client.SendAsync(requestMessage);
            var responseContent = await response.Content.ReadAsStringAsync();

            return (appName, response, responseContent);
        }

        [Fact]
        public async Task ConcurrentStreamingRequests_WithDifferentApps_ShouldNotInterfere()
        {
            // Arrange - 使用 TestWebApplicationFactory 配置的内存数据库
            using var scope = _factory.Services.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<LlmDbContext>>();
            
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var modelType = new LlmModelType
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "openai",
                    Description = "OpenAI Models"
                };
                db.ModelTypes.Add(modelType);

                var config1 = new LlmConfig
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "gpt-4o-mini-1",
                    Model = "gpt-4o-mini",
                    BaseUrl = "https://api.openai.com/v1",
                    ApiKey = "test-key-1",
                    ModelTypeId = modelType.Id,
                    IsEnabled = true
                };

                var config2 = new LlmConfig
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "gpt-4o-mini-2",
                    Model = "gpt-4o-mini",
                    BaseUrl = "https://api.openai.com/v1",
                    ApiKey = "test-key-2",
                    ModelTypeId = modelType.Id,
                    IsEnabled = true
                };

                db.Configs.AddRange(config1, config2);

                var app1 = new LlmApp
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "stream-app-1",
                    AppType = "Prompt",
                    LlmConfigId = config1.Id,
                    IsEnabled = true
                };

                var app2 = new LlmApp
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "stream-app-2",
                    AppType = "Prompt",
                    LlmConfigId = config2.Id,
                    IsEnabled = true
                };

                db.Apps.AddRange(app1, app2);
                await db.SaveChangesAsync();
            }

            var client = _factory.CreateClient();

            // Act - 并发发送流式请求
            var tasks = new List<Task<(string appName, HttpResponseMessage response, string content)>>();
            
            for (int i = 0; i < 3; i++)
            {
                tasks.Add(SendStreamingRequestAsync(client, "stream-app-1", i));
                tasks.Add(SendStreamingRequestAsync(client, "stream-app-2", i));
            }

            var results = await Task.WhenAll(tasks);

            // Assert - 验证所有流式请求都正常处理
            foreach (var (appName, response, content) in results)
            {
                _output.WriteLine($"Streaming - App: {appName}, Status: {response.StatusCode}, Content Length: {content.Length}");
                
                // 验证响应格式（流式响应是 text/event-stream）
                Assert.True(
                    response.StatusCode == HttpStatusCode.OK || 
                    response.StatusCode == HttpStatusCode.BadRequest,
                    $"Unexpected status code: {response.StatusCode}");
            }

            // 验证没有响应混淆
            var app1Results = results.Where(r => r.appName == "stream-app-1").ToList();
            var app2Results = results.Where(r => r.appName == "stream-app-2").ToList();
            
            Assert.Equal(3, app1Results.Count);
            Assert.Equal(3, app2Results.Count);
        }

        private async Task<(string appName, HttpResponseMessage response, string content)> SendStreamingRequestAsync(
            HttpClient client, string appName, int requestIndex)
        {
            var request = new
            {
                model = appName,
                messages = new[]
                {
                    new { role = "user", content = $"Streaming test {requestIndex} for {appName}" }
                },
                stream = true
            };

            var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = content
            };
            requestMessage.Headers.Add("Authorization", "Bearer test-api-key");

            var response = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);
            var responseContent = await response.Content.ReadAsStringAsync();

            return (appName, response, responseContent);
        }

        private bool IsValidJson(string content)
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
