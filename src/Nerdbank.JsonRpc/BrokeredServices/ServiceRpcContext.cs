// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Provides live infrastructure for one service RPC connection, independently of a descriptor record.</summary>
public sealed record ServiceRpcContext
{
	/// <summary>Gets the connection logger.</summary>
	public ILogger<JsonRpc> Logger { get; init; } = NullLogger<JsonRpc>.Instance;

	/// <summary>Gets the factory for broker client diagnostics associated with this connection.</summary>
	public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;

	/// <summary>Gets the factory used for joinable-task propagation.</summary>
	public JoinableTaskFactory? JoinableTaskFactory { get; init; }

	/// <summary>Gets the incoming request synchronization context.</summary>
	public SynchronizationContext? SynchronizationContext { get; init; } = new NonConcurrentSynchronizationContext(sticky: false);
}
