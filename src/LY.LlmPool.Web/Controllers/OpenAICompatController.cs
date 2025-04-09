using System.Net;
using System.Text;
using System.Text.Json;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LY.LlmPool.Web.Controllers;

[ApiController]
[Route("v1")]
public class OpenAICompatController : ControllerBase
{
    private readonly LlmPoolService _llmPoolService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAICompatController> _logger;

    public OpenAICompatController(
        LlmPoolService llmPoolService,
        IHttpClientFactory httpClientFactory,
        ILogger<OpenAICompatController> logger)
    {
        _llmPoolService = llmPoolService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpPost("chat/completions")]
    public async Task ChatCompletions()
    {
        // 从请求头中获取API Key
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader) || 
            string.IsNullOrEmpty(authHeader) || 
            !authHeader.ToString().StartsWith("Bearer "))
        {
            Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            await Response.WriteAsJsonAsync(new { error = "Missing or invalid API key" });
            return;
        }

        var apiKey = authHeader.ToString().Replace("Bearer ", "");
        var config = await _llmPoolService.GetAvailableConfigByKeyAsync(apiKey);
        
        if (config == null)
        {
            Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            await Response.WriteAsJsonAsync(new { error = "No available model found or invalid API key" });
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var proxyRequest = await CreateProxyRequest(config);

            // 设置响应头
            Response.Headers["Transfer-Encoding"] = "chunked";
            if (Request.Headers.Accept.Any(x => x.Contains("text/event-stream")))
            {
                Response.Headers["Content-Type"] = "text/event-stream";
            }
            else
            {
                Response.Headers["Content-Type"] = "application/json";
            }

            using var response = await client.SendAsync(proxyRequest, HttpCompletionOption.ResponseHeadersRead);
            await using var stream = await response.Content.ReadAsStreamAsync();
            await stream.CopyToAsync(Response.Body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing request");
            Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await Response.WriteAsJsonAsync(new { error = "Internal server error" });
        }
        finally
        {
            _llmPoolService.ReleaseConfig(config.Id);
        }
    }

    private async Task<HttpRequestMessage> CreateProxyRequest(LlmConfig config)
    {
        // 创建新的请求
        var proxyRequest = new HttpRequestMessage();
        var requestContent = await new StreamReader(Request.Body).ReadToEndAsync();
        
        // 复制原始请求的属性
        proxyRequest.Method = new HttpMethod(Request.Method);
        proxyRequest.RequestUri = new Uri($"{config.BaseUrl.TrimEnd('/')}/v1/chat/completions");
        
        // 设置请求内容
        if (!string.IsNullOrEmpty(requestContent))
        {
            proxyRequest.Content = new StringContent(requestContent, Encoding.UTF8, "application/json");
        }

        // 设置认证头
        proxyRequest.Headers.Add("Authorization", $"Bearer {config.ApiKey}");

        // 添加额外的请求头
        foreach (var header in config.AdditionalHeaders)
        {
            proxyRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return proxyRequest;
    }
} 