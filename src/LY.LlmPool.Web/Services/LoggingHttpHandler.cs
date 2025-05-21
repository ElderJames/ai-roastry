using System.Text;

namespace LY.LlmPool.Web.Services;

public class LoggingHttpHandler : DelegatingHandler
{
    private readonly ILogger<LoggingHttpHandler> _logger;

    public LoggingHttpHandler(ILogger<LoggingHttpHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString();

        try
        {
            // Log request
            var requestContent = request.Content != null ? 
                await request.Content.ReadAsStringAsync(cancellationToken) : "";
            
            _logger.LogInformation(
                "Request {RequestId}: {Method} {Url}\nHeaders: {Headers}\nContent: {Content}",
                requestId,
                request.Method,
                request.RequestUri,
                string.Join(", ", request.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")),
                requestContent
            );

            // Send request and get response
            var response = await base.SendAsync(request, cancellationToken);

            // Log response headers
            _logger.LogInformation(
                "Response {RequestId}: {StatusCode}\nHeaders: {Headers}",
                requestId,
                response.StatusCode,
                string.Join(", ", response.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}"))
            );

            // Only log response content for non-streaming responses
            if (!response.Headers.Contains("Transfer-Encoding") || 
                !response.Headers.TransferEncoding.ToString().Contains("chunked"))
            {
                var responseContent = response.Content != null ? 
                    await response.Content.ReadAsStringAsync(cancellationToken) : "";

                _logger.LogInformation(
                    "Response {RequestId} Content: {Content}",
                    requestId,
                    responseContent
                );

                // Create a new response with the original content
                var newResponse = new HttpResponseMessage(response.StatusCode)
                {
                    Content = new StringContent(responseContent, Encoding.UTF8, response.Content?.Headers.ContentType?.MediaType),
                    RequestMessage = response.RequestMessage,
                    Version = response.Version
                };

                // Copy headers
                foreach (var header in response.Headers)
                {
                    newResponse.Headers.Add(header.Key, header.Value);
                }

                return newResponse;
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in request {RequestId}", requestId);
            throw;
        }
    }
} 