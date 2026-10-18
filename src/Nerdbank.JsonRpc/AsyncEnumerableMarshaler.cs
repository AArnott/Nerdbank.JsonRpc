// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

/// <summary>Bridges from an <see cref="ITypeShape{T}"/> of <see cref="IAsyncEnumerable{T}"/> to its element type.</summary>
internal static class AsyncEnumerableMarshaler
{
	/// <summary>Assigns a token to a sequence being transmitted.</summary>
	/// <typeparam name="T">The <see cref="IAsyncEnumerable{T}"/> type being marshaled.</typeparam>
	/// <param name="manager">The manager that tracks generators for this connection.</param>
	/// <param name="enumerable">The sequence to transmit.</param>
	/// <param name="shape">The shape of the <see cref="IAsyncEnumerable{T}"/> type.</param>
	/// <param name="encoding">The wire encoding to produce.</param>
	/// <returns>The encoded value to write in place of the sequence.</returns>
	/// <param name="callState">The scopes that apply to the message being written.</param>
	internal static JsonRpcValue Marshal<T>(AsyncEnumerableManager manager, T enumerable, ITypeShape<T> shape, JsonRpcEncoding encoding, RpcCallState? callState)
		=> GetContract(shape).Marshal(manager, enumerable!, encoding, callState);

	/// <summary>Creates a local sequence that pulls its values from a remote generator.</summary>
	/// <typeparam name="T">The <see cref="IAsyncEnumerable{T}"/> type being unmarshaled.</typeparam>
	/// <param name="manager">The manager that tracks generators for this connection.</param>
	/// <param name="value">The encoded value carrying the token and any values that rode along with it.</param>
	/// <param name="shape">The shape of the <see cref="IAsyncEnumerable{T}"/> type.</param>
	/// <returns>The sequence.</returns>
	/// <param name="callState">The scopes that apply to the message being read.</param>
	internal static object Unmarshal<T>(AsyncEnumerableManager manager, JsonRpcValue value, ITypeShape<T> shape, RpcCallState? callState)
		=> GetContract(shape).Unmarshal(manager, value, callState);

	/// <summary>Tests whether a type is exactly <see cref="IAsyncEnumerable{T}"/>.</summary>
	/// <param name="type">The candidate type.</param>
	/// <returns><see langword="true"/> if the type is a constructed <see cref="IAsyncEnumerable{T}"/>.</returns>
	internal static bool IsAsyncEnumerable(Type type)
		=> type.IsInterface && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>);

	/// <summary>Builds a strongly typed contract for the element type of a sequence shape.</summary>
	/// <typeparam name="T">The <see cref="IAsyncEnumerable{T}"/> type.</typeparam>
	/// <param name="shape">The shape of the sequence type.</param>
	/// <returns>The contract.</returns>
	private static EnumerableContract GetContract<T>(ITypeShape<T> shape)
	{
		Type elementType = shape.Type.GetGenericArguments()[0];
		ITypeShape elementShape = shape.Provider.GetTypeShape(elementType)
			?? throw new NotSupportedException($"A PolyType shape for async enumerable element type '{elementType}' is required.");
		return (EnumerableContract)elementShape.Invoke(EnumerableShapeFunc.Instance)!;
	}

	/// <summary>A type-erased view of an element type.</summary>
	private abstract class EnumerableContract
	{
		/// <summary>Assigns a token to a sequence being transmitted.</summary>
		/// <param name="manager">The manager that tracks generators.</param>
		/// <param name="enumerable">The sequence.</param>
		/// <param name="encoding">The wire encoding.</param>
		/// <param name="callState">The scopes that apply to the message being written.</param>
		/// <returns>The encoded value.</returns>
		internal abstract JsonRpcValue Marshal(AsyncEnumerableManager manager, object enumerable, JsonRpcEncoding encoding, RpcCallState? callState);

		/// <summary>Creates a local sequence backed by a remote generator.</summary>
		/// <param name="manager">The manager that tracks generators.</param>
		/// <param name="value">The encoded value.</param>
		/// <param name="callState">The scopes that apply to the message being read.</param>
		/// <returns>The sequence.</returns>
		internal abstract object Unmarshal(AsyncEnumerableManager manager, JsonRpcValue value, RpcCallState? callState);
	}

	/// <summary>A contract bound to a particular element type.</summary>
	/// <typeparam name="TElement">The element type.</typeparam>
	/// <param name="elementShape">The shape of the element type.</param>
	private sealed class Contract<TElement>(ITypeShape<TElement> elementShape) : EnumerableContract
	{
		/// <inheritdoc/>
		internal override JsonRpcValue Marshal(AsyncEnumerableManager manager, object enumerable, JsonRpcEncoding encoding, RpcCallState? callState)
			=> manager.Marshal((IAsyncEnumerable<TElement>)enumerable, elementShape, encoding, callState);

		/// <inheritdoc/>
		internal override object Unmarshal(AsyncEnumerableManager manager, JsonRpcValue value, RpcCallState? callState)
			=> manager.Unmarshal(value, elementShape, callState);
	}

	/// <summary>Re-enters a generic context for the element type.</summary>
	private sealed class EnumerableShapeFunc : ITypeShapeFunc
	{
		/// <summary>The singleton instance.</summary>
		internal static readonly EnumerableShapeFunc Instance = new();

		/// <inheritdoc/>
		public object? Invoke<T>(ITypeShape<T> shape, object? state = null) => new Contract<T>(shape);
	}
}
