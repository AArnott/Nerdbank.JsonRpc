// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Benchmarks;

/// <summary>The server-side implementation of <see cref="IRpcBenchmarkContract"/> shared by both libraries under test.</summary>
public sealed class RpcBenchmarkServer : IRpcBenchmarkContract
{
	/// <inheritdoc/>
	public Task<int> PingAsync(CancellationToken cancellationToken) => Task.FromResult(1);

	/// <inheritdoc/>
	public ValueTask<int> PingValueTaskAsync(CancellationToken cancellationToken) => new(1);

	/// <inheritdoc/>
	public ValueTask VoidValueTaskAsync(CancellationToken cancellationToken) => default;

	/// <inheritdoc/>
	public Task VoidTaskAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <inheritdoc/>
	public Task<int> AddAsync(int a, int b, string label, CancellationToken cancellationToken) => Task.FromResult(a + b + label.Length);

	/// <inheritdoc/>
	public Task<long> ProcessGraphAsync(WorkspaceGraph graph, CancellationToken cancellationToken) => Task.FromResult(WorkspaceGraphFactory.Checksum(graph));
}
