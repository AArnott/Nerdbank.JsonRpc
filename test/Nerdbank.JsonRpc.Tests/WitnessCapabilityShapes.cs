// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[GenerateShapeFor<IWitnessCapability>(IncludeMethods = MethodShapeFlags.PublicInstance)]
[GenerateShapeFor<IWitnessOptionalCapability>(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial class WitnessCapabilityShapes
{
}
