// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.Reflection;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

/// <summary>Describes the statically known contract for a registered RPC method.</summary>
/// <param name="Name">The exact method name used for dispatch.</param>
/// <param name="Description">The method description, if supplied.</param>
/// <param name="Parameters">The RPC parameters, excluding injected parameters.</param>
/// <param name="ResultType">The method result type shape.</param>
/// <param name="ResultIsNullable">Whether the method result accepts null.</param>
/// <param name="SupportsCancellation">Whether the method accepts a cancellation token.</param>
internal sealed record JsonRpcMethodDescription(
	string Name,
	string? Description,
	IReadOnlyList<JsonRpcParameterDescription> Parameters,
	ITypeShape ResultType,
	bool ResultIsNullable,
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
				!parameter.ParameterType.Type.IsValueType && !parameter.IsNonNullable,
				parameter.AttributeProvider?.GetCustomAttribute<DescriptionAttribute>()?.Description))
			.ToArray();
		return new JsonRpcMethodDescription(
			rpcName,
			method.AttributeProvider?.GetCustomAttribute<DescriptionAttribute>()?.Description,
			parameters,
			method.ReturnType,
			IsNullableResult(method),
			method.Parameters.Any(static parameter => parameter.ParameterType.Type == typeof(CancellationToken)));
	}

	private static bool IsNullableResult(IMethodShape method)
	{
		if (method.ReturnType.Type.IsValueType)
		{
			return false;
		}

#if NET
		if (method.MethodBase is MethodInfo methodInfo)
		{
			NullabilityInfo? returnInfo;
			try
			{
				returnInfo = new NullabilityInfoContext().Create(methodInfo.ReturnParameter);
			}
			catch (InvalidOperationException)
			{
				return true;
			}

			if (methodInfo.ReturnType == method.ReturnType.Type)
			{
				return returnInfo.ReadState != NullabilityState.NotNull;
			}

			if (methodInfo.ReturnType.IsGenericType)
			{
				Type[] returnTypes = methodInfo.ReturnType.GetGenericArguments();
				for (int i = 0; i < returnTypes.Length; i++)
				{
					if (returnTypes[i] == method.ReturnType.Type && i < returnInfo.GenericTypeArguments.Length)
					{
						return returnInfo.GenericTypeArguments[i].ReadState != NullabilityState.NotNull;
					}
				}
			}
		}
#else
		if (method.MethodBase is MethodInfo methodInfo)
		{
			int resultFlagIndex = 0;
			if (methodInfo.ReturnType != method.ReturnType.Type && methodInfo.ReturnType.IsGenericType)
			{
				Type[] genericArguments = methodInfo.ReturnType.GetGenericArguments();
				int argumentIndex = Array.IndexOf(genericArguments, method.ReturnType.Type);
				if (argumentIndex >= 0)
				{
					resultFlagIndex = argumentIndex + 1;
				}
			}

			byte? returnFlag = GetNullableFlag(methodInfo.ReturnParameter.GetCustomAttributesData(), resultFlagIndex)
				?? GetNullableFlag(methodInfo.GetCustomAttributesData(), resultFlagIndex);
			if (returnFlag == 1)
			{
				return false;
			}

			if (returnFlag == 2)
			{
				return true;
			}

			byte? context = GetNullableContext(methodInfo.GetCustomAttributesData())
				?? GetNullableContext(methodInfo.DeclaringType?.GetCustomAttributesData());
			if (context == 1)
			{
				return false;
			}

			if (context == 2)
			{
				return true;
			}
		}
#endif

		// Unknown nullability should not advertise a narrower contract than the method can implement.
		return true;
	}

#if !NET
	private static byte? GetNullableContext(IEnumerable<CustomAttributeData>? attributes)
		=> attributes?.FirstOrDefault(static attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.NullableContextAttribute")?.ConstructorArguments.FirstOrDefault().Value is byte value
			? value
			: null;

	private static byte? GetNullableFlag(IEnumerable<CustomAttributeData> attributes, int index)
	{
		CustomAttributeData? attribute = attributes.FirstOrDefault(static item => item.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute");
		if (attribute?.ConstructorArguments.FirstOrDefault().Value is byte flag)
		{
			return flag;
		}

		if (attribute?.ConstructorArguments.FirstOrDefault().Value is IEnumerable<CustomAttributeTypedArgument> flags)
		{
			return flags.Skip(index).Select(static item => item.Value).OfType<byte>().FirstOrDefault();
		}

		return null;
	}
#endif
}
