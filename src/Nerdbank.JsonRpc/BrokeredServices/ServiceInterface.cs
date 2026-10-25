// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType.Abstractions;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Captures one additional service contract and its generated registration support.</summary>
public abstract class ServiceInterface
{
	/// <summary>Initializes a new instance of the <see cref="ServiceInterface"/> class.</summary>
	/// <param name="id">A stable, application-assigned wire capability identifier.</param>
	/// <param name="interfaceType">The additional interface.</param>
	protected ServiceInterface(string id, Type interfaceType)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			throw new ArgumentException("A stable capability identifier is required.", nameof(id));
		}

		this.Id = id;
		this.InterfaceType = interfaceType ?? throw new ArgumentNullException(nameof(interfaceType));
		if (!interfaceType.IsInterface)
		{
			throw new ArgumentException("The contract must be an interface.", nameof(interfaceType));
		}
	}

	/// <summary>Gets the stable wire capability identifier, not a CLR type name.</summary>
	public string Id { get; }

	/// <summary>Gets the additional interface type.</summary>
	public Type InterfaceType { get; }

	/// <summary>Captures a source-generated additional contract.</summary>
	/// <typeparam name="T">The additional interface.</typeparam>
	/// <param name="id">The stable capability identifier.</param>
	/// <param name="shape">The generated contract shape.</param>
	/// <returns>The captured contract.</returns>
	public static ServiceInterface<T> Create<T>(string id, ITypeShape<T> shape)
		where T : class => new(id, shape);

#if NET
	/// <summary>Captures a statically shaped additional contract.</summary>
	/// <typeparam name="T">The additional interface.</typeparam>
	/// <param name="id">The stable capability identifier.</param>
	/// <returns>The captured contract.</returns>
	public static ServiceInterface<T> Create<T>(string id)
		where T : class, IShapeable<T> => Create(id, T.GetTypeShape());

	/// <summary>Captures an additional contract using a source-generated witness.</summary>
	/// <typeparam name="T">The additional interface.</typeparam>
	/// <typeparam name="TProvider">The source-generated shape provider.</typeparam>
	/// <param name="id">The stable capability identifier.</param>
	/// <returns>The typed contract metadata.</returns>
	public static ServiceInterface<T> Create<T, TProvider>(string id)
		where T : class
		where TProvider : IShapeable<T> => Create(id, TProvider.GetTypeShape());
#endif

	/// <summary>Constructs a generated proxy for this captured contract.</summary>
	/// <param name="rpc">The unstarted connection.</param>
	/// <param name="options">Proxy naming and argument conventions, or the defaults.</param>
	/// <returns>The generated proxy. The connection remains caller-owned.</returns>
	public object CreateProxy(JsonRpc rpc, JsonRpcProxyOptions? options = null) => this.CreateProxyCore(rpc, options);

	/// <summary>Constructs a generated direct proxy for this captured contract.</summary>
	/// <param name="context">The target and callback lifetime.</param>
	/// <returns>The generated direct proxy.</returns>
	public object CreateLocalProxy(ServiceProxyContext context) => this.CreateLocalProxyCore(context);

	/// <summary>Copies capability metadata while validating its identifiers and CLR types.</summary>
	/// <param name="interfaces">The declared capabilities.</param>
	/// <param name="parameterName">The source argument name used in validation errors.</param>
	/// <returns>An immutable snapshot.</returns>
	internal static IReadOnlyList<ServiceInterface> CopyAndValidate(IReadOnlyList<ServiceInterface> interfaces, string parameterName)
	{
		ServiceInterface[] snapshot = (interfaces ?? throw new ArgumentNullException(parameterName)).ToArray();
		if (snapshot.Any(static item => item is null)
			|| snapshot.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Length
			|| snapshot.Select(static item => item.InterfaceType).Distinct().Count() != snapshot.Length)
		{
			throw new ArgumentException("Additional contracts must be non-null with unique types and identifiers.", parameterName);
		}

		return Array.AsReadOnly(snapshot);
	}

	/// <summary>Registers the captured contract after its type has been erased.</summary>
	/// <param name="rpc">The connection.</param>
	/// <param name="target">The implementation.</param>
	/// <param name="options">Target registration options.</param>
	internal void AddRpcTarget(JsonRpc rpc, object target, JsonRpcTargetOptions options) => this.AddRpcTargetCore(rpc, target, options);

	/// <summary>Constructs a proxy after the captured contract type has been erased.</summary>
	/// <param name="rpc">The unstarted connection.</param>
	/// <param name="options">Proxy naming and argument conventions.</param>
	/// <returns>The generated proxy.</returns>
	protected abstract object CreateProxyCore(JsonRpc rpc, JsonRpcProxyOptions? options);

	/// <summary>Constructs a direct proxy after the captured contract type has been erased.</summary>
	/// <param name="context">The target and callback lifetime.</param>
	/// <returns>The generated proxy.</returns>
	protected abstract object CreateLocalProxyCore(ServiceProxyContext context);

	/// <summary>Registers the captured contract after its type has been erased.</summary>
	/// <param name="rpc">The connection.</param>
	/// <param name="target">The implementation.</param>
	/// <param name="options">Target registration options.</param>
	protected abstract void AddRpcTargetCore(JsonRpc rpc, object target, JsonRpcTargetOptions options);
}
