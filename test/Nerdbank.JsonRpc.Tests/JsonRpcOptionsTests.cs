// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.Streams;

namespace Nerdbank.JsonRpc.Tests;

public class JsonRpcOptionsTests
{
	[Test]
	public void Defaults()
	{
		JsonRpcOptions options = new();
		Assert.Equal(8 * 1024 * 1024, options.MaximumMessageSize);
		Assert.Same(JsonRpcProxyOptions.Default, options.MarshaledProxy);
		Assert.Same(JsonRpcTargetOptions.Default, options.MarshaledTarget);
		Assert.True(options.IncludeExceptionDetails);
		Assert.Equal(Timeout.InfiniteTimeSpan, options.OutboundRequestTimeout);
		Assert.Equal(JsonRpcOptions.Default, options);
	}

	[Test]
	[Arguments(0)]
	[Arguments(-1)]
	[Arguments(int.MinValue)]
	public void MaximumMessageSizeMustBePositive(int size)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new JsonRpcOptions { MaximumMessageSize = size });
		Assert.Throws<ArgumentOutOfRangeException>(() => JsonRpcOptions.Default with { MaximumMessageSize = size });
	}

	[Test]
	[Arguments(1)]
	[Arguments(int.MaxValue)]
	public void MaximumMessageSizeAcceptsPositiveValues(int size)
	{
		Assert.Equal(size, new JsonRpcOptions { MaximumMessageSize = size }.MaximumMessageSize);
	}

	[Test]
	public void MarshaledOptionsRejectNull()
	{
		Assert.Throws<ArgumentNullException>(() => new JsonRpcOptions { MarshaledProxy = null! });
		Assert.Throws<ArgumentNullException>(() => new JsonRpcOptions { MarshaledTarget = null! });
	}

	[Test]
	public void WithExpressionPreservesOriginalPolicy()
	{
		JsonRpcOptions original = new();
		JsonRpcProxyOptions proxyOptions = new() { MethodNameTransform = CommonMethodNameTransforms.Identity };
		JsonRpcTargetOptions targetOptions = new() { MethodNameTransform = CommonMethodNameTransforms.Identity };
		JsonRpcOptions modified = original with
		{
			MaximumMessageSize = 1024,
			MarshaledProxy = proxyOptions,
			MarshaledTarget = targetOptions,
		};

		Assert.Equal(8 * 1024 * 1024, original.MaximumMessageSize);
		Assert.Same(JsonRpcProxyOptions.Default, original.MarshaledProxy);
		Assert.Same(JsonRpcTargetOptions.Default, original.MarshaledTarget);
		Assert.Equal(1024, modified.MaximumMessageSize);
		Assert.Same(proxyOptions, modified.MarshaledProxy);
		Assert.Same(targetOptions, modified.MarshaledTarget);
	}

	[Test]
	public void SharedOptionsDoNotShareConnectionRuntimeState()
	{
		JsonRpcOptions options = new() { MaximumMessageSize = 1024 };
		using JsonRpc first = new(new JsonRpcJsonChannel(FullDuplexStream.CreatePipePair().Item1, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited), options);
		using JsonRpc second = new(new JsonRpcJsonChannel(FullDuplexStream.CreatePipePair().Item1, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited), options);
		Assert.Same(options, first.Options);
		Assert.Same(options, second.Options);
		Assert.NotSame(first.SynchronizationContext, second.SynchronizationContext);

		first.Dispose();

		Assert.True(first.IsDisposed);
		Assert.False(second.IsDisposed);
		Assert.Equal(JsonRpcState.NotStarted, second.ConnectionState);
	}
}
