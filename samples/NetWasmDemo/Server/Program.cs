// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Net.WebSockets;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Nerdbank.JsonRpc;
using Nerdbank.Streams;
using NetWasmDemo;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebApplication app = builder.Build();

// The browser client: the NetWasm publish output (see ../Client/build.sh), which includes index.html.
string clientRoot = Path.GetFullPath(app.Configuration["ClientRoot"] ?? Path.Combine(app.Environment.ContentRootPath, "..", "Client", "publish", "browser"));
app.Logger.LogInformation("Serving the NetWasm client from {ClientRoot}", clientRoot);
FileExtensionContentTypeProvider contentTypes = new();
contentTypes.Mappings[".wasm"] = "application/wasm";
contentTypes.Mappings[".mjs"] = "text/javascript";
PhysicalFileProvider clientFiles = new(clientRoot);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = clientFiles });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = clientFiles,
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

app.UseWebSockets();
app.Map("/rpc", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
    ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("rpc");
    logger.LogInformation("WebSocket client connected from {RemoteIp}", context.Connection.RemoteIpAddress);

    // Each WebSocket message carries newline-delimited JSON-RPC messages.
    IDuplexPipe pipe = webSocket.UsePipe(cancellationToken: context.RequestAborted);
    JsonRpcJsonChannel channel = new(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, logger);
    using JsonRpc rpc = new(channel) { Logger = logger };
    rpc.AddRpcTarget<IDemoServer>(new DemoServer(logger));
    rpc.Start();

    // Server-to-client notifications, sent through a generated proxy of IDemoClient.
    IDemoClient client = rpc.Attach<IDemoClient>();
    using CancellationTokenSource tickCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    Task ticker = Task.Run(async () =>
    {
        int count = 0;
        while (!tickCancellation.IsCancellationRequested)
        {
            client.Tick(++count, $"Hello from the server at {DateTimeOffset.Now:HH:mm:ss}");
            await Task.Delay(TimeSpan.FromSeconds(2), tickCancellation.Token).ConfigureAwait(false);
        }
    });

    try
    {
        await rpc.Completion;
    }
    catch (Exception ex)
    {
        logger.LogInformation(ex, "JSON-RPC connection ended with an error.");
    }
    finally
    {
        await tickCancellation.CancelAsync();
        try
        {
            await ticker;
        }
        catch (Exception)
        {
            // The ticker ends with cancellation or a disposed connection; either is expected here.
        }
    }

    logger.LogInformation("WebSocket client disconnected.");
});

app.Run();

internal sealed class DemoServer(ILogger logger) : IDemoServer
{
    public ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken)
    {
        logger.LogInformation("Add({A}, {B}) called by the browser.", a, b);
        return new(a + b);
    }

    public ValueTask<string> GreetAsync(string name, CancellationToken cancellationToken)
    {
        logger.LogInformation("Greet({Name}) called by the browser.", name);
        return new($"Hello, {name}! (from ASP.NET Core {Environment.Version} on the server)");
    }
}
