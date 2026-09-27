// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IProgressService
{
	Task<int> ReportProgressAsync(IProgress<int>? progress, CancellationToken cancellationToken);

	Task<bool> ReportAfterCompletionAsync(CancellationToken cancellationToken);

	Task<int> ReportMultipleProgressAsync(IProgress<int>? numbers, IProgress<string>? messages, CancellationToken cancellationToken);
}
