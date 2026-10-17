// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

/// <summary>Describes the statically known contract for a registered RPC method.</summary>
/// <param name="Name">The exact method name used for dispatch.</param>
/// <param name="Description">The method description, if supplied.</param>
/// <param name="Parameters">The RPC parameters, excluding injected parameters.</param>
/// <param name="ResultType">The method result type shape.</param>
/// <param name="SupportsCancellation">Whether the method accepts a cancellation token.</param>
internal sealed record JsonRpcMethodDescription(
	string Name,
	string? Description,
	IReadOnlyList<JsonRpcParameterDescription> Parameters,
	ITypeShape ResultType,
	bool SupportsCancellation)
{
	/// <summary>Creates an RPC method contract from the same PolyType shape used for dispatch.</summary>
	/// <param name="method">The method shape.</param>
	/// <param name="rpcName">The resolved JSON-RPC method name.</param>
	/// <returns>The RPC method contract.</returns>
	internal static JsonRpcMethodDescription Create(IMethodShape method, string rpcName)
	{
		IReadOnlyList<JsonRpcParameterDescription> parameters = method.Parameters
			.Where(static parameter => parameter.ParameterType.Type != typeof(CancellationToken) && parameter.ParameterType.Type != typeof(DispatchRequest))
			.Select(static parameter => new JsonRpcParameterDescription(
				parameter.Name,
				parameter.ParameterType,
				parameter.IsRequired,
				parameter.AttributeProvider?.GetCustomAttribute<DescriptionAttribute>()?.Description))
			.ToArray();
		return new JsonRpcMethodDescription(
			rpcName,
			method.AttributeProvider?.GetCustomAttribute<DescriptionAttribute>()?.Description,
			parameters,
			method.ReturnType,
			method.Parameters.Any(static parameter => parameter.ParameterType.Type == typeof(CancellationToken)));
	}
}
