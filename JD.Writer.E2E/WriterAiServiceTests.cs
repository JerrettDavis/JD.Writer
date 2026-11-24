using JD.Writer.ApiService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.CircuitBreaker;

namespace JD.Writer.E2E;

[TestFixture]
public sealed class WriterAiServiceTests
{
    [Test]
    public async Task StreamAssistAsync_OpenOllamaCircuit_ReturnsFallbackChunks()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI:Provider"] = "ollama",
            ["AI:Ollama:Model"] = "test-model",
            ["AI:NativeLlama:Enabled"] = "false"
        }).Build();
        using var handler = new OpenCircuitHandler();
        var factory = new OpenCircuitClientFactory(handler);
        var service = new WriterAiService(configuration, NullLogger<WriterAiService>.Instance, factory);

        var chunks = new List<AssistStreamChunk>();
        await foreach (var chunk in service.StreamAssistAsync("plugin-risks", "# Draft", "List risks", CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.That(handler.Requests, Is.EqualTo(1));
        Assert.That(chunks, Is.Not.Empty);
        Assert.That(chunks.Select(chunk => chunk.Source), Has.All.EqualTo("fallback"));
    }

    private sealed class OpenCircuitClientFactory(OpenCircuitHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class OpenCircuitHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new BrokenCircuitException();
        }
    }
}
