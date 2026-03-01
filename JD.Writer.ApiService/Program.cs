using System.Text.Json;
using JD.Writer.ApiService;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<WriterAiService>();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", () =>
{
    return Results.Ok(new
    {
        service = "JD.Writer AI API",
        providerSummary = WriterAiService.GetProviderSummary(builder.Configuration)
    });
});

app.MapPost("/ai/continue", async (ContinueDraftRequest request, WriterAiService aiService, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Draft))
    {
        return Results.Ok(new ContinueDraftResponse("", "fallback"));
    }

    var continuation = await aiService.ContinueDraftAsync(request.Draft, cancellationToken);
    return Results.Ok(continuation);
});

app.MapPost("/ai/assist/stream", async Task (HttpContext httpContext, AssistStreamRequest request, WriterAiService aiService, CancellationToken cancellationToken) =>
{
    httpContext.Response.ContentType = "application/x-ndjson";

    await foreach (var chunk in aiService.StreamAssistAsync(
        request.Mode,
        request.Draft,
        request.Prompt,
        cancellationToken))
    {
        var payload = JsonSerializer.Serialize(chunk);
        await httpContext.Response.WriteAsync(payload + "\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }
});

app.MapPost("/ai/slash", async (SlashCommandRequest request, WriterAiService aiService, CancellationToken cancellationToken) =>
{
    var response = await aiService.RunSlashCommandAsync(request.Command, request.Draft, request.Prompt, cancellationToken);
    return Results.Ok(response);
});

app.MapDefaultEndpoints();

app.Run();
