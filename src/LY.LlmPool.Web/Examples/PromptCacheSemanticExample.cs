using System;
using System.Threading.Tasks;
using LY.LlmPool.Web.Services.PromptCache;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LY.LlmPool.Web.Examples;

/// <summary>
/// Prompt 缓存语义匹配示例代码
/// </summary>
public class PromptCacheSemanticExample
{
    private readonly PromptCacheService _cacheService;
    private readonly ILogger<PromptCacheSemanticExample> _logger;

    public PromptCacheSemanticExample(
        PromptCacheService cacheService,
        ILogger<PromptCacheSemanticExample> logger)
    {
        _cacheService = cacheService;
        _logger = logger;
    }

    /// <summary>
    /// 示例 1: 基本的缓存使用
    /// </summary>
    public async Task BasicCachingExample()
    {
        var prompt = "What is machine learning?";
        
        // 尝试从缓存获取
        var cached = await _cacheService.TryGetCachedResponseAsync(prompt);
        
        if (cached != null)
        {
            _logger.LogInformation(
                "✅ Cache HIT! Similarity: {Score:F3}, Saved: {Tokens} tokens",
                cached.SimilarityScore,
                cached.SavedTokens
            );
            
            Console.WriteLine($"Cached Response: {cached.CachedResponse}");
            return;
        }
        
        // 缓存未命中，调用 LLM
        _logger.LogInformation("❌ Cache MISS, calling LLM API...");
        var response = await CallLlmApi(prompt);
        
        // 缓存响应
        await _cacheService.CacheResponseAsync(
            prompt,
            response,
            promptTokens: 50,
            completionTokens: 150
        );
        
        Console.WriteLine($"LLM Response: {response}");
    }

    /// <summary>
    /// 示例 2: 语义匹配演示
    /// </summary>
    public async Task SemanticMatchingExample()
    {
        // 测试一组语义相似的 prompt
        var prompts = new[]
        {
            "Explain what artificial intelligence is",           // 原始
            "What is artificial intelligence?",                  // 相似度 ~0.95
            "Can you tell me about AI?",                        // 相似度 ~0.92
            "Define artificial intelligence",                    // 相似度 ~0.96
            "How does machine learning work?"                    // 相似度 ~0.70（不应命中）
        };

        // 第一个 prompt：建立缓存
        Console.WriteLine($"\n🔷 Testing: {prompts[0]}");
        var response = await CallLlmApi(prompts[0]);
        await _cacheService.CacheResponseAsync(
            prompts[0],
            response,
            promptTokens: 60,
            completionTokens: 200
        );
        Console.WriteLine("✅ Cached as baseline");

        // 测试其他 prompt 的匹配情况
        for (int i = 1; i < prompts.Length; i++)
        {
            Console.WriteLine($"\n🔷 Testing: {prompts[i]}");
            
            var cached = await _cacheService.TryGetCachedResponseAsync(prompts[i]);
            
            if (cached != null)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✅ Cache HIT!");
                Console.WriteLine($"   Similarity: {cached.SimilarityScore:F3}");
                Console.WriteLine($"   Saved Tokens: {cached.SavedTokens}");
                Console.WriteLine($"   Response: {cached.CachedResponse[..Math.Min(100, cached.CachedResponse.Length)]}...");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"❌ Cache MISS (similarity below threshold)");
                Console.ResetColor();
            }
            
