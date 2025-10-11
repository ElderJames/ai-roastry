using ModelContextProtocol.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LY.LlmPool.Web.Services.Aggregation;

public static class McpClientTransportFactory
{
    public static IClientTransport Create(string name, McpServerConfigDto config)
    {
        if (config.Type == "stdio")
        {
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = $"{name}-client",
                Command = config.Command!,
                Arguments = config.Args,
                EnvironmentVariables = config.Env
            });
        }
        else if (config.Type == "sse" || config.Type == "http")
        {
            var options = new HttpClientTransportOptions
            {
                Name = $"{name}-client",
                Endpoint = new Uri(config.Url!),
                TransportMode = config.Type == "sse" ? HttpTransportMode.Sse : HttpTransportMode.AutoDetect,
                AdditionalHeaders = config.Headers
            };

            return new HttpClientTransport(options);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported MCP server type: {config.Type}");
        }
    }
}


