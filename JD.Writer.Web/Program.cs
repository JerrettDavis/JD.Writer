using JD.Writer.Web;
using JD.Writer.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        // Local-first state payloads can exceed the default SignalR message limit.
        // Raise the cap so large persisted note/layer state doesn't kill the circuit on load.
        options.MaximumReceiveMessageSize = 8 * 1024 * 1024;
    });

builder.Services.AddOutputCache();

builder.Services.AddHttpClient<AiAssistantClient>(client =>
    {
        var aiClientMode = (builder.Configuration["AiClient:Mode"] ?? "auto").Trim().ToLowerInvariant();
        if (string.Equals(aiClientMode, "local", StringComparison.Ordinal))
        {
            // Local-only mode uses in-process heuristics and does not call the API service.
            client.BaseAddress = new("http://localhost");
            return;
        }

        // Test and standalone runs can override the API base URL.
        var explicitBaseUrl = builder.Configuration["ApiServiceBaseUrl"];
        if (!string.IsNullOrWhiteSpace(explicitBaseUrl))
        {
            client.BaseAddress = new(explicitBaseUrl);
            return;
        }

        // This URL uses "https+http://" to indicate HTTPS is preferred over HTTP.
        // Learn more about service discovery scheme resolution at https://aka.ms/dotnet/sdschemes.
        client.BaseAddress = new("https+http://apiservice");
    });

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAntiforgery();

app.UseOutputCache();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
