// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>A primitive value stored in a top-level JSON-RPC envelope extension property.</summary>
internal readonly struct TopLevelPropertyValue : IEquatable<TopLevelPropertyValue>
{
	private readonly string? stringValue;
	private readonly long int64Value;

	private TopLevelPropertyValue(string value)
	{
		this.Kind = TopLevelPropertyKind.String;
		this.stringValue = value;
		this.int64Value = 0;
	}

	private TopLevelPropertyValue(long value)
	{
		this.Kind = TopLevelPropertyKind.Int64;
		this.stringValue = null;
		this.int64Value = value;
	}

	/// <summary>Gets the kind of primitive stored.</summary>
	internal TopLevelPropertyKind Kind { get; }

	/// <summary>Gets the string value, if <see cref="Kind"/> is <see cref="TopLevelPropertyKind.String"/>.</summary>
	internal string? StringValue => this.Kind == TopLevelPropertyKind.String ? this.stringValue : null;

	/// <summary>Gets the integer value, if <see cref="Kind"/> is <see cref="TopLevelPropertyKind.Int64"/>.</summary>
	internal long? Int64Value => this.Kind == TopLevelPropertyKind.Int64 ? this.int64Value : null;

	/// <summary>Creates a string value.</summary>
	/// <param name="value">The non-null string.</param>
	public static implicit operator TopLevelPropertyValue(string value) => new(value ?? throw new ArgumentNullException(nameof(value)));

	/// <summary>Creates an integer value.</summary>
	/// <param name="value">The integer.</param>
	public static implicit operator TopLevelPropertyValue(long value) => new(value);

	/// <inheritdoc/>
	public bool Equals(TopLevelPropertyValue other) => this.Kind == other.Kind && this.int64Value == other.int64Value && string.Equals(this.stringValue, other.stringValue, StringComparison.Ordinal);

	/// <inheritdoc/>
	public override bool Equals(object? obj) => obj is TopLevelPropertyValue other && this.Equals(other);

	/// <inheritdoc/>
	public override int GetHashCode() => this.Kind == TopLevelPropertyKind.String ? StringComparer.Ordinal.GetHashCode(this.stringValue!) : this.int64Value.GetHashCode();

	/// <inheritdoc/>
	public override string ToString() => this.Kind == TopLevelPropertyKind.String ? this.stringValue! : this.int64Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
