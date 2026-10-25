// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;

namespace Nerdbank.JsonRpc;

/// <summary>A same-account IPC listener with explicitly observed connection callbacks.</summary>
public sealed class IpcServer : IIpcServer
{
	private readonly CancellationTokenSource shutdown = new();
	private readonly object syncObject = new();
	private readonly HashSet<Stream> clients = [];
	private readonly Func<Stream, CancellationToken, Task> callback;
	private readonly IpcServerOptions options;
	private readonly ILogger<IpcServer> logger;
	private readonly string pipeName;
	private NamedPipeServerStream? listener;
	private int disposed;

	/// <summary>Initializes a new instance of the <see cref="IpcServer"/> class and starts listening.</summary>
	/// <param name="callback">The connection callback, which owns the stream until it completes.</param>
	/// <param name="options">The listening options.</param>
	internal IpcServer(Func<Stream, CancellationToken, Task> callback, IpcServerOptions options)
	{
		this.callback = callback;
		this.options = options;
		this.logger = options.Logger ?? NullLogger<IpcServer>.Instance;
		this.pipeName = IpcUtilities.ValidateName(options.Name ?? Guid.NewGuid().ToString("N"));
		this.Name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"\\.\pipe\" + this.pipeName
			: Path.IsPathRooted(this.pipeName) ? this.pipeName : Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + this.pipeName);
		this.listener = IpcUtilities.CreatePipe(this.pipeName);
		this.Completion = this.ListenAsync();
	}

	/// <inheritdoc/>
	public string Name { get; }

	/// <inheritdoc/>
	public Task Completion { get; }

	/// <summary>Starts listening before returning the endpoint.</summary>
	/// <param name="onConnected">A callback that owns the stream until it completes. The stream is then disposed.</param>
	/// <param name="options">Listening options.</param>
	/// <returns>The listening server.</returns>
	public static IIpcServer Create(Func<Stream, CancellationToken, Task> onConnected, IpcServerOptions options = default)
		=> new IpcServer(onConnected ?? throw new ArgumentNullException(nameof(onConnected)), options);

	/// <inheritdoc/>
	public void Dispose()
	{
		lock (this.syncObject)
		{
			if (this.disposed != 0)
			{
				return;
			}

			this.disposed = 1;
			try
			{
				this.shutdown.Cancel();
			}
			finally
			{
				this.listener?.Dispose();
				foreach (Stream client in this.clients)
				{
					client.Dispose();
				}
			}
		}
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync()
	{
		this.Dispose();
#pragma warning disable VSTHRD003 // Listener completion is asynchronous and never requires a synchronously blocked caller.
		await this.Completion.ConfigureAwait(false);
#pragma warning restore VSTHRD003
	}

	private async Task ListenAsync()
	{
		List<Task> callbacks = [];
		try
		{
			while (!this.shutdown.IsCancellationRequested)
			{
				NamedPipeServerStream accepting = this.listener!;
				await accepting.WaitForConnectionAsync(this.shutdown.Token).ConfigureAwait(false);
				lock (this.syncObject)
				{
					if (this.shutdown.IsCancellationRequested)
					{
						// Closing the pending native handle is non-blocking and must occur while ownership is locked.
#pragma warning disable VSTHRD103
						accepting.Dispose();
#pragma warning restore VSTHRD103
						break;
					}

					this.clients.Add(accepting);
					this.listener = this.options.AllowMultipleClients ? IpcUtilities.CreatePipe(this.pipeName) : null;
				}

				callbacks.RemoveAll(static task => task.Status == TaskStatus.RanToCompletion);
				callbacks.Add(this.HandleAsync(accepting));
				if (!this.options.AllowMultipleClients)
				{
					break;
				}
			}
		}
		catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
		{
		}
		catch (ObjectDisposedException) when (this.shutdown.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			this.logger.LogError(ex, "IPC listener {Endpoint} failed.", this.Name);
#pragma warning disable VSTHRD103 // Only cancel and close handles here; awaiting listener completion would await this operation itself.
			this.Dispose();
#pragma warning restore VSTHRD103
			throw;
		}
		finally
		{
			try
			{
				this.listener?.Dispose();
				await Task.WhenAll(callbacks).ConfigureAwait(false);
			}
			finally
			{
				lock (this.syncObject)
				{
					this.disposed = 1;
					this.shutdown.Dispose();
				}
			}
		}
	}

	private async Task HandleAsync(Stream stream)
	{
		try
		{
			await this.callback(stream, this.shutdown.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
		{
		}
		catch (IOException) when (this.shutdown.IsCancellationRequested)
		{
		}
		catch (ObjectDisposedException) when (this.shutdown.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			this.logger.LogError(ex, "An IPC connection callback for {Endpoint} failed.", this.Name);
#pragma warning disable VSTHRD103 // Abort the listener without awaiting the currently executing callback.
			this.Dispose();
#pragma warning restore VSTHRD103
			throw;
		}
		finally
		{
			lock (this.syncObject)
			{
				this.clients.Remove(stream);
			}

#if NET
			await stream.DisposeAsync().ConfigureAwait(false);
#else
#pragma warning disable VSTHRD103 // Legacy Stream has no asynchronous disposal.
			stream.Dispose();
#pragma warning restore VSTHRD103
#endif
		}
	}
}
