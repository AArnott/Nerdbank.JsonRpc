// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices.JavaScript;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nerdbank.JsonRpc;
using PolyType;

namespace NetWasmDemo;

/// <summary>The browser callback that receives WebSocket message payloads.</summary>
/// <param name="value">The bytes of one WebSocket message.</param>
public delegate void BytesCallback(byte[]? value);

/// <summary>Functions implemented in JavaScript by index.html (module "app.host").</summary>
public static class Host
{
    /// <summary>Appends a line to the page's log.</summary>
    [JSImport("log", "app.host")]
    public static extern void Log(string? value);

    /// <summary>Shows a result on the page, formatted as "id=text".</summary>
    [JSImport("show", "app.host")]
    public static extern void Show(string? value);

    /// <summary>Opens the WebSocket to the given path. The task completes when the socket is open.</summary>
    [JSImport("connect", "app.host")]
    public static extern Task<int> Connect(string? path);

    /// <summary>Sends one binary WebSocket message.</summary>
    [JSImport("send", "app.host")]
    public static extern void Send(byte[]? value);

    /// <summary>Registers the callback that receives WebSocket messages.</summary>
    [JSImport("onMessage", "app.host")]
    public static extern JSSubscription OnMessage(BytesCallback callback);
}

/// <summary>Receives the server's notifications.</summary>
public class ClientCallbacks : IDemoClient
{
    /// <inheritdoc/>
    public void Tick(int count, string message)
    {
        Host.Show("tick=#" + count + ": " + message);
        if (count <= 3)
        {
            Host.Log("notification tick #" + count + ": " + message);
        }
    }
}

/// <summary>The PolyType witness for the shapes this client needs. NetWasm has no reflection-based shape resolution.</summary>
[GenerateShapeFor<IDemoServer>]
[GenerateShapeFor<ClientCallbacks>(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial class Witness;

/// <summary>The entry point.</summary>
public static class Program
{
    internal static string Describe(Exception ex)
    {
        string? stack;
        try
        {
            stack = ex.StackTrace;
        }
        catch (Exception)
        {
            stack = "<StackTrace unavailable>";
        }

        // Not ex.GetType().Name: NetWasm has no runtime type names.
        string result = ex.Message + "\n" + stack;
        if (ex.InnerException is { } inner)
        {
            result += "\n ---> " + Describe(inner);
        }

        return result;
    }

    private static async Task WatchCompletionAsync(JsonRpc rpc)
    {
        try
        {
            await rpc.Completion;
            Host.Log("wasm: rpc completed");
        }
        catch (Exception ex)
        {
            Host.Log("wasm: rpc faulted: " + Describe(ex));
        }
    }

    /// <summary>Connects to the server and calls it.</summary>
    /// <returns>A task that never completes, so the module stays alive to receive notifications.</returns>
    public static async Task Main()
    {
        Host.Log("wasm: Main started (Nerdbank.JsonRpc compiled to WebAssembly by NetWasm)");
        try
        {
            WebSocketPipe pipe = new();
            await Host.Connect("/rpc");
            Host.Log("wasm: WebSocket connected");

            JsonRpcJsonChannel channel = new(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, PageLogger.Instance);
            JsonRpc rpc = new(channel) { Logger = PageLogger.Instance };
            rpc.AddRpcTarget(new ClientCallbacks(), Witness.GeneratedTypeShapeProvider.GetTypeShapeOrThrow<ClientCallbacks>());
            rpc.Start();
            Host.Log("wasm: JsonRpc started (JSON, newline-delimited)");
            _ = WatchCompletionAsync(rpc);

            // JsonRpc.Attach<T>() needs reflection, so construct the generated proxy directly with an explicit shape provider.
            IDemoServer server = new DemoServerProxy(rpc, JsonRpcProxyOptions.Default, Witness.GeneratedTypeShapeProvider);

            int sum = await server.AddAsync(2, 3, CancellationToken.None);
            Host.Log("wasm: Add(2, 3) = " + sum);
            Host.Show("add=" + sum);

            string greeting = await server.GreetAsync("NetWasm", CancellationToken.None);
            Host.Log("wasm: Greet(\"NetWasm\") = " + greeting);
            Host.Show("greet=" + greeting);

            Host.Show("status=connected; waiting for server notifications");
            await rpc.Completion;
            Host.Log("wasm: JSON-RPC connection completed");
        }
        catch (Exception ex)
        {
            Host.Log("wasm: EXCEPTION " + Describe(ex));
            Host.Show("status=error: " + ex.Message);
        }

        // Keep the module alive.
        await new TaskCompletionSource<bool>().Task;
    }
}

/// <summary>Forwards JsonRpc diagnostics (warnings and errors) to the page log.</summary>
internal sealed class PageLogger : ILogger
{
    internal static readonly PageLogger Instance = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (this.IsEnabled(logLevel))
        {
            Host.Log("wasm log [" + logLevel + "]: " + formatter(state, exception) + (exception is null ? string.Empty : " | " + Program.Describe(exception)));
        }
    }
}

/// <summary>Bridges browser WebSocket messages to an <see cref="IDuplexPipe"/>.</summary>
internal sealed class WebSocketPipe : IDuplexPipe
{
    private static readonly PipeOptions Options = new(readerScheduler: PipeScheduler.Inline, writerScheduler: PipeScheduler.Inline, useSynchronizationContext: false);
    private readonly Pipe inbound = new(Options);
    private readonly Pipe outbound = new(Options);

    internal WebSocketPipe()
    {
        Host.OnMessage(this.OnMessage);
        _ = this.PumpOutboundAsync();
    }

    public PipeReader Input => this.inbound.Reader;

    public PipeWriter Output => this.outbound.Writer;

    private void OnMessage(byte[]? bytes)
    {
        if (bytes is { Length: > 0 })
        {
            this.inbound.Writer.Write(bytes);
            _ = this.inbound.Writer.FlushAsync();
        }
    }

    private async Task PumpOutboundAsync()
    {
        try
        {
            while (true)
            {
                ReadResult result = await this.outbound.Reader.ReadAsync();
                ReadOnlySequence<byte> buffer = result.Buffer;
                if (!buffer.IsEmpty)
                {
                    Host.Send(buffer.ToArray());
                }

                this.outbound.Reader.AdvanceTo(buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Host.Log("wasm: outbound pump failed: " + ex.Message);
        }
    }
}
