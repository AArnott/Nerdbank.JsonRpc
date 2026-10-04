// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
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

		AggregateException inner = Assert.IsType<AggregateException>(exception.InnerException);
		Assert.Empty(inner.InnerExceptions);
	}

	[Test]
	public async Task DisabledDetails_SendsGenericError()
	{
		RecordingLogger logger = new();
		using Fixture fixture = new(JsonRpcEncoding.MessagePack, new JsonRpcOptions { IncludeExceptionDetails = false }, serverLogger: logger);
		RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(
			() => fixture.Client.RequestAsync("Fail", EmptyParameters(JsonRpcEncoding.MessagePack), this.TimeoutToken).AsTask());

		Assert.Equal("The request could not be completed.", exception.Message);
		Assert.Null(exception.RemoteException);
		Assert.Null(exception.InnerException);
		Assert.False(exception.ErrorDetails.Data.HasValue);
		Assert.Equal(1, logger.Exceptions.Count);
		Assert.IsType<InvalidOperationException>(logger.Exceptions[0]);
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

	[GenerateShape]
	internal sealed partial class UnmarshaledException(string message) : Exception(message);

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class FailingService
	{
		public void Fail() => throw new InvalidOperationException("The remote operation failed.", new FormatException("Nested failure."));

		public void FailCustom() => throw new MappingException("Custom failure.", 42);

		public void FailEmptyAggregate() => throw new AggregateException("No inner failures.");

		public void FailArgument()
		{
			ArgumentException exception = new("Bad value.", "input");
			exception.Data["reason"] = "invalid";
			throw exception;
		}
	}

	private sealed class Fixture : IDisposable
	{
		internal Fixture(JsonRpcEncoding encoding, JsonRpcOptions? serverOptions = null, JsonRpcOptions? clientOptions = null, ILogger? serverLogger = null)
		{
			(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
			this.Client = new JsonRpc(CreateChannel(clientPipe, encoding), clientOptions ?? JsonRpcOptions.Default);
			this.Server = new JsonRpc(CreateChannel(serverPipe, encoding), serverOptions ?? JsonRpcOptions.Default)
			{
				Logger = serverLogger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
			};
			this.Server.AddRpcTarget(new FailingService(), new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.Identity });
			this.Server.Start();
			this.Client.Start();
		}

		internal JsonRpc Client { get; }

		private JsonRpc Server { get; }

		public void Dispose()
		{
			this.Client.Dispose();
			this.Server.Dispose();
		}

		private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
			=> encoding == JsonRpcEncoding.Json
				? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited)
				: new JsonRpcMessagePackChannel(pipe);
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
