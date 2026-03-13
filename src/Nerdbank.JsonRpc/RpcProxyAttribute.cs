// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>
/// Applied to a partial class that declares one or more RPC interfaces
/// that are themselves attributed with <see cref="GenerateShapeAttribute"/>
/// to trigger source generation of the implementations of those interfaces
/// such that the class will act as an RPC client proxy.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class RpcProxyAttribute : Attribute
{
}
