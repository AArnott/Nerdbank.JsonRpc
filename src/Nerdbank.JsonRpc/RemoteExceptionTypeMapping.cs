// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections;
using Microsoft;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

/// <summary>Defines explicitly allowed exception types that may be reconstructed from peer diagnostics.</summary>
/// <remarks>This mutable builder is frozen when assigned to <see cref="JsonRpcOptions.AdditionalExceptionTypes"/>.</remarks>
public sealed class RemoteExceptionTypeMapping : IReadOnlyCollection<Type>
{
	private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
	private bool frozen;

	/// <summary>Gets the number of allowed exception types.</summary>
	public int Count => this.entries.Count;

	/// <summary>Gets the immutable empty mapping shared by default options.</summary>
	internal static RemoteExceptionTypeMapping Empty { get; } = new() { frozen = true };

#if NET
	/// <summary>Adds a self-shapeable exception type that may be serialized and reconstructed.</summary>
	/// <typeparam name="TException">The exception type, which provides its own generated shape.</typeparam>
	/// <remarks>
	/// The shape must use a PolyType marshaler to select and sanitize the data that crosses the RPC boundary.
	/// The serializer uses its surrogate shape to construct the exception without a factory delegate.
	/// </remarks>
	public void Add<TException>()
		where TException : Exception, IShapeable<TException>
		=> this.AddResolvedShape(TException.GetTypeShape());

	/// <summary>Adds an exception type using a shape provided by a source-generated witness type.</summary>
	/// <typeparam name="TException">The allowed exception type.</typeparam>
	/// <typeparam name="TProvider">The witness type that provides the shape for <typeparamref name="TException"/>.</typeparam>
	/// <remarks>Use a PolyType marshaler on the shape to exclude or sanitize members that should not cross the RPC boundary.</remarks>
	public void Add<TException, TProvider>()
		where TException : Exception
		where TProvider : IShapeable<TException>
		=> this.AddResolvedShape(TProvider.GetTypeShape());
#endif

	/// <summary>Gets the explicitly allowed exception types.</summary>
	/// <returns>The allowed types.</returns>
	public IEnumerator<Type> GetEnumerator() => this.entries.Values.Select(static entry => entry.Type).GetEnumerator();

	/// <inheritdoc/>
	IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

	/// <summary>Registers an exception type using a resolved source-generated shape.</summary>
	/// <typeparam name="TException">The allowed exception type.</typeparam>
	/// <param name="shape">The source-generated shape that establishes NativeAOT reachability.</param>
	internal void AddResolvedShape<TException>(ITypeShape<TException> shape)
		where TException : Exception
	{
		Requires.NotNull(shape);
		this.ThrowIfFrozen();
		if (shape.Type != typeof(TException))
		{
			throw new ArgumentException("The shape must describe the registered exception type.", nameof(shape));
		}

		if (shape is not ISurrogateTypeShape)
		{
			// Due to https://github.com/eiriktsarpalis/PolyType/issues/349
			throw new ArgumentException("Exception shapes must declare a PolyType marshaler that selects the data to serialize.", nameof(shape));
		}

		string name = shape.Type.FullName ?? throw new ArgumentException("The exception type must have a full name.", nameof(shape));
		this.entries.Add(name, new Entry(
			shape.Type,
			shape,
			(serializer, exception) =>
			{
				TException typedException = (TException)exception;
				return serializer.Serialize(typedException, shape);
			},
			(serializer, value) => serializer.Deserialize(value, shape) ?? throw new FormatException("The remote exception payload was null.")));
	}

	/// <summary>Looks up an explicitly registered exception mapping by CLR full name.</summary>
	/// <param name="name">The assembly-independent full type name.</param>
	/// <param name="entry">Receives the mapping when one exists.</param>
	/// <returns><see langword="true"/> if a mapping exists; otherwise, <see langword="false"/>.</returns>
	internal bool TryGet(string name, out Entry? entry)
	{
		if (this.entries.TryGetValue(name, out entry) && entry.Type.FullName == name)
		{
			return true;
		}

		entry = null;
		return false;
	}

	/// <summary>Looks up an explicitly registered exception mapping by exact CLR type.</summary>
	/// <param name="type">The local exception type.</param>
	/// <param name="entry">Receives the mapping when one exists.</param>
	/// <returns><see langword="true"/> if a mapping exists; otherwise, <see langword="false"/>.</returns>
	internal bool TryGet(Type type, out Entry? entry)
	{
		string? name = type.FullName;
		if (name is not null && this.entries.TryGetValue(name, out entry) && entry.Type == type)
		{
			return true;
		}

		entry = null;
		return false;
	}

	/// <summary>Prevents further changes to this mapping.</summary>
	/// <returns>This now-immutable mapping.</returns>
	internal RemoteExceptionTypeMapping Freeze()
	{
		this.frozen = true;
		return this;
	}

	private void ThrowIfFrozen()
	{
		if (this.frozen)
		{
			throw new InvalidOperationException("This exception mapping has been frozen and may not be changed.");
		}
	}

	/// <summary>Holds one exception allowlist entry and its source-generated shape root.</summary>
	/// <param name="Type">The registered exception type.</param>
	/// <param name="Shape">The source-generated type shape.</param>
	/// <param name="Serialize">Serializes the exception using its registered shape.</param>
	/// <param name="Deserialize">Reconstructs the exception using its registered shape.</param>
	internal sealed record Entry(Type Type, ITypeShape Shape, Func<JsonRpcSerializer, Exception, JsonRpcValue> Serialize, Func<JsonRpcSerializer, JsonRpcValue, Exception> Deserialize);
}
