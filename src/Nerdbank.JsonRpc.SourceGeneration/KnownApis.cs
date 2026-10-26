// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.SourceGeneration;

/// <summary>Metadata and member names used by contract analysis and proxy generation.</summary>
internal static class KnownApis
{
	/// <summary>The broker interface metadata name.</summary>
	internal const string IServiceBroker = "Nerdbank.JsonRpc.BrokeredServices.IServiceBroker";

	/// <summary>The cached broker client metadata name.</summary>
	internal const string ServiceBrokerClient = "Nerdbank.JsonRpc.BrokeredServices.ServiceBrokerClient";

	/// <summary>The rental metadata name within the cached broker client.</summary>
	internal const string Rental = "Rental`1";

	/// <summary>The proxy acquisition method name.</summary>
	internal const string GetProxyAsync = "GetProxyAsync";

	/// <summary>The asynchronous disposable interface metadata name.</summary>
	internal const string IAsyncDisposable = "System.IAsyncDisposable";

	/// <summary>The asynchronous disposal method name.</summary>
	internal const string DisposeAsync = "DisposeAsync";

	/// <summary>The proxy interface group attribute metadata name.</summary>
	internal const string JsonRpcProxyInterfaceGroupAttribute = "Nerdbank.JsonRpc.JsonRpcProxyInterfaceGroupAttribute";

	/// <summary>The JSON-RPC client metadata name.</summary>
	internal const string JsonRpc = "Nerdbank.JsonRpc.JsonRpc";

	/// <summary>The JSON-RPC batch client metadata name.</summary>
	internal const string JsonRpcBatch = "Nerdbank.JsonRpc.JsonRpcBatch";

	/// <summary>The proxy attachment method name.</summary>
	internal const string Attach = "Attach";

	/// <summary>The RPC contract attribute metadata name.</summary>
	internal const string JsonRpcContractAttribute = "Nerdbank.JsonRpc.JsonRpcContractAttribute";

	/// <summary>The proxy generation switch member name.</summary>
	internal const string GenerateProxy = "GenerateProxy";

	/// <summary>The marshalable attribute metadata name.</summary>
	internal const string RpcMarshalableAttribute = "Nerdbank.JsonRpc.RpcMarshalableAttribute";

	/// <summary>The optional marshalable interface attribute metadata name.</summary>
	internal const string RpcMarshalableOptionalInterfaceAttribute = "Nerdbank.JsonRpc.RpcMarshalableOptionalInterfaceAttribute";

	/// <summary>The shape generation attribute metadata name.</summary>
	internal const string GenerateShapeAttribute = "PolyType.GenerateShapeAttribute";

	/// <summary>The shape provider attribute simple name.</summary>
	internal const string TypeShapeProviderAttributeName = "TypeShapeProviderAttribute";

	/// <summary>The method shape attribute metadata name.</summary>
	internal const string MethodShapeAttribute = "PolyType.MethodShapeAttribute";

	/// <summary>The event shape attribute metadata name.</summary>
	internal const string EventShapeAttribute = "PolyType.EventShapeAttribute";

	/// <summary>The type shape resolver metadata name.</summary>
	internal const string TypeShapeResolver = "PolyType.Abstractions.TypeShapeResolver";

	/// <summary>The call-scoped lifetime member name.</summary>
	internal const string CallScopedLifetime = "CallScopedLifetime";

	/// <summary>The explicit shape name member name.</summary>
	internal const string MethodShapeName = "Name";

	/// <summary>The method shape exclusion member name.</summary>
	internal const string MethodShapeIgnore = "Ignore";

	/// <summary>The static shape resolution method name.</summary>
	internal const string TypeShapeResolve = "Resolve";

	/// <summary>The dynamic shape resolution method name.</summary>
	internal const string TypeShapeResolveDynamicOrThrow = "ResolveDynamicOrThrow";

	/// <summary>The disposable interface metadata name.</summary>
	internal const string IDisposable = "System.IDisposable";

	/// <summary>The PolyType assembly name.</summary>
	internal const string PolyTypeAssembly = "PolyType";

	/// <summary>The PolyType namespace.</summary>
	internal const string PolyTypeNamespace = "PolyType";

	/// <summary>The shapeable interface metadata name.</summary>
	internal const string IShapeableMetadataName = "IShapeable`1";

	/// <summary>The disposal method name.</summary>
	internal const string Dispose = "Dispose";

	/// <summary>The fully qualified cancellation token type name.</summary>
	internal const string CancellationToken = "global::System.Threading.CancellationToken";

	/// <summary>The fully qualified generic ValueTask type name.</summary>
	internal const string ValueTaskOfT = "global::System.Threading.Tasks.ValueTask<TResult>";

	/// <summary>The fully qualified async enumerable type name.</summary>
	internal const string IAsyncEnumerableOfT = "global::System.Collections.Generic.IAsyncEnumerable<T>";

	/// <summary>The fully qualified generic Task type name.</summary>
	internal const string TaskOfT = "global::System.Threading.Tasks.Task<TResult>";

	/// <summary>The fully qualified ValueTask type name.</summary>
	internal const string ValueTask = "global::System.Threading.Tasks.ValueTask";

	/// <summary>The fully qualified Task type name.</summary>
	internal const string Task = "global::System.Threading.Tasks.Task";

	/// <summary>The void type display name.</summary>
	internal const string Void = "void";
}
