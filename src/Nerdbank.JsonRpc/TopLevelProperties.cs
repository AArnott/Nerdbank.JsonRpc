// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Net;

namespace Nerdbank.JsonRpc;

/// <summary>
/// An ordered set of primitive top-level properties carried on a JSON-RPC message envelope
/// in addition to the properties defined by the JSON-RPC 2.0 specification.
/// </summary>
/// <remarks>
/// Both the JSON and MessagePack codecs read and write these properties,
/// so new well-known properties (or a future public API) need no codec changes.
/// </remarks>
internal sealed class TopLevelProperties
{
	/// <summary>The top-level property that carries a <c>JoinableTask</c> token, compatible with StreamJsonRpc.</summary>
	internal const string JoinableTaskTokenPropertyName = "joinableTaskToken";

	private readonly List<KeyValuePair<string, TopLevelPropertyValue>> properties = new(1);

	/// <summary>Gets the number of properties.</summary>
	internal int Count => this.properties.Count;

	/// <summary>Gets the properties in insertion order.</summary>
	internal IReadOnlyList<KeyValuePair<string, TopLevelPropertyValue>> Properties => this.properties;

	/// <summary>Gets a value indicating whether a property name is defined by the JSON-RPC envelope itself.</summary>
	/// <param name="name">The property name.</param>
	/// <returns><see langword="true"/> if the name may not be used as an extension property.</returns>
	internal static bool IsReserved(string name) => name is "jsonrpc" or "id" or "method" or "params" or "result" or "error";

	/// <summary>Gets the kind a well-known extension property must have, if any.</summary>
	/// <param name="name">The property name.</param>
	/// <param name="kind">Receives the required kind.</param>
	/// <returns><see langword="true"/> if the property is well-known.</returns>
	internal static bool TryGetRequiredKind(string name, out TopLevelPropertyKind kind)
	{
		switch (name)
		{
			case JoinableTaskTokenPropertyName:
				kind = TopLevelPropertyKind.String;
				return true;
			default:
				kind = default;
				return false;
		}
	}

	/// <summary>Adds or replaces a property value.</summary>
	/// <param name="name">The property name.</param>
	/// <param name="value">The value.</param>
	/// <exception cref="ArgumentException">Thrown if the name is empty, reserved, or the value kind is invalid for a well-known property.</exception>
	internal void Set(string name, TopLevelPropertyValue value)
	{
		ValidateName(name);
		ValidateKind(name, value);
		int index = this.IndexOf(name);
		if (index >= 0)
		{
			this.properties[index] = new(name, value);
		}
		else
		{
			this.properties.Add(new(name, value));
		}
	}

	/// <summary>Adds a property read from the wire.</summary>
	/// <param name="name">The property name.</param>
	/// <param name="value">The value.</param>
	/// <exception cref="ProtocolViolationException">Thrown for duplicate names or well-known properties of the wrong kind.</exception>
	internal void AddReceived(string name, TopLevelPropertyValue value)
	{
		if (TryGetRequiredKind(name, out TopLevelPropertyKind kind) && kind != value.Kind)
		{
			throw new ProtocolViolationException($"The JSON-RPC '{name}' property must be a {kind} value.");
		}

		if (this.IndexOf(name) >= 0)
		{
			throw new ProtocolViolationException($"Duplicate JSON-RPC '{name}' property.");
		}

		this.properties.Add(new(name, value));
	}

	/// <summary>Gets a property value.</summary>
	/// <param name="name">The property name.</param>
	/// <param name="value">Receives the value.</param>
	/// <returns><see langword="true"/> if the property exists.</returns>
	internal bool TryGet(string name, out TopLevelPropertyValue value)
	{
		int index = this.IndexOf(name);
		value = index >= 0 ? this.properties[index].Value : default;
		return index >= 0;
	}

	/// <summary>Removes a property.</summary>
	/// <param name="name">The property name.</param>
	/// <returns><see langword="true"/> if the property existed.</returns>
	internal bool Remove(string name)
	{
		int index = this.IndexOf(name);
		if (index >= 0)
		{
			this.properties.RemoveAt(index);
		}

		return index >= 0;
	}

	private static void ValidateName(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			throw new ArgumentException("A top-level property name must be non-empty.", nameof(name));
		}

		if (IsReserved(name))
		{
			throw new ArgumentException($"'{name}' is a reserved JSON-RPC property name.", nameof(name));
		}
	}

	private static void ValidateKind(string name, TopLevelPropertyValue value)
	{
		if (TryGetRequiredKind(name, out TopLevelPropertyKind kind) && kind != value.Kind)
		{
			throw new ArgumentException($"The '{name}' property must be a {kind} value.", nameof(value));
		}
	}

	private int IndexOf(string name)
	{
		for (int i = 0; i < this.properties.Count; i++)
		{
			if (string.Equals(this.properties[i].Key, name, StringComparison.Ordinal))
			{
				return i;
			}
		}

		return -1;
	}
}
