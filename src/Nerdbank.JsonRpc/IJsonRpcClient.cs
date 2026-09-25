// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Provides the client operations used by generated JSON-RPC proxy implementations.
/// </summary>
/// <remarks>
/// This interface is public so source-generated code in consuming assemblies can reference it.
/// Most application code should use <see cref="JsonRpc"/> or <see cref="JsonRpcBatch"/> directly.
/// External implementations are not supported. Members may be added to this interface in future releases.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public interface IJsonRpcClient
{
	/// <summary>
	/// Gets the serializer used to encode arguments and decode results.
	/// </summary>
	JsonRpcSerializer Serializer { get; }

	/// <summary>Creates a serializer-neutral builder for named or positional arguments.</summary>
	/// <param name="named">Whether to use named arguments.</param>
	/// <param name="count">The exact number of arguments to write.</param>
	/// <param name="cancellationToken">A token used when serializing all arguments.</param>
	/// <returns>An argument builder using this client's serializer.</returns>
	JsonRpcArgumentsBuilder CreateArguments(bool named, int count, CancellationToken cancellationToken = default);

	/// <summary>
	/// Sends a request with arguments already serialized using the channel's selected encoding.
	/// </summary>
	/// <param name="method">The name of the remote method to invoke.</param>
	/// <param name="arguments">The pre-serialized arguments payload.</param>
	/// <param name="cancellationToken">A token whose cancellation should be propagated to the remote endpoint.</param>
	/// <returns>A task that completes when the remote endpoint sends its response.</returns>
	ValueTask RequestAsync(string method, JsonRpcValue arguments, CancellationToken cancellationToken);

	/// <summary>
	/// Sends a request with arguments already serialized using the channel's selected encoding.
	/// </summary>
	/// <typeparam name="TResult">The expected result type.</typeparam>
	/// <param name="method">The name of the remote method to invoke.</param>
	/// <param name="arguments">The pre-serialized arguments payload.</param>
	/// <param name="resultShape">The type shape describing <typeparamref name="TResult"/>.</param>
	/// <param name="cancellationToken">A token whose cancellation should be propagated to the remote endpoint.</param>
	/// <returns>A task that completes with the result returned by the remote endpoint.</returns>
	ValueTask<TResult> RequestAsync<TResult>(string method, JsonRpcValue arguments, ITypeShape<TResult> resultShape, CancellationToken cancellationToken);

	/// <summary>
	/// Sends a notification with arguments already serialized using the channel's selected encoding.
	/// </summary>
	/// <param name="method">The name of the remote method to invoke.</param>
	/// <param name="arguments">The pre-serialized arguments payload.</param>
	/// <param name="cancellationToken">A token whose cancellation is observed before the notification is posted.</param>
	/// <returns>A task that completes when the notification has been accepted by the outbound channel.</returns>
	ValueTask NotifyAsync(string method, JsonRpcValue arguments, CancellationToken cancellationToken);

	/// <summary>Marshals a disposable object into an encoded RPC value.</summary>
	/// <param name="value">The object to marshal.</param>
	/// <returns>An encoded marshaled-object marker.</returns>
	JsonRpcValue MarshalDisposable(IDisposable value);

	/// <summary>Sends a request whose result is a marshaled disposable object.</summary>
	/// <param name="method">The remote method name.</param>
	/// <param name="arguments">The encoded arguments.</param>
	/// <param name="cancellationToken">A cancellation token.</param>
	/// <returns>The remote disposable proxy.</returns>
	ValueTask<IDisposable> RequestDisposableAsync(string method, JsonRpcValue arguments, CancellationToken cancellationToken);
}
