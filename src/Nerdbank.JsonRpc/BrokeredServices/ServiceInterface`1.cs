// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType.Abstractions;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Captures an optional or callback contract without erasing its interface type.</summary>
/// <typeparam name="T">The captured contract.</typeparam>
public sealed class ServiceInterface<T> : ServiceInterface
	where T : class
{
	private readonly ITypeShape<T> shape;

	/// <summary>Initializes a new instance of the <see cref="ServiceInterface{T}"/> class.</summary>
	/// <param name="id">The stable wire capability identifier.</param>
	/// <param name="shape">The source-generated contract shape.</param>
	internal ServiceInterface(string id, ITypeShape<T> shape)
		: base(id, typeof(T))
	{
		this.shape = shape ?? throw new ArgumentNullException(nameof(shape));
	}

	/// <summary>Registers the typed contract implementation.</summary>
	/// <param name="rpc">The connection.</param>
	/// <param name="target">The contract implementation.</param>
	/// <param name="options">Target registration options.</param>
	public void AddRpcTarget(JsonRpc rpc, T target, JsonRpcTargetOptions options)
		=> (rpc ?? throw new ArgumentNullException(nameof(rpc))).AddRpcTarget(target, this.shape, options);

	/// <summary>Constructs a typed proxy for this captured contract.</summary>
	/// <param name="rpc">The unstarted connection.</param>
	/// <param name="options">Proxy naming and argument conventions.</param>
	/// <returns>The generated proxy. The connection remains caller-owned.</returns>
	public new T CreateProxy(JsonRpc rpc, JsonRpcProxyOptions? options = null)
		=> (rpc ?? throw new ArgumentNullException(nameof(rpc))).Attach<T>(options);

	/// <summary>Constructs a typed direct proxy for this captured contract.</summary>
	/// <param name="context">The target and callback lifetime.</param>
	/// <returns>The generated proxy.</returns>
	public new T CreateLocalProxy(ServiceProxyContext context) => ServiceProxyFactoryAttribute.Create<T>(context, []);

	/// <inheritdoc/>
	protected override object CreateProxyCore(JsonRpc rpc, JsonRpcProxyOptions? options) => this.CreateProxy(rpc, options);

	/// <inheritdoc/>
	protected override object CreateLocalProxyCore(ServiceProxyContext context) => this.CreateLocalProxy(context);

	/// <inheritdoc/>
	protected override void AddRpcTargetCore(JsonRpc rpc, object target, JsonRpcTargetOptions options)
		=> this.AddRpcTarget(rpc, (T)target, options);
}
