var builder = DistributedApplication.CreateBuilder(args);

var apiService = builder.AddProject<Projects.JD_Writer_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.JD_Writer_Web>("webfrontend")
    .WithEnvironment("AiClient__Mode", "remote")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
