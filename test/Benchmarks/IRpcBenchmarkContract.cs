// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

namespace Benchmarks;

/// <summary>
/// An RPC contract shared by the Nerdbank.JsonRpc and StreamJsonRpc benchmarks, covering a no-argument method,
/// a method with a few small arguments, and a method that accepts a large object graph.
/// </summary>
[GenerateJsonRpcProxy]
[StreamJsonRpc.JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IRpcBenchmarkContract
{
	/// <summary>Invokes a method that takes no arguments and returns a fixed value.</summary>
	/// <param name="cancellationToken">A token to cancel the request.</param>
	/// <returns>A fixed value.</returns>
	Task<int> PingAsync(CancellationToken cancellationToken);

	/// <summary>Invokes a method with a few small arguments.</summary>
	/// <param name="a">The first addend.</param>
	/// <param name="b">The second addend.</param>
	/// <param name="label">A short label whose length contributes to the result.</param>
	/// <param name="cancellationToken">A token to cancel the request.</param>
	/// <returns>The sum of <paramref name="a"/>, <paramref name="b"/>, and the length of <paramref name="label"/>.</returns>
	Task<int> AddAsync(int a, int b, string label, CancellationToken cancellationToken);

	/// <summary>Invokes a method that accepts a large, nested object graph.</summary>
	/// <param name="graph">The workspace graph to process.</param>
	/// <param name="cancellationToken">A token to cancel the request.</param>
	/// <returns>The checksum of the received graph, per <see cref="WorkspaceGraphFactory.Checksum(WorkspaceGraph)"/>.</returns>
	Task<long> ProcessGraphAsync(WorkspaceGraph graph, CancellationToken cancellationToken);
}
