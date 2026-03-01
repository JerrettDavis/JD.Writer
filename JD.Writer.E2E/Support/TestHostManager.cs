using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;

namespace JD.Writer.E2E.Support;

internal static class TestHostManager
{
    public const string ApiBaseUrl = "http://127.0.0.1:19081";
    public const string WebBaseUrl = "http://127.0.0.1:19080";
    public const string StandaloneWebBaseUrl = "http://127.0.0.1:19082";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static Process? _apiProcess;
    private static Process? _webProcess;
    private static Process? _standaloneWebProcess;
    private static bool _started;
    private static bool _standaloneStarted;

    public static async Task EnsureStartedAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_started)
            {
                return;
            }

            var repoRoot = ResolveRepoRoot();

            _apiProcess = StartDotnetProject(
                projectPath: Path.Combine(repoRoot, "JD.Writer.ApiService", "JD.Writer.ApiService.csproj"),
                url: ApiBaseUrl,
                environment: new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["AI__Provider"] = "ollama",
                    ["AI__Ollama__Endpoint"] = "http://localhost:11434"
                });

            await WaitForHttpReadyAsync(ApiBaseUrl, TimeSpan.FromSeconds(90));

            _webProcess = StartDotnetProject(
                projectPath: Path.Combine(repoRoot, "JD.Writer.Web", "JD.Writer.Web.csproj"),
                url: WebBaseUrl,
                environment: new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["ApiServiceBaseUrl"] = ApiBaseUrl
                });

            await WaitForHttpReadyAsync(WebBaseUrl, TimeSpan.FromSeconds(90));

            _started = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task StopAsync()
    {
        await Gate.WaitAsync();
        try
        {
            StopProcess(_standaloneWebProcess);
            StopProcess(_webProcess);
            StopProcess(_apiProcess);

            _standaloneWebProcess = null;
            _webProcess = null;
            _apiProcess = null;
            _started = false;
            _standaloneStarted = false;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task EnsureStandaloneClientOnlyStartedAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_standaloneStarted)
            {
                return;
            }

            var repoRoot = ResolveRepoRoot();

            _standaloneWebProcess = StartDotnetProject(
                projectPath: Path.Combine(repoRoot, "JD.Writer.Web", "JD.Writer.Web.csproj"),
                url: StandaloneWebBaseUrl,
                environment: new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["AiClient__Mode"] = "local"
                });

            await WaitForHttpReadyAsync(StandaloneWebBaseUrl, TimeSpan.FromSeconds(90));
            _standaloneStarted = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Process StartDotnetProject(string projectPath, string url, IDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(projectPath) ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--no-launch-profile");
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(url);

        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                Console.WriteLine($"[host] {args.Data}");
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                Console.Error.WriteLine($"[host] {args.Data}");
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {projectPath}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private static async Task WaitForHttpReadyAsync(string baseUrl, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(baseUrl);
                if ((int)response.StatusCode is >= 200 and < 500)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Service not ready yet.
            }
            catch (TaskCanceledException)
            {
                // Retry until deadline.
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Timed out waiting for {baseUrl} to respond.");
    }

    private static void StopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var slnPath = Path.Combine(current.FullName, "JD.Writer.sln");
            if (File.Exists(slnPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate JD.Writer.sln from test output directory.");
    }
}
