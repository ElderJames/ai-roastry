using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LY.LlmPool.Web.Data;
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
    public async Task<IActionResult> ChatCompletions(
        [FromBody] JsonDocument requestBody,
        [FromHeader(Name = "X-LLM-Type")] string? llmType = null,
        CancellationToken cancellationToken = default)
    {
        LlmType? targetType = null;
        if (!string.IsNullOrEmpty(llmType) && Enum.TryParse<LlmType>(llmType, true, out var type))
        {
            targetType = type;
        }

        var llm = await _llmPoolService.GetAvailableLlmAsync(targetType, cancellationToken);
        if (llm == null)
        {
            return StatusCode(503, new { error = new { message = "No available LLM found" } });
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, $"{llm.BaseUrl.TrimEnd('/')}/v1/chat/completions");
            
            // Copy original request headers
            foreach (var header in Request.Headers)
            {
                if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            // Set API key
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", llm.ApiKey);

            // Add additional headers
            //foreach (var (key, value) in llm.AdditionalHeaders)
            //{
            //    request.Headers.TryAddWithoutValidation(key, value);
            //}

            // Forward request body
            var json = requestBody.RootElement.GetRawText();
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            
            // Stream the response
            Response.Headers.Append("Transfer-Encoding", "chunked");
            foreach (var header in response.Headers)
            {
                Response.Headers.Append(header.Key, header.Value.ToArray());
            }

            Response.StatusCode = (int)response.StatusCode;
            
            await response.Content.CopyToAsync(Response.Body, cancellationToken);
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing request for LLM {LlmId}", llm.Id);
            return StatusCode(500, new { error = new { message = "Internal server error" } });
        }
        finally
        {
            await _llmPoolService.ReleaseLlm(llm.Id);
        }
    }
} 