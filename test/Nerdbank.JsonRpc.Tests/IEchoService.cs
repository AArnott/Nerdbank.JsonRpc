// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IEchoService
{
	Task<string> EchoAsync(string value, CancellationToken cancellationToken);

	Task<int> DoubleAsync(int value, CancellationToken cancellationToken);
}
