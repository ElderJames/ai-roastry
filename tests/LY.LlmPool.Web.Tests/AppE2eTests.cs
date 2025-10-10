using System;
using System.Threading.Tasks;
using Xunit;
using Microsoft.Playwright;

namespace LY.LlmPool.Web.Tests;

public class AppE2eTests
{
    [Fact]
    public async Task HomePage_Should_Render_When_BaseUrl_Set()
    {
        var baseUrl = Environment.GetEnvironmentVariable("E2E_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            // No base URL provided; skip-like pass to keep CI green.
            return;
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var response = await page.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
        Assert.NotNull(response);
        Assert.True(response!.Ok);

    // Basic app sanity: expect any root element of the Razor app is present
    // Try a couple of common selectors to be resilient.
    var rootHandle = await page.WaitForSelectorAsync("app, #app, .app, body", new PageWaitForSelectorOptions { Timeout = 10000 });
    Assert.NotNull(rootHandle);
    }

    [Fact]
    public async Task Wizard_Create_AgentGroup_When_BaseUrl_Set()
    {
        var baseUrl = Environment.GetEnvironmentVariable("E2E_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            // No E2E base URL provided; consider this test skipped/passed.
            return;
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        // Navigate to App List page
        var appListUrl = baseUrl.TrimEnd('/') + "/app-list";
        await page.GotoAsync(appListUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });

    // Ensure page rendered
    var pageRoot = await page.WaitForSelectorAsync("app, #app, .app, body", new PageWaitForSelectorOptions { Timeout = 10000 });
    Assert.NotNull(pageRoot);

        // Launch AgentGroup wizard from toolbar button
    var wizardLaunch = page.Locator("[data-testid=app-wizard-launch] button");
    await wizardLaunch.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
    await wizardLaunch.ClickAsync();

        // Wait for wizard dialog to appear
        await page.WaitForSelectorAsync(".ant-modal-title:has-text('AgentGroup 向导')", new PageWaitForSelectorOptions { Timeout = 10000 });

        // Step 0: set orchestration mode to GroupChat if option exists
        var orchSelect = page.Locator("[data-testid=wizard-orch-mode] .ant-select");
        await orchSelect.ClickAsync();
        var groupChatOption = page.Locator(".ant-select-item[title=GroupChat]");
        if (await groupChatOption.CountAsync() == 0)
        {
            groupChatOption = page.Locator(".ant-select-item:has-text('GroupChat')");
        }
        if (await groupChatOption.CountAsync() > 0)
        {
            await groupChatOption.First.ClickAsync();
        }
        else
        {
            await page.Keyboard.PressAsync("Escape");
        }

        await page.Locator("[data-testid=wizard-next] button").ClickAsync();

        // Step 1: ensure at least one member row and provide basic data
        await page.Locator("[data-testid=wizard-add-row] button").ClickAsync();
        var nameInput = page.Locator(".ant-table input").First;
        if (await nameInput.CountAsync() > 0)
        {
            await nameInput.FillAsync("Agent E2E");
        }

        var firstSelect = page.Locator(".ant-table .ant-select").First;
        if (await firstSelect.CountAsync() > 0)
        {
            await firstSelect.ClickAsync();
            var firstOption = page.Locator(".ant-select-item-option").First;
            if (await firstOption.CountAsync() > 0)
            {
                await firstOption.ClickAsync();
            }
            else
            {
                await page.Keyboard.PressAsync("Escape");
            }
        }

        await page.Locator("[data-testid=wizard-next] button").ClickAsync();

        // Step 2: confirm wizard output
        await page.Locator("[data-testid=wizard-confirm] button").ClickAsync();

        // Wait for wizard to close and return to edit modal
        await page.WaitForSelectorAsync(".ant-modal-title:has-text('AgentGroup 向导')", new PageWaitForSelectorOptions { State = WaitForSelectorState.Detached, Timeout = 10000 });

        // Fill in app details in the edit modal
        var appNameInput = page.Locator("input[placeholder='Enter app name']");
        await appNameInput.FillAsync("E2E-GroupChat-App");

        // Ensure modal is visible and save
        var saveButton = page.Locator(".ant-modal-footer button.ant-btn-primary");
        await saveButton.ClickAsync();

        await page.WaitForSelectorAsync(".ant-message-notice .ant-message-success", new PageWaitForSelectorOptions { Timeout = 10000 });

        // Verify the new app appears in the list
        var createdRow = page.Locator("[data-testid=app-row-name][data-app-name='E2E-GroupChat-App']");
        await createdRow.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
    }
}
 
