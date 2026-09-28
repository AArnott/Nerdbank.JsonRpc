// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>
/// Tunes how an <see cref="IAsyncEnumerable{T}"/> is transmitted across a JSON-RPC connection.
/// </summary>
/// <remarks>
/// <para>
/// The default values reproduce the behavior of a local <see cref="IAsyncEnumerable{T}"/>:
/// exactly one value is produced and transmitted at a time, and only when the consumer asks for it.
/// </para>
/// <para>
/// These settings are only honored by the <em>generator</em> (the party that sends the enumerable).
/// They are ignored when applied by the consumer. Apply them with
/// <see cref="JsonRpcEnumerableExtensions.WithJsonRpcSettings{T}(IAsyncEnumerable{T}, JsonRpcEnumerableSettings)"/>.
/// </para>
/// </remarks>
public record class JsonRpcEnumerableSettings
{
	private readonly int maxReadAhead;
	private readonly int minBatchSize = 1;
	private readonly int prefetch;

	/// <summary>Gets the default settings, which transmit one value per round-trip with no read ahead or prefetch.</summary>
	public static JsonRpcEnumerableSettings Default { get; } = new();

	/// <summary>
	/// Gets the maximum number of values to produce and cache from the generator in anticipation of the
	/// consumer asking for them.
	/// </summary>
	/// <value>Defaults to 0, meaning the generator produces values only while responding to a consumer request.</value>
	/// <remarks>
	/// Raising this improves throughput when producing each value is slow, because the generator can be working
	/// on later values while earlier ones are in transit or being processed by the consumer.
	/// </remarks>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when set to a negative value.</exception>
	public int MaxReadAhead
	{
		get => this.maxReadAhead;
		init => this.maxReadAhead = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A non-negative value is required.");
	}

	/// <summary>
	/// Gets the minimum number of values the generator collects before responding to a consumer's request for more values.
	/// </summary>
	/// <value>Defaults to 1.</value>
	/// <remarks>
	/// Raising this reduces the number of round-trips the consumer makes while enumerating, which improves
	/// performance when network latency is significant. The generator always sends fewer than this number of
	/// values when the sequence ends first.
	/// </remarks>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when set to a value less than 1.</exception>
	public int MinBatchSize
	{
		get => this.minBatchSize;
		init => this.minBatchSize = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A positive value is required.");
	}

	/// <summary>
	/// Gets the number of values to produce up front and include in the message that carries the enumerable itself,
	/// sparing the consumer a round-trip for the first values.
	/// </summary>
	/// <value>Defaults to 0.</value>
	/// <remarks>
	/// <para>This is only applied to an <see cref="IAsyncEnumerable{T}"/> returned directly from an RPC method.</para>
	/// <para>
	/// To prefetch values for an enumerable used as an RPC method <em>argument</em>, or nested within an object graph,
	/// use <see cref="JsonRpcEnumerableExtensions.WithPrefetchAsync{T}(IAsyncEnumerable{T}, int, CancellationToken)"/>
	/// instead and leave this at 0.
	/// </para>
	/// </remarks>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when set to a negative value.</exception>
	public int Prefetch
	{
		get => this.prefetch;
		init => this.prefetch = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A non-negative value is required.");
	}
}