            await Task.Delay(500); // 避免请求过快
        }
    }

    /// <summary>
    /// 示例 3: 实际应用场景 - 客服问答
    /// </summary>
    public async Task CustomerSupportExample()
    {
        Console.WriteLine("\n=== Customer Support Scenario ===\n");
        
        // 模拟客户提出的相似问题
        var customerQuestions = new[]
        {
            "How do I reset my password?",
            "I forgot my password, what should I do?",
            "Can you help me reset my account password?",
            "My password doesn't work, how to reset it?",
            "What's the process for password recovery?"
        };

        int totalTokensSaved = 0;
        int cacheHits = 0;

        foreach (var question in customerQuestions)
        {
            Console.WriteLine($"\n👤 Customer: {question}");
            
            var cached = await _cacheService.TryGetCachedResponseAsync(
                question,
                llmConfigId: "customer-support",
                modelName: "gpt-4"
            );
            
            if (cached != null)
            {
                cacheHits++;
                totalTokensSaved += (int)cached.SavedTokens;
                
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"🤖 Agent (cached, similarity={cached.SimilarityScore:F3}): {cached.CachedResponse}");
                Console.ResetColor();
            }
            else
            {
                var response = await CallLlmApi(question);
                await _cacheService.CacheResponseAsync(
                    question,
                    response,
                    llmConfigId: "customer-support",
                    modelName: "gpt-4",
                    promptTokens: 40,
                    completionTokens: 100
                );
                
                Console.WriteLine($"🤖 Agent (new): {response}");
            }
        }

        // 显示统计
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n📊 Statistics:");
        Console.WriteLine($"   Total questions: {customerQuestions.Length}");
        Console.WriteLine($"   Cache hits: {cacheHits} ({(float)cacheHits / customerQuestions.Length:P0})");
        Console.WriteLine($"   Total tokens saved: {totalTokensSaved}");
        Console.WriteLine($"   Estimated cost saved: ${totalTokensSaved * 0.00003:F4}");
        Console.ResetColor();
    }

    /// <summary>
    /// 示例 4: 批量处理和性能测试
    /// </summary>
    public async Task BatchProcessingExample()
    {
        Console.WriteLine("\n=== Batch Processing Performance Test ===\n");
        
        var prompts = new[]
        {
            "What is Python?",
            "Explain Python programming language",
            "Tell me about Python",
            "Python programming basics",
            "Introduction to Python"
        };

        var timings = new List<(string prompt, long uncachedMs, long cachedMs)>();

        foreach (var prompt in prompts)
        {
            // 第一次：未缓存
            var sw1 = System.Diagnostics.Stopwatch.StartNew();
            var response = await CallLlmApi(prompt);
            await _cacheService.CacheResponseAsync(prompt, response, promptTokens: 30, completionTokens: 100);
            sw1.Stop();

            await Task.Delay(200);

            // 第二次：使用缓存
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            var cached = await _cacheService.TryGetCachedResponseAsync(prompt);
            sw2.Stop();

            timings.Add((prompt, sw1.ElapsedMilliseconds, sw2.ElapsedMilliseconds));
            
            Console.WriteLine($"Prompt: {prompt[..Math.Min(40, prompt.Length)]}...");
            Console.WriteLine($"  Uncached: {sw1.ElapsedMilliseconds}ms | Cached: {sw2.ElapsedMilliseconds}ms | Speedup: {(float)sw1.ElapsedMilliseconds / sw2.ElapsedMilliseconds:F1}x");
        }

        var avgSpeedup = timings.Average(t => (float)t.uncachedMs / t.cachedMs);
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"\n⚡ Average speedup: {avgSpeedup:F1}x faster with cache!");
        Console.ResetColor();
    }

    /// <summary>
    /// 示例 5: 查看缓存统计
    /// </summary>
    public async Task ViewStatisticsExample()
    {
        Console.WriteLine("\n=== Cache Statistics ===\n");
        
        var stats = await _cacheService.GetStatisticsAsync();
        
        Console.WriteLine($"📈 Total Entries:      {stats.TotalEntries}");
        Console.WriteLine($"🎯 Total Hits:         {stats.TotalHits}");
        Console.WriteLine($"💰 Total Saved Tokens: {stats.TotalSavedTokens:N0}");
        Console.WriteLine($"📊 Hit Rate:           {stats.AverageHitRate:P2}");
        Console.WriteLine($"🗑️  Expired Entries:   {stats.ExpiredEntries}");
        
        // 计算成本节省（假设 GPT-4 价格）
        var inputCostPer1K = 0.03;
        var outputCostPer1K = 0.06;
        var avgInputRatio = 0.33; // 假设 1/3 是输入 tokens
        
        var savedCost = stats.TotalSavedTokens * avgInputRatio * inputCostPer1K / 1000 +
                       stats.TotalSavedTokens * (1 - avgInputRatio) * outputCostPer1K / 1000;
        
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n💵 Estimated cost saved: ${savedCost:F2}");
        Console.ResetColor();
    }

    /// <summary>
    /// 示例 6: 相似度阈值测试
    /// </summary>
    public async Task SimilarityThresholdTesting()
    {
        Console.WriteLine("\n=== Similarity Threshold Testing ===\n");
        
        var basePrompt = "Explain quantum computing";
        var testPrompts = new[]
        {
            ("Explain quantum computing", 1.00),                          // 精确匹配
            ("What is quantum computing?", 0.95),                         // 高度相似
            ("Can you explain quantum computing to me?", 0.92),          // 相似
            ("Tell me about quantum computers", 0.88),                    // 中度相似
            ("How do quantum computers work?", 0.82),                     // 相关但不够相似
            ("What are the applications of quantum mechanics?", 0.65)     // 相关度较低
        };

        // 建立基准缓存
        var baseResponse = await CallLlmApi(basePrompt);
        await _cacheService.CacheResponseAsync(basePrompt, baseResponse);
        Console.WriteLine($"✅ Baseline cached: {basePrompt}\n");

        // 测试不同相似度的 prompt
        foreach (var (prompt, expectedSimilarity) in testPrompts)
        {
            var cached = await _cacheService.TryGetCachedResponseAsync(prompt);
            
            Console.Write($"Prompt: {prompt[..Math.Min(50, prompt.Length)]}... ");
            
            if (cached != null)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"✅ HIT");
                Console.ResetColor();
                Console.WriteLine($" (similarity={cached.SimilarityScore:F3}, expected~{expectedSimilarity:F2})");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"❌ MISS");
                Console.ResetColor();
                Console.WriteLine($" (expected~{expectedSimilarity:F2}, below threshold)");
            }
        }
        
        Console.WriteLine("\n💡 Tip: Adjust 'SimilarityThreshold' in appsettings.json to control matching behavior");
    }

    /// <summary>
    /// 模拟 LLM API 调用
    /// </summary>
    private async Task<string> CallLlmApi(string prompt)
    {
        // 模拟 API 延迟
        await Task.Delay(Random.Shared.Next(500, 1500));
        
        // 返回模拟响应
        var lowerPrompt = prompt.ToLower();
        
        if (lowerPrompt.Contains("password"))
            return "To reset your password, please visit https://example.com/reset and follow the instructions.";
        
        if (lowerPrompt.Contains("python"))
            return "Python is a high-level, interpreted programming language known for its simplicity and readability.";
        
        if (lowerPrompt.Contains("machine learning") || lowerPrompt.Contains("artificial intelligence") || lowerPrompt.Contains("ai"))
            return "Machine learning is a subset of artificial intelligence that enables systems to learn and improve from experience without being explicitly programmed.";
        
        if (lowerPrompt.Contains("quantum"))
            return "Quantum computing leverages quantum mechanical phenomena like superposition and entanglement to perform computations.";
        
        return $"This is a simulated response to: {prompt}";
    }

    /// <summary>
    /// 运行所有示例
    /// </summary>
    public async Task RunAllExamples()
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   Prompt Cache Semantic Matching Examples            ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════╝\n");

        try
        {
            await BasicCachingExample();
            await Task.Delay(1000);
            
            await SemanticMatchingExample();
            await Task.Delay(1000);
            
            await CustomerSupportExample();
            await Task.Delay(1000);
            
            await BatchProcessingExample();
            await Task.Delay(1000);
            
            await SimilarityThresholdTesting();
            await Task.Delay(1000);
            
            await ViewStatisticsExample();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n❌ Error: {ex.Message}");
            Console.ResetColor();
            throw;
        }

        Console.WriteLine("\n✅ All examples completed!");
    }
}

/// <summary>
/// 程序入口（用于独立测试）
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        // 注意：在实际应用中，这些服务应该通过 DI 注入
        // 这里只是演示目的
        
        Console.WriteLine("Please run these examples from the web application context");
        Console.WriteLine("where all services are properly configured and injected.");
        Console.WriteLine("\nTo use this example:");
        Console.WriteLine("1. Add a controller endpoint that calls these methods");
        Console.WriteLine("2. Or integrate into your existing service layer");
        Console.WriteLine("3. Make sure to configure API keys in appsettings.json");
    }
}
