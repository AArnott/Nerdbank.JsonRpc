// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

internal sealed class MarshaledObjectProxyClient(JsonRpc owner, long handle) : IJsonRpcClient
{
	private int disposed;

	public JsonRpcSerializer Serializer => owner.UserDataSerializer;

	public JsonRpcArgumentsBuilder CreateArguments(bool named, int count, CancellationToken cancellationToken = default)
	{
		this.ThrowIfDisposed();
		return owner.CreateArguments(named, count, cancellationToken);
	}

	public ValueTask RequestAsync(string method, JsonRpcValue arguments, CancellationToken cancellationToken)
	{
		this.ThrowIfDisposed();
		return owner.RequestAsync(this.GetMethodName(method), arguments, cancellationToken);
	}

	public ValueTask<TResult> RequestAsync<TResult>(string method, JsonRpcValue arguments, ITypeShape<TResult> resultShape, CancellationToken cancellationToken)
	{
		this.ThrowIfDisposed();
		return owner.RequestAsync(this.GetMethodName(method), arguments, resultShape, cancellationToken);
	}

	public ValueTask NotifyAsync(string method, JsonRpcValue arguments, CancellationToken cancellationToken)
	{
		if (method == "dispose")
		{
			if (Interlocked.Exchange(ref this.disposed, 1) == 0)
			{
				owner.MarshaledObjects.Release(handle);
			}

			return default;
		}

		this.ThrowIfDisposed();
		return owner.NotifyAsync(this.GetMethodName(method), arguments, cancellationToken);
	}

	private string GetMethodName(string method) => $"$/invokeProxy/{handle}/{method}";

	private void ThrowIfDisposed()
	{
		if (Volatile.Read(ref this.disposed) != 0)
		{
			throw new ObjectDisposedException("marshaled proxy");
		}
	}
}
