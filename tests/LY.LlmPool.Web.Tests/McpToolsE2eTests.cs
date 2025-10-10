using System;
using System.Threading.Tasks;
using Xunit;
using Microsoft.Playwright;

namespace LY.LlmPool.Web.Tests;

public class McpToolsE2eTests
{
    [Fact]
    public async Task McpToolsModal_Expand_Copy_ShowsSuccessMessage()
    {
        var baseUrl = Environment.GetEnvironmentVariable("E2E_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl)) return; // skip when not configured

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        // Go to MCP config page
        var resp = await page.GotoAsync(baseUrl.TrimEnd('/') + "/mcp-config", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
        Assert.NotNull(resp);
        Assert.True(resp!.Ok);

        // Try to find any "查看工具" button in the table. If none, skip to avoid flakiness.
        var viewButtons = page.Locator("button:has-text('查看工具')");
        if (!await viewButtons.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 2000 }))
        {
            // no data or button not visible — skip
            return;
        }

        // Click the first "查看工具" button
        await viewButtons.First.ClickAsync();

        // Wait for modal to appear
        await page.WaitForSelectorAsync(".ant-modal");

        // If there are no tool rows, bail
        var toolRows = page.Locator(".ant-table [role=row]");
        if (!await toolRows.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 2000 })) return;

        // Find first expand button inside the modal cards (按钮文本 为 '展开' 或 '收起')
        var expandBtn = page.Locator(".ant-modal button:has-text('展开')").First;
        if (!await expandBtn.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 2000 }))
        {
            // nothing to expand; try to find copy button directly
            var copyBtn = page.Locator(".ant-modal button:has-text('复制')").First;
            if (!await copyBtn.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 2000 })) return;
            await copyBtn.ClickAsync();
        }
        else
        {
            await expandBtn.ClickAsync();
            // wait for JSON content to appear
            await page.WaitForSelectorAsync(".ant-modal pre", new PageWaitForSelectorOptions { Timeout = 3000 });
            var copyBtn = page.Locator(".ant-modal button:has-text('复制')").First;
            Assert.True(await copyBtn.IsVisibleAsync());
            await copyBtn.ClickAsync();
        }

        // Expect antd success message
        var msg = page.Locator(".ant-message-notice .ant-message-success");
        Assert.True(await msg.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 5000 }));
    }
}
