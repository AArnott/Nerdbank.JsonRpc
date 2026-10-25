// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc.BrokeredServices;
using PolyType;

namespace Nerdbank.JsonRpc.BrokeredServiceContracts;

[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
[JsonRpcProxyInterfaceGroup]
[JsonRpcProxyInterfaceGroup(typeof(IBrokeredSubtract))]
public partial interface IBrokeredCalculator
{
	event EventHandler<int> Changed;

	ValueTask<int> AddAsync(int left, int right, CancellationToken cancellationToken = default);

	Task FailAsync(CancellationToken cancellationToken = default);
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IBrokeredSubtract
{
	ValueTask<int> SubtractAsync(int left, int right, CancellationToken cancellationToken = default);
}

public static class BrokeredTestServices
{
	public static ServiceInterface<IBrokeredSubtract> Subtract { get; } = ServiceInterface.Create("test.subtract.v1", PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_BrokeredServiceContracts.Default.IBrokeredSubtract);

	public static JsonRpcMessagePackDescriptor<IBrokeredCalculator> Descriptor { get; } =
		new(new("test.calculator"), PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_BrokeredServiceContracts.Default.IBrokeredCalculator) { AdditionalInterfaces = [Subtract] };

	public static IJsonRpcDescriptor<IBrokeredCalculator> Calculator { get; } = Descriptor;
}
