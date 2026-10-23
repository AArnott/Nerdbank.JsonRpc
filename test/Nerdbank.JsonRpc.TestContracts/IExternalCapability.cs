// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

namespace Nerdbank.JsonRpc.TestContracts;

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IExternalCapability
{
	event Action<ExternalPayload>? ExternalChanged;

	Task<ExternalPayload> EchoAsync(ExternalPayload value, CancellationToken cancellationToken);
}
