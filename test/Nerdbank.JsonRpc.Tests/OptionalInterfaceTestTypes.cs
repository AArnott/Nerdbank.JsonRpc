// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IOptionalInterfaceService
{
	Task<IOptionalObject> GetObjectAsync(int capabilities, CancellationToken cancellationToken);
}

[RpcMarshalable]
[RpcMarshalableOptionalInterface(-7, typeof(ISubtractCapability))]
[RpcMarshalableOptionalInterface(42, typeof(IMultiplyCapability))]
#pragma warning disable CS0618 // Exercise compatibility with metadata emitted by the previous source generator.
[JsonRpcOptionalProxyImplementation(typeof(LegacyOptionalObjectProxy), -7)]
#pragma warning restore CS0618
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IOptionalObject : IDisposable
{
	Task<int> GetValueAsync(CancellationToken cancellationToken);
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ISubtractCapability
{
	Task<int> CalculateAsync(int value, CancellationToken cancellationToken);
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IMultiplyCapability
{
	Task<int> CalculateAsync(int value, CancellationToken cancellationToken);
}

internal sealed class OptionalInterfaceService : IOptionalInterfaceService
{
	public Task<IOptionalObject> GetObjectAsync(int capabilities, CancellationToken cancellationToken)
	{
		IOptionalObject result = capabilities switch
		{
			0 => new OptionalObject(),
			1 => new SubtractOptionalObject(),
			3 => new AllOptionalObject(),
			_ => throw new ArgumentOutOfRangeException(nameof(capabilities)),
		};
		return Task.FromResult(result);
	}
}
