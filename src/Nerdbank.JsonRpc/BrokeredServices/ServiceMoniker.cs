// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Identifies a service contract independently of its location or implementation.</summary>
[GenerateShape]
public sealed partial record ServiceMoniker
{
	/// <summary>Initializes a new instance of the <see cref="ServiceMoniker"/> class.</summary>
	/// <param name="name">The nonempty, ordinal case-sensitive service name.</param>
	/// <param name="version">The contract version, or null for an unversioned contract.</param>
	public ServiceMoniker(string name, Version? version = null)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new ArgumentException("A service name is required.", nameof(name));
		}

		this.Name = name;
		this.Version = version;
	}

	/// <summary>Gets the service name.</summary>
	public string Name { get; }

	/// <summary>Gets the contract version.</summary>
	public Version? Version { get; }

	/// <inheritdoc/>
	public override string ToString() => this.Version is null ? this.Name : $"{this.Name} ({this.Version})";
}
