// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

/// <summary>Writes positional or named request parameters directly to the selected serializer's output buffer.</summary>
public ref struct JsonRpcArgumentsBuilder
{
	private readonly JsonRpcSerializer serializer;
	private readonly MarshaledObjectManager.HandleScope marshaledObjectsScope;
	private readonly bool named;
	private readonly int count;
	private readonly CancellationToken cancellationToken;
	private readonly Sequence<byte> buffer;
	private int written;
	private bool built;
	private bool failed;

	/// <summary>Initializes a new instance of the <see cref="JsonRpcArgumentsBuilder"/> struct.</summary>
	/// <param name="named">Whether to encode named parameters.</param>
	/// <param name="count">The exact number of parameters to write.</param>
	/// <param name="cancellationToken">A token used when serializing every parameter.</param>
	/// <param name="context">The RPC object that supplies serialization and encodes disposable values as marshaled handles.</param>
	internal JsonRpcArgumentsBuilder(IArgumentsBuilderContext context, bool named, int count, CancellationToken cancellationToken)
	{
		if (context is null)
		{
			throw new ArgumentNullException(nameof(context));
		}

		this.serializer = context.Serializer;
		this.marshaledObjectsScope = context.MarshaledObjects.TrackMarshaledObjects();
		if (count < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(count));
		}

		this.named = named;
		this.count = count;
		this.cancellationToken = cancellationToken;
		this.buffer = new();
		if (this.serializer.Encoding == JsonRpcEncoding.Json)
		{
			this.WriteByte(named ? (byte)'{' : (byte)'[');
		}
		else
		{
			MessagePackWriter writer = new(this.buffer);
			if (named)
			{
				writer.WriteMapHeader(count);
			}
			else
			{
				writer.WriteArrayHeader(count);
			}

			writer.Flush();
		}
	}

	/// <summary>Serializes a parameter directly into the output buffer.</summary>
	/// <typeparam name="T">The parameter type.</typeparam>
	/// <param name="name">The name for a named parameter; ignored for positional parameters.</param>
	/// <param name="value">The parameter value.</param>
	/// <param name="shape">The parameter type shape.</param>
	public void Add<T>(string? name, in T value, ITypeShape<T> shape)
	{
		this.ThrowIfUnavailable();
		if (this.written == this.count)
		{
			throw new InvalidOperationException("The declared number of parameters has already been written.");
		}

		if (this.named && name is null)
		{
			throw new ArgumentNullException(nameof(name));
		}

		this.failed = true;
		if (this.serializer.Encoding == JsonRpcEncoding.Json && this.written > 0)
		{
			this.WriteByte((byte)',');
		}

		if (this.named)
		{
			this.serializer.WriteArgumentName(this.buffer, name!);
			if (this.serializer.Encoding == JsonRpcEncoding.Json)
			{
				this.WriteByte((byte)':');
			}
		}

		this.serializer.SerializeTo(this.buffer, value, shape, this.cancellationToken);
		this.written++;
		this.failed = false;
	}

	/// <summary>Builds an owned JSON-RPC params value after every declared parameter has been added.</summary>
	/// <returns>An encoded array or object.</returns>
	public JsonRpcValue Build()
	{
		this.ThrowIfUnavailable();
		if (this.written != this.count)
		{
			throw new InvalidOperationException("The declared number of parameters has not been written.");
		}

		if (this.serializer.Encoding == JsonRpcEncoding.Json)
		{
			this.WriteByte(this.named ? (byte)'}' : (byte)']');
		}

		this.built = true;
		return JsonRpcValue.FromOwnedBytes(this.buffer.AsReadOnlySequence.ToArray(), this.serializer.Encoding, this.marshaledObjectsScope.Commit());
	}

	/// <summary>Releases buffers owned by this builder.</summary>
	public void Dispose()
	{
		if (!this.built)
		{
			this.failed = true;
		}

		this.marshaledObjectsScope?.Dispose();
		this.buffer?.Dispose();
	}

	private void ThrowIfUnavailable()
	{
		if (this.buffer is null || this.built || this.failed)
		{
			throw new InvalidOperationException("This argument builder cannot be used again.");
		}
	}

	private void WriteByte(byte value)
	{
		Span<byte> span = this.buffer.GetSpan(1);
		span[0] = value;
		this.buffer.Advance(1);
	}
}
