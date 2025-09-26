using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Linq;
using Xunit;

namespace LY.LlmPool.Client.Tests;

public class ScratchTests
{
    [Fact]
    public void FunctionResultContent_PropertyNames()
    {
#pragma warning disable IL2077
        var props = typeof(Microsoft.SemanticKernel.FunctionResultContent).GetProperties();
#pragma warning restore IL2077
        var names = props.Select(p => p.Name).ToArray();
    Assert.Contains("CallId", names);
        Assert.Contains("FunctionName", names);
        Assert.Contains("Result", names);
    Assert.Contains("PluginName", names);
    }
}
