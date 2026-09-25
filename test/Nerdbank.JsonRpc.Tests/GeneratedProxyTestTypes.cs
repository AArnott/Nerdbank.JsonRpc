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
