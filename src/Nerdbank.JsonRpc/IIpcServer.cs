// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Listens for same-account local IPC connections.</summary>
public interface IIpcServer : IDisposable, IAsyncDisposable
{
	/// <summary>Gets the endpoint name to pass to <see cref="IpcClient.ConnectAsync"/>.</summary>
	string Name { get; }

	/// <summary>Gets a task that completes when listening and all accepted callbacks finish.</summary>
	Task Completion { get; }
}
