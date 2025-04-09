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
        EndpointCallRecord? callRecord = null;
        var requestStartTime = DateTime.UtcNow;
        object? requestData = null;
        string? requestBody = null;
        
        try
        {
            // Capture request data
            using (var reader = new StreamReader(Request.Body))
            {
                requestBody = await reader.ReadToEndAsync();
            }
            
            if (!string.IsNullOrEmpty(requestBody))
            {
                try
                {
                    requestData = JsonSerializer.Deserialize<object>(requestBody);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Error parsing request JSON");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading request data");
        }
        
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
        
        try
        {
            // Create initial call record
            callRecord = await _llmPoolService.CreateCallRecordAsync(apiKey, requestData);
            
            var modelAcquireStartTime = DateTime.UtcNow;
            var config = await _llmPoolService.GetAvailableConfigByKeyAsync(apiKey);
            var waitTime = DateTime.UtcNow - modelAcquireStartTime;
            
            // Update call record with waiting time
            if (callRecord != null)
            {
                callRecord.WaitTime = waitTime;
            }
            
            if (config == null)
            {
                // Update call record with error
                if (callRecord != null)
                {
                    callRecord.IsSuccessful = false;
                    callRecord.ErrorMessage = "No available model found or invalid API key";
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }
                
                Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                await Response.WriteAsJsonAsync(new { error = "No available model found or invalid API key" });
                return;
            }

            // Update call record with config info
            if (callRecord != null)
            {
                callRecord.LlmConfigId = config.Id;
                await _llmPoolService.UpdateCallRecordAsync(callRecord);
            }

            try
            {
                var client = _httpClientFactory.CreateClient();
                var proxyRequest = await CreateProxyRequest(config, requestBody);

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

                // Record model call start time
                var modelCallStartTime = DateTime.UtcNow;
                if (callRecord != null)
                {
                    callRecord.ModelCallStartedAt = modelCallStartTime;
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }

                using var response = await client.SendAsync(proxyRequest, HttpCompletionOption.ResponseHeadersRead);
                
                // Record model response start time
                var modelResponseStartTime = DateTime.UtcNow;
                if (callRecord != null)
                {
                    callRecord.ModelResponseStartedAt = modelResponseStartTime;
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }

                // Copy status code and headers
                Response.StatusCode = (int)response.StatusCode;
                foreach (var header in response.Headers)
                {
                    Response.Headers[header.Key] = header.Value.ToArray();
                }

                // Stream the response
                using var responseStream = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[8192];
                var memoryStream = new MemoryStream();
                int bytesRead;

                while ((bytesRead = await responseStream.ReadAsync(buffer)) > 0)
                {
                    await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead));
                    await Response.Body.FlushAsync();
                    
                    // Also save to memory stream for logging
                    await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                }

                // Try to capture response data for logging
                try
                {
                    if (callRecord != null)
                    {
                        memoryStream.Position = 0;
                        using var reader = new StreamReader(memoryStream);
                        var responseContent = await reader.ReadToEndAsync();

                        if (!string.IsNullOrEmpty(responseContent))
                        {
                            try
                            {
                                callRecord.ResponseData = JsonSerializer.Deserialize<object>(responseContent);
                            }
                            catch
                            {
                                // If response is not valid JSON (like streaming data), store as string
                                callRecord.ResponseDataJson = JsonSerializer.Serialize(responseContent);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error capturing response data for logging");
                }

                // Record model response end time and success status
                if (callRecord != null)
                {
                    callRecord.ModelResponseEndedAt = DateTime.UtcNow;
                    callRecord.IsSuccessful = response.IsSuccessStatusCode;
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }
            }
            catch (Exception ex)
            {
                // Update call record with error
                if (callRecord != null)
                {
                    callRecord.IsSuccessful = false;
                    callRecord.ErrorMessage = ex.Message;
                    callRecord.ModelResponseEndedAt = DateTime.UtcNow;
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }
                
                _logger.LogError(ex, "Error processing request");
                Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await Response.WriteAsJsonAsync(new { error = "Internal server error" });
            }
            finally
            {
                if (config != null)
                {
                    _llmPoolService.ReleaseConfig(config.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in chat completions");
            Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await Response.WriteAsJsonAsync(new { error = "Internal server error" });
        }
    }

    private async Task<HttpRequestMessage> CreateProxyRequest(LlmConfig config, string? requestBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{config.BaseUrl}/v1/chat/completions");

        // Copy headers
        foreach (var header in Request.Headers)
        {
            if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        // Set API key
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);

        // Add additional headers if configured
        var additionalHeaders = config.AdditionalHeaders;
        if (additionalHeaders != null)
        {
            foreach (var header in additionalHeaders)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        // Set request content
        if (!string.IsNullOrEmpty(requestBody))
        {
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        }

        return request;
    }
} 