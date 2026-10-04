// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using PolyType;

public partial class RemoteExceptionTests : TestBase
{
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task RequestError_RoundTripsDiagnostics(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(
			() => fixture.Client.RequestAsync("Fail", EmptyParameters(encoding), this.TimeoutToken).AsTask());

		Assert.Equal("The remote operation failed.", exception.Message);
		Assert.Equal(typeof(InvalidOperationException).FullName, exception.RemoteException!.TypeName);
		InvalidOperationException inner = Assert.IsType<InvalidOperationException>(exception.InnerException);
		Assert.Equal("The remote operation failed.", inner.Message);
		Assert.Equal(typeof(FormatException).FullName, exception.RemoteException.Inner!.TypeName);
		Assert.IsType<FormatException>(inner.InnerException);
		Assert.Contains("remote throw stack", exception.ToString());
		Assert.Contains("local rethrow stack", exception.ToString());
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task EmptyAggregateException_DoesNotInventInnerException(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(
			() => fixture.Client.RequestAsync("FailEmptyAggregate", EmptyParameters(encoding), this.TimeoutToken).AsTask());

		AggregateException inner = Assert.IsAssignableFrom<AggregateException>(exception.InnerException);
		Assert.Empty(inner.InnerExceptions);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task DeepExceptionChain_IsTruncatedWithoutLosingDiagnostics(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(
			() => fixture.Client.RequestAsync("FailDeep", EmptyParameters(encoding), this.TimeoutToken).AsTask());

		Assert.NotNull(exception.RemoteException);
		int nodes = 0;
		for (RemoteExceptionData? item = exception.RemoteException; item is not null; item = item.Inner)
		{
			nodes++;
		}

		Assert.True(nodes <= 12);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task LargeAggregate_IsTruncatedAndPreservesMessage(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(
			() => fixture.Client.RequestAsync("FailAggregate", EmptyParameters(encoding), this.TimeoutToken).AsTask());

		RemoteExceptionData remote = Assert.IsType<RemoteExceptionData>(exception.RemoteException);
		AggregateException inner = Assert.IsAssignableFrom<AggregateException>(exception.InnerException);
		Assert.Equal(remote.Message, inner.Message);
		Assert.True(inner.InnerExceptions.Count < 64);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ReceivedTypedPayloads_UseSharedBudgetAndDepthLimit(JsonRpcEncoding encoding)
	{
		RemoteExceptionTypeMapping mapping = new();
		mapping.Add<TextPayloadException>();
		JsonRpcOptions options = new() { AdditionalExceptionTypes = mapping };
		string firstPayload = new('x', 160 * 1024);
		JsonRpcValue oversizedChildren = CreateTypedExceptionDiagnostics(encoding, firstPayload, new string('y', 160 * 1024), nestedPayloadDepth: 0);
		RemoteInvocationException aggregateError = await ExchangePeerErrorAsync(encoding, oversizedChildren, options, this.TimeoutToken);
		AggregateException aggregate = Assert.IsAssignableFrom<AggregateException>(aggregateError.InnerException);
		Assert.IsType<TextPayloadException>(aggregate.InnerExceptions[0]);
		Assert.IsNotType<TextPayloadException>(aggregate.InnerExceptions[1]);

		JsonRpcValue deeplyNestedPayload = CreateTypedExceptionDiagnostics(encoding, string.Empty, null, nestedPayloadDepth: 25);
		RemoteInvocationException deepError = await ExchangePeerErrorAsync(encoding, deeplyNestedPayload, options, this.TimeoutToken);
		Assert.NotNull(deepError.InnerException);
		Assert.IsNotType<TextPayloadException>(deepError.InnerException);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task CompleteErrorFrameSize_IncludesLargeEchoedId(JsonRpcEncoding encoding)
	{
		(IDuplexPipe rpcPipe, IDuplexPipe peerPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc server = new(CreateTestChannel(rpcPipe, encoding)) { MaximumMessageSize = 4096 };
		server.AddRpcTarget(new FailingService(), new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.Identity });
		await using JsonRpcPipeChannel peer = CreateTestChannel(peerPipe, encoding);
		server.Start();
		peer.Start();

		RequestId largeId = new(new string('i', 3900));
		await peer.Writer.WriteAsync(new JsonRpcRequest { Id = largeId, Method = "Fail", Arguments = EmptyParameters(encoding) }, this.TimeoutToken);
		JsonRpcError response = Assert.IsType<JsonRpcError>(await peer.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(largeId, response.Id);
		Assert.Equal("The request could not be completed.", response.Error.Message);
		Assert.False(response.Error.Data.HasValue);
		Assert.False(server.Completion.IsCompleted);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task OversizedDiagnostics_FallBackToGenericError(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding, maximumMessageSize: 1024);
		RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(
			() => fixture.Client.RequestAsync("FailLarge", EmptyParameters(encoding), this.TimeoutToken).AsTask());

		Assert.Equal("The request could not be completed.", exception.Message);
		Assert.Null(exception.RemoteException);
		Assert.False(exception.ErrorDetails.Data.HasValue);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task DisabledDetails_SendsGenericError(JsonRpcEncoding encoding)
	{
		RecordingLogger logger = new();
		using Fixture fixture = new(encoding, new JsonRpcOptions { IncludeExceptionDetails = false }, serverLogger: logger);
		RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(
			() => fixture.Client.RequestAsync("Fail", EmptyParameters(encoding), this.TimeoutToken).AsTask());

		Assert.Equal(JsonRpcErrorCode.InternalError, exception.ErrorDetails.Code);
		Assert.Equal("The request could not be completed.", exception.Message);
		Assert.Null(exception.RemoteException);
		Assert.Null(exception.InnerException);
		Assert.False(exception.ErrorDetails.Data.HasValue);
		InvalidOperationException loggedException = Assert.IsType<InvalidOperationException>(Assert.Single(logger.Exceptions));
		Assert.Equal("The remote operation failed.", loggedException.Message);
		Assert.Equal("Nested failure.", loggedException.InnerException?.Message);
		Assert.NotNull(loggedException.StackTrace);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task DisabledDetails_CancellationResponseIsGeneric(JsonRpcEncoding encoding)
	{
		RecordingLogger logger = new();
		using Fixture fixture = new(encoding, new JsonRpcOptions { IncludeExceptionDetails = false }, serverLogger: logger);
		using CancellationTokenSource cancellation = new();
		Task request = fixture.Client.RequestAsync("WaitForCancellation", EmptyParameters(encoding), cancellation.Token).AsTask();
		await fixture.Service.CancellationStarted.Task.WithCancellation(this.TimeoutToken);
		cancellation.Cancel();

		OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WithCancellation(this.TimeoutToken));
		RemoteInvocationException remote = Assert.IsType<RemoteInvocationException>(exception.InnerException);
		Assert.Equal(JsonRpcErrorCode.RequestCancelled, remote.ErrorDetails.Code);
		Assert.Equal("The request was canceled.", remote.Message);
		Assert.False(remote.ErrorDetails.Data.HasValue);
		OperationCanceledException loggedException = Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(logger.Exceptions));
		Assert.NotNull(loggedException.StackTrace);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task DisabledDetails_EnumerationFailureIsGenericAndDisposesGenerator(JsonRpcEncoding encoding)
	{
		RecordingLogger logger = new();
		using Fixture fixture = new(encoding, new JsonRpcOptions { IncludeExceptionDetails = false }, serverLogger: logger);
		IAsyncEnumerable<int> sequence = fixture.Client.Attach<IAsyncEnumerableService>().GetFailingSequenceAsync(2, this.TimeoutToken);
		RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(async () =>
		{
			await using IAsyncEnumerator<int> enumerator = sequence.GetAsyncEnumerator(this.TimeoutToken);
			while (await enumerator.MoveNextAsync())
			{
			}
		});

		Assert.Equal(JsonRpcErrorCode.InternalError, exception.ErrorDetails.Code);
		Assert.Equal("The request could not be completed.", exception.Message);
		Assert.False(exception.ErrorDetails.Data.HasValue);
		InvalidOperationException loggedException = Assert.IsType<InvalidOperationException>(Assert.Single(logger.Exceptions));
		Assert.Equal("The sequence failed as requested.", loggedException.Message);
		Assert.NotNull(loggedException.StackTrace);
		await fixture.EnumerableService.FailingGeneratorDisposed.Task.WithCancellation(this.TimeoutToken);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ArgumentException_PreservesMessageAndParameterData(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(
			() => fixture.Client.RequestAsync("FailArgument", EmptyParameters(encoding), this.TimeoutToken).AsTask());

		Assert.Equal(typeof(ArgumentException).FullName, exception.RemoteException!.TypeName);
		Assert.Equal("input", exception.RemoteException.ParameterName);
		Assert.Equal("invalid", exception.RemoteException.Data["reason"]);
		ArgumentException inner = Assert.IsAssignableFrom<ArgumentException>(exception.InnerException);
		Assert.Equal(exception.RemoteException.Message, inner.Message);
		Assert.Equal("input", inner.ParamName);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task AllowedExceptionType_IsReconstructedByShape(JsonRpcEncoding encoding)
	{
		RemoteExceptionTypeMapping mapping = new();
		mapping.Add<MappingException>();
		JsonRpcOptions options = new() { AdditionalExceptionTypes = mapping };
		using Fixture fixture = new(encoding, clientOptions: options, serverOptions: options);
		RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(
			() => fixture.Client.RequestAsync("FailCustom", EmptyParameters(encoding), this.TimeoutToken).AsTask());

		Assert.Equal(typeof(MappingException).FullName, exception.RemoteException!.TypeName);
		MappingException reconstructed = Assert.IsType<MappingException>(exception.InnerException);
		Assert.Equal(42, reconstructed.Quota);
	}

	[Test]
	public void Add_RequiresSurrogateShape()
	{
		RemoteExceptionTypeMapping mapping = new();
		Assert.Throws<ArgumentException>(() => mapping.Add<UnmarshaledException>());
	}

	[Test]
	public void Options_FreezeAdditionalExceptionMappings()
	{
		RemoteExceptionTypeMapping mapping = new();
		mapping.Add<MappingException>();
		JsonRpcOptions options = new() { AdditionalExceptionTypes = mapping };

		Assert.Throws<InvalidOperationException>(() => mapping.Add<MappingException>());
		Assert.Same(mapping, options.AdditionalExceptionTypes);
	}

	private static JsonRpcPipeChannel CreateTestChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
		=> encoding == JsonRpcEncoding.Json
			? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited)
			: new JsonRpcMessagePackChannel(pipe);

	private static async Task<RemoteInvocationException> ExchangePeerErrorAsync(JsonRpcEncoding encoding, JsonRpcValue diagnostics, JsonRpcOptions clientOptions, CancellationToken cancellationToken)
	{
		(IDuplexPipe clientPipe, IDuplexPipe peerPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc client = new(CreateTestChannel(clientPipe, encoding), clientOptions);

		await using JsonRpcPipeChannel peer = CreateTestChannel(peerPipe, encoding);
		client.Start();
		peer.Start();
		Task requestTask = client.RequestAsync("unhandled", EmptyParameters(encoding), cancellationToken).AsTask();
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await peer.Reader.ReadAsync(cancellationToken));
		JsonRpcError response = new()
		{
			Id = request.Id!.Value,
			Error = new JsonRpcErrorDetails { Code = JsonRpcErrorCode.InternalError, Message = "remote failure", Data = diagnostics },
		};
		await peer.Writer.WriteAsync(response, cancellationToken);
		return await Assert.ThrowsAsync<RemoteInvocationException>(() => requestTask.WithCancellation(cancellationToken));
	}

	private static JsonRpcValue CreateTypedExceptionDiagnostics(JsonRpcEncoding encoding, string firstPayload, string? secondPayload, int nestedPayloadDepth)
	{
		if (encoding == JsonRpcEncoding.Json)
		{
			string firstException = nestedPayloadDepth > 0 ? new string('[', nestedPayloadDepth) + "0" + new string(']', nestedPayloadDepth) : System.Text.Json.JsonSerializer.Serialize(firstPayload);
			string exceptionTypeName = System.Text.Json.JsonSerializer.Serialize(typeof(TextPayloadException).FullName);
			string children = "{\"type\":" + exceptionTypeName + ",\"message\":\"child\",\"exception\":" + firstException + "}";
			if (secondPayload is not null)
			{
				children += ", {\"type\":" + exceptionTypeName + ",\"message\":\"child\",\"exception\":" + System.Text.Json.JsonSerializer.Serialize(secondPayload) + "}";
			}

			return JsonRpcValue.FromJson(System.Text.Encoding.UTF8.GetBytes("{\"type\":" + System.Text.Json.JsonSerializer.Serialize(typeof(AggregateException).FullName) + ",\"message\":\"root\",\"children\":[" + children + "]}"));
		}

		using Sequence<byte> buffer = new();
		MessagePackWriter writer = new(buffer);
		writer.WriteMapHeader(6);
		writer.Write(0);
		writer.Write(typeof(AggregateException).FullName);
		writer.Write(1);
		writer.Write("root");
		writer.Write(2);
		writer.WriteNil();
		writer.Write(3);
		writer.Write(0);
		writer.Write(4);
		writer.WriteNil();
		writer.Write(5);
		writer.WriteMapHeader(3);
		writer.Write("parameterName");
		writer.WriteNil();
		writer.Write("children");
		writer.WriteArrayHeader(secondPayload is null ? 1 : 2);
		WriteMessagePackTypedException(ref writer, firstPayload, nestedPayloadDepth);
		if (secondPayload is not null)
		{
			WriteMessagePackTypedException(ref writer, secondPayload, 0);
		}

		writer.Write("data");
		writer.WriteMapHeader(0);
		writer.Flush();
		return JsonRpcValue.FromMessagePack((RawMessagePack)buffer.AsReadOnlySequence);
	}

	private static void WriteMessagePackTypedException(ref MessagePackWriter writer, string payload, int nestedPayloadDepth)
	{
		writer.WriteMapHeader(6);
		writer.Write(0);
		writer.Write(typeof(TextPayloadException).FullName);
		writer.Write(1);
		writer.Write("child");
		writer.Write(2);
		writer.WriteNil();
		writer.Write(3);
		writer.Write(0);
		writer.Write(4);
		writer.WriteNil();
		writer.Write(5);
		writer.WriteMapHeader(4);
		writer.Write("parameterName");
		writer.WriteNil();
		writer.Write("children");
		writer.WriteArrayHeader(0);
		writer.Write("data");
		writer.WriteMapHeader(0);
		writer.Write("exception");
		if (nestedPayloadDepth > 0)
		{
			writer.WriteArrayHeader(1);
			for (int i = 1; i < nestedPayloadDepth; i++)
			{
				writer.WriteArrayHeader(1);
			}

			writer.Write(0);
		}
		else
		{
			writer.Write(payload);
		}
	}

	private static JsonRpcValue EmptyParameters(JsonRpcEncoding encoding)
		=> encoding == JsonRpcEncoding.Json
			? JsonRpcValue.FromJson("[]"u8.ToArray())
			: JsonRpcValue.FromMessagePack(EmptyParamsMsgPack);

	[GenerateShape(Marshaler = typeof(Marshaler))]
	internal sealed partial class MappingException : Exception
	{
		[ConstructorShape]
		public MappingException(string message, int quota)
			: base(message)
		{
			this.Quota = quota;
		}

		public int Quota { get; }

		internal sealed record ExceptionData(string Message, int Quota);

		internal sealed class Marshaler : PolyType.IMarshaler<MappingException, ExceptionData?>
		{
			public MappingException? Unmarshal(ExceptionData? value) => value is null ? null : new(value.Message, value.Quota);

			public ExceptionData? Marshal(MappingException? value) => value is null ? null : new(value.Message, value.Quota);
		}
	}

	[GenerateShape(Marshaler = typeof(Marshaler))]
	internal sealed partial class TextPayloadException(string message) : Exception(message)
	{
		internal sealed class Marshaler : PolyType.IMarshaler<TextPayloadException, string?>
		{
			public TextPayloadException? Unmarshal(string? value) => value is null ? null : new(value);

			public string? Marshal(TextPayloadException? value) => value?.Message;
		}
	}

	[GenerateShape]
	internal sealed partial class UnmarshaledException(string message) : Exception(message);

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class FailingService
	{
		internal TaskCompletionSource<bool> CancellationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public void Fail() => throw new InvalidOperationException("The remote operation failed.", new FormatException("Nested failure."));

		public void FailCustom() => throw new MappingException("Custom failure.", 42);

		public void FailEmptyAggregate() => throw new AggregateException("No inner failures.");

		public void FailDeep()
		{
			Exception exception = new InvalidOperationException("Deepest failure.");
			for (int i = 0; i < 40; i++)
			{
				exception = new InvalidOperationException($"Inner failure {i}.", exception);
			}

			throw exception;
		}

		public void FailAggregate() => throw new AggregateException("Many failures.", Enumerable.Range(0, 63).Select(i => new InvalidOperationException($"Failure {i}.", new FormatException("Nested failure."))));

		public void FailLarge() => throw new InvalidOperationException(new string('x', 6000));

		public async Task WaitForCancellation(CancellationToken cancellationToken)
		{
			this.CancellationStarted.TrySetResult(true);
			await Task.Delay(Timeout.Infinite, cancellationToken);
		}

		public void FailArgument()
		{
			ArgumentException exception = new("Bad value.", "input");
			exception.Data["reason"] = "invalid";
			throw exception;
		}
	}

	private sealed class Fixture : IDisposable
	{
		internal Fixture(JsonRpcEncoding encoding, JsonRpcOptions? serverOptions = null, JsonRpcOptions? clientOptions = null, ILogger? serverLogger = null, int? maximumMessageSize = null)
		{
			(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
			this.Client = new JsonRpc(CreateTestChannel(clientPipe, encoding), clientOptions ?? JsonRpcOptions.Default);
			this.Server = new JsonRpc(CreateTestChannel(serverPipe, encoding), serverOptions ?? JsonRpcOptions.Default)
			{
				Logger = serverLogger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
				MaximumMessageSize = maximumMessageSize ?? 8 * 1024 * 1024,
			};
			this.Service = new FailingService();
			this.Server.AddRpcTarget(this.Service, new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.Identity });
			this.EnumerableService = new AsyncEnumerableService();
			this.Server.AddRpcTarget<IAsyncEnumerableService>(this.EnumerableService);
			this.Server.Start();
			this.Client.Start();
		}

		internal JsonRpc Client { get; }

		internal FailingService Service { get; }

		internal AsyncEnumerableService EnumerableService { get; }

		private JsonRpc Server { get; }

		public void Dispose()
		{
			this.Client.Dispose();
			this.Server.Dispose();
		}
	}

	private sealed class RecordingLogger : ILogger
	{
		internal List<Exception?> Exceptions { get; } = [];

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (exception is not null)
			{
				this.Exceptions.Add(exception);
			}
		}
	}
}
