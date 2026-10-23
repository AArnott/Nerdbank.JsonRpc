// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ICalculator
{
	ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);

	Task<int> MultiplyAsync(int a, int b, CancellationToken cancellationToken);

	ValueTask PingAsync(CancellationToken cancellationToken);

	Task PingTaskAsync(CancellationToken cancellationToken);

	void SetLastValue(int value, CancellationToken cancellationToken);
}

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IDisposableContract
{
	Task<IDisposable> GetDisposableAsync(CancellationToken cancellationToken);

	Task UseDisposableAsync(IDisposable value, CancellationToken cancellationToken);

	Task<bool> IsReturnedDisposableAsync(IDisposable value, CancellationToken cancellationToken);

	Task UseDisposableContainerAsync(DisposableContainer value, CancellationToken cancellationToken);

	Task UseConcreteDisposableContainerAsync(ConcreteDisposableContainer value, CancellationToken cancellationToken);
}

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ICompositeCalculator : ICalculator
{
}

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IOptionalParameterContract
{
	ValueTask<int> AddAsync(int left, int right = 42, CancellationToken cancellationToken = default);

	ValueTask<int> GetValueAsync(int value = 42, CancellationToken cancellationToken = default);

	Task<string?> EchoAsync(string? value = "interface default", CancellationToken cancellationToken = default);
}

internal interface INotGeneratedProxy
{
}

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IGeneratedMemberNameCollision
{
	Task NerdbankJsonRpc_TransformedRpcName0(CancellationToken cancellationToken);
}

#pragma warning disable SA1300 // Element should begin with upper-case letter
[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IGeneratedTransformFieldCollision
{
	Task methodNameTransform(CancellationToken cancellationToken);

	Task OtherMethod(CancellationToken cancellationToken);
}
#pragma warning restore SA1300 // Element should begin with upper-case letter

internal interface IInheritedGeneratedMemberNameCollisionBase
{
	Task NerdbankJsonRpc_MethodNameTransform(CancellationToken cancellationToken);
}

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IInheritedGeneratedMemberNameCollision : IInheritedGeneratedMemberNameCollisionBase
{
	Task OtherMethod(CancellationToken cancellationToken);
}

internal interface IProxyEventBase
{
	event EventHandler<int>? Changed;
}

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IProxyEvents : IProxyEventBase
{
	event EventHandler? Empty;

	event Action? Pulse;

	event Action<object?, int>? Lookalike;

	[EventShape(Name = "custom.pair")]
	event Action<int, string>? Pair;
}

internal interface INullableProxyEventBase<T>
{
	event Action<T>? Changed;
}

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface INullableProxyEvents : INullableProxyEventBase<string?>
{
}

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ICapabilityService : ICalculator, ISubtractCapability
{
}

[GenerateJsonRpcProxy]
[JsonRpcProxyInterfaceGroup]
[JsonRpcProxyInterfaceGroup(typeof(ISubtractCapability))]
[JsonRpcProxyInterfaceGroup(typeof(IGroupedEventCapability))]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IGroupedRootService : ICalculator
{
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IGroupedEventCapability
{
	event EventHandler<int>? Changed;
}

[GenerateJsonRpcProxy]
[JsonRpcProxyInterfaceGroup(typeof(ISecondNamedOperation))]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IFirstNamedOperation
{
	[MethodShape(Name = "first.operation")]
	Task<int> ExecuteAsync(CancellationToken cancellationToken);

	[MethodShape(Name = "first.description")]
	Task<int> DescribeAsync(CancellationToken cancellationToken);
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ISecondNamedOperation
{
	[MethodShape(Name = "second.operation")]
	Task<int> ExecuteAsync(CancellationToken cancellationToken);

	[MethodShape(Name = "second.description")]
	Task<string> DescribeAsync(CancellationToken cancellationToken);
}

[GenerateJsonRpcProxy]
[JsonRpcProxyInterfaceGroup(typeof(Nerdbank.JsonRpc.TestContracts.IExternalCapability))]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IExternalGroupedRoot
{
}

[GenerateJsonRpcProxy]
[JsonRpcProxyInterfaceGroup(typeof(IInheritedOperations))]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IInheritedGroupedRoot
{
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IInheritedOperations : IFirstOperation, ISecondOperation
{
}

internal interface IFirstOperation
{
	[MethodShape(Name = "first.ping")]
	Task<int> PingAsync(CancellationToken cancellationToken);
}

internal interface ISecondOperation
{
	[MethodShape(Name = "second.ping")]
	Task<int> PingAsync(CancellationToken cancellationToken);
}

internal sealed class Calculator : ICalculator
{
	internal TaskCompletionSource<int> NotificationReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal int PingCount { get; private set; }

	public ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken) => new(a + b);

	public Task<int> MultiplyAsync(int a, int b, CancellationToken cancellationToken) => Task.FromResult(a * b);

	public ValueTask PingAsync(CancellationToken cancellationToken)
	{
		this.PingCount++;
		return default;
	}

	public Task PingTaskAsync(CancellationToken cancellationToken)
	{
		this.PingCount++;
		return Task.CompletedTask;
	}

	public void SetLastValue(int value, CancellationToken cancellationToken)
	{
		this.NotificationReceived.TrySetResult(value);
	}
}
