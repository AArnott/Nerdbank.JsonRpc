// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc.TestContracts;
using PolyType;

[GenerateShapeFor<ILegacyCapability>(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial class LegacyCapabilityWitness
{
}
