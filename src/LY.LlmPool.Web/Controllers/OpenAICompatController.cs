using System.Text;
using System.Text.Json;
using LY.LlmPool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LY.LlmPool.Web.Controllers;

[ApiController]
[Route("v1")]
public class OpenAICompatController : ControllerBase
{
    private readonly ILogger<OpenAICompatController> _logger;
    private readonly LlmPoolService _llmPoolService;
    private readonly IHttpClientFactory _httpClientFactory;

    public OpenAICompatController(
        ILogger<OpenAICompatController> logger,
        LlmPoolService llmPoolService,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _llmPoolService = llmPoolService;
        _httpClientFactory = httpClientFactory;
    }

    [HttpPost("chat/completions")]
    public async Task<IActionResult> ChatCompletions(
        [FromBody] JsonDocument requestBody,
        [FromQuery] string? modelType = null,
        CancellationToken cancellationToken = default)
    {
        var config = await _llmPoolService.GetAvailableConfigAsync(modelType ?? "");
        if (config == null)
        {
            return StatusCode(503, new { error = new { message = "No available LLM configuration found." } });
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, $"{config.BaseUrl}/v1/chat/completions")
            {
                Content = new StringContent(requestBody.RootElement.ToString(), Encoding.UTF8, "application/json")
            };

            request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
            foreach (var header in config.AdditionalHeaders)
            {
                request.Headers.Add(header.Key, header.Value);
            }

            var response = await httpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("LLM request failed: {StatusCode} {Content}", response.StatusCode, content);
            }

            return StatusCode((int)response.StatusCode, JsonDocument.Parse(content));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing LLM request");
            return StatusCode(500, new { error = new { message = "Internal server error" } });
        }
        finally
        {
            _llmPoolService.ReleaseConfig(config.Id);
        }
    }

    [HttpPost("{*path}")]
    public async Task<IActionResult> ForwardRequest(
        [FromBody] JsonDocument requestBody,
        [FromRoute] string path,
        [FromQuery] string? modelType = null,
        CancellationToken cancellationToken = default)
    {
        var config = await _llmPoolService.GetAvailableConfigAsync(modelType ?? "");
        if (config == null)
        {
            return StatusCode(503, new { error = new { message = "No available LLM configuration found." } });
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, $"{config.BaseUrl}/v1/{path}")
            {
                Content = new StringContent(requestBody.RootElement.ToString(), Encoding.UTF8, "application/json")
            };

            request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
            foreach (var header in config.AdditionalHeaders)
            {
                request.Headers.Add(header.Key, header.Value);
            }

            var response = await httpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("LLM request failed: {StatusCode} {Content}", response.StatusCode, content);
            }

            return StatusCode((int)response.StatusCode, JsonDocument.Parse(content));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing LLM request");
            return StatusCode(500, new { error = new { message = "Internal server error" } });
        }
        finally
        {
            _llmPoolService.ReleaseConfig(config.Id);
        }
    }
} 