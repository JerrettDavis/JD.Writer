using Microsoft.Playwright;
using Reqnroll;

namespace JD.Writer.E2E.Support;

[Binding]
public sealed class TestHooks
{
    private readonly ScenarioContext _scenarioContext;

    public TestHooks(ScenarioContext scenarioContext)
    {
        _scenarioContext = scenarioContext;
    }

    [BeforeTestRun(Order = 0)]
    public static async Task BeforeTestRunAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("JDWRITER_SKIP_PLAYWRIGHT_INSTALL"), "1", StringComparison.OrdinalIgnoreCase))
        {
            var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Playwright browser install failed with exit code {exitCode}.");
            }
        }

        await TestHostManager.EnsureStartedAsync();
    }

    [AfterTestRun(Order = 100)]
    public static async Task AfterTestRunAsync()
    {
        await TestHostManager.StopAsync();
    }

    [BeforeScenario(Order = 0)]
    public async Task BeforeScenarioAsync()
    {
        await TestHostManager.EnsureStartedAsync();

        var state = new ScenarioState();
        state.Playwright = await Playwright.CreateAsync();
        state.Browser = await state.Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        state.BrowserContext = await state.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = TestHostManager.WebBaseUrl
        });
        state.Page = await state.BrowserContext.NewPageAsync();
        state.Page.Console += (_, message) => Console.WriteLine($"[browser:{message.Type}] {message.Text}");
        state.Page.PageError += (_, message) => Console.WriteLine($"[browser:error] {message}");

        _scenarioContext.SetState(state);
    }

    [AfterScenario(Order = 100)]
    public async Task AfterScenarioAsync()
    {
        var state = _scenarioContext.GetState();

        if (state.Page is not null)
        {
            await state.Page.CloseAsync();
        }

        if (state.BrowserContext is not null)
        {
            await state.BrowserContext.CloseAsync();
        }

        if (state.Browser is not null)
        {
            await state.Browser.CloseAsync();
        }

        state.Playwright?.Dispose();
    }
}
