// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc.TestContracts;
using PolyType;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IOptionalInterfaceService
{
	Task<IOptionalObject> GetObjectAsync(int capabilities, CancellationToken cancellationToken);

	Task<bool> HasMultiplyCapabilityAsync(IOptionalObject value, CancellationToken cancellationToken);

	Task<IGroupedOptionalObject> GetGroupedObjectAsync(int capabilities, CancellationToken cancellationToken);

	Task<IIncompleteGroupedObject> GetIncompleteObjectAsync(CancellationToken cancellationToken);

	Task<IFullOnlyObject> GetFullOnlyObjectAsync(bool multiply, CancellationToken cancellationToken);

	Task<ISiblingCapabilityObject> GetSiblingObjectAsync(CancellationToken cancellationToken);

	Task<ILegacyOptionalObject> GetLegacyObjectAsync(bool optional, CancellationToken cancellationToken);
}

[RpcMarshalable]
[RpcMarshalableOptionalInterface(-7, typeof(ISubtractCapability))]
[RpcMarshalableOptionalInterface(42, typeof(IMultiplyCapability))]
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

[RpcMarshalable]
[RpcMarshalableOptionalInterface(-7, typeof(ISubtractCapability))]
[RpcMarshalableOptionalInterface(42, typeof(IMultiplyCapability))]
[JsonRpcProxyInterfaceGroup]
[JsonRpcProxyInterfaceGroup(typeof(ISubtractCapability))]
[JsonRpcProxyInterfaceGroup(typeof(ISubtractCapability), typeof(IMultiplyCapability))]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IGroupedOptionalObject : IDisposable
{
}

[RpcMarshalable]
[RpcMarshalableOptionalInterface(42, typeof(IMultiplyCapability))]
[JsonRpcProxyInterfaceGroup]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IIncompleteGroupedObject : IDisposable
{
}

[RpcMarshalable]
[RpcMarshalableOptionalInterface(42, typeof(IMultiplyCapability))]
[JsonRpcProxyInterfaceGroup(typeof(IMultiplyCapability))]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IFullOnlyObject : IDisposable
{
}

[RpcMarshalable]
[RpcMarshalableOptionalInterface(1, typeof(IServerCapability))]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ISiblingCapabilityObject : IDisposable
{
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ICommonCapability
{
	Task<int> ReadAsync(CancellationToken cancellationToken);
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IServerCapability : ICommonCapability
{
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IClientCapability : ICommonCapability
{
}

internal sealed class OptionalInterfaceService : IOptionalInterfaceService
{
	public Task<ILegacyOptionalObject> GetLegacyObjectAsync(bool optional, CancellationToken cancellationToken)
		=> Task.FromResult<ILegacyOptionalObject>(optional ? new LegacyOptionalObject() : new LegacyObject());

	public Task<ISiblingCapabilityObject> GetSiblingObjectAsync(CancellationToken cancellationToken)
		=> Task.FromResult<ISiblingCapabilityObject>(new SiblingCapabilityObject());

	public Task<bool> HasMultiplyCapabilityAsync(IOptionalObject value, CancellationToken cancellationToken)
		=> Task.FromResult(value.As<IMultiplyCapability>() is not null);

	public Task<IGroupedOptionalObject> GetGroupedObjectAsync(int capabilities, CancellationToken cancellationToken)
		=> Task.FromResult<IGroupedOptionalObject>(capabilities switch
		{
			0 => new GroupedObject(),
			1 => new SubtractGroupedObject(),
			2 => new MultiplyGroupedObject(),
			3 => new AllGroupedObject(),
			_ => throw new ArgumentOutOfRangeException(nameof(capabilities)),
		});

	public Task<IIncompleteGroupedObject> GetIncompleteObjectAsync(CancellationToken cancellationToken)
		=> Task.FromResult<IIncompleteGroupedObject>(new MultiplyGroupedObject());

	public Task<IFullOnlyObject> GetFullOnlyObjectAsync(bool multiply, CancellationToken cancellationToken)
		=> Task.FromResult<IFullOnlyObject>(multiply ? new MultiplyGroupedObject() : new GroupedObject());

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

	private class GroupedObject : IGroupedOptionalObject, IFullOnlyObject
	{
		public void Dispose()
		{
		}
	}

	private sealed class SubtractGroupedObject : GroupedObject, ISubtractCapability
	{
		public Task<int> CalculateAsync(int value, CancellationToken cancellationToken) => Task.FromResult(10 - value);
	}

	private sealed class MultiplyGroupedObject : GroupedObject, IIncompleteGroupedObject, IMultiplyCapability
	{
		public Task<int> CalculateAsync(int value, CancellationToken cancellationToken) => Task.FromResult(10 * value);
	}

	private sealed class AllGroupedObject : GroupedObject, ISubtractCapability, IMultiplyCapability
	{
		Task<int> ISubtractCapability.CalculateAsync(int value, CancellationToken cancellationToken) => Task.FromResult(10 - value);

		Task<int> IMultiplyCapability.CalculateAsync(int value, CancellationToken cancellationToken) => Task.FromResult(10 * value);
	}

	private sealed class SiblingCapabilityObject : ISiblingCapabilityObject, IServerCapability
	{
		public Task<int> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(42);

		public void Dispose()
		{
		}
	}

	private class LegacyObject : ILegacyOptionalObject
	{
		public void Dispose()
		{
		}
	}

	private sealed class LegacyOptionalObject : LegacyObject, ILegacyCapability
	{
	}
}
