// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>
/// Indicates that a client proxy should be generated for the annotated RPC contract interface.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class GenerateJsonRpcProxyAttribute : Attribute
{
}
