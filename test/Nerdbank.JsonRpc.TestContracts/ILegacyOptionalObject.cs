// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

namespace Nerdbank.JsonRpc.TestContracts;

// Reproduces the metadata layout emitted before interface groups, without running the proxy generator.
[RpcMarshalable]
[RpcMarshalableOptionalInterface(1, typeof(ILegacyCapability))]
[LegacyBaseFactory]
[LegacyOptionalFactory]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ILegacyOptionalObject : IDisposable
{
}

public interface ILegacyCapability
{
}
