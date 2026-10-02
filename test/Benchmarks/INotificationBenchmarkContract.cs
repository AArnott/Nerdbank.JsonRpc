// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

namespace Benchmarks;

/// <summary>A generated notification contract used to measure submission overhead.</summary>
[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface INotificationBenchmarkContract
{
	/// <summary>Queues a notification.</summary>
	/// <param name="value">The payload.</param>
	/// <param name="cancellationToken">A token controlling submission.</param>
	void Notify(string value, CancellationToken cancellationToken);
}
