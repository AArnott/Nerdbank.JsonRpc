// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

/// <summary>Describes one serializable RPC parameter.</summary>
/// <param name="Name">The parameter name used for named arguments.</param>
/// <param name="Type">The parameter type shape.</param>
/// <param name="IsRequired">Whether the remote caller must provide the parameter.</param>
/// <param name="IsNullable">Whether the parameter accepts null.</param>
/// <param name="Description">The parameter description, if supplied.</param>
internal sealed record JsonRpcParameterDescription(string Name, ITypeShape Type, bool IsRequired, bool IsNullable, string? Description);
