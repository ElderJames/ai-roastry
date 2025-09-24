# LY.LlmPool.Client (SDK)

A minimal .NET client for calling Llm-Pool OpenAI-compatible chat endpoints via Microsoft Semantic Kernel.

## Install

Add the project to your solution and reference it from your app.

## Usage

```csharp
using LY.LlmPool.Client;
using Microsoft.SemanticKernel;

var client = new LlmPoolClient("https://your-llmpool-base/v1", "your-api-key");

// Non-streaming
var reply = await client.ChatAsync(
    model: "your-app-or-model-name",
    messages: new [] {
        new ClientMessage { Role = "system", Content = "You are helpful." },
        new ClientMessage { Role = "user", Content = "Hello" }
    },
    parameters: new Dictionary<string,object> { ["city"] = "Shanghai" }
);
Console.WriteLine(reply);

// Streaming
await foreach (var token in client.ChatStreamAsync(
    model: "your-app-or-model-name",
    messages: new [] { new ClientMessage { Role = "user", Content = "Tell me a joke" } }
))
{
    Console.Write(token);
}

// With tools
public class MyTools
{
    [KernelFunction("sum")]
    public int Sum(int a, int b) => a + b;
}

var tools = new object[] { new MyTools() };
var reply2 = await client.ChatAsync(
    model: "your-app-or-model-name",
    messages: new [] { new ClientMessage { Role = "user", Content = "Call sum with a=2,b=3" } },
    toolObjects: tools
);
Console.WriteLine(reply2);
```
