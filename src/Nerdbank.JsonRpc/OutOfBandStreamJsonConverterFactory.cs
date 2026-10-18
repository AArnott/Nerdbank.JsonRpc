// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO;
using System.IO.Pipelines;
using System.Text;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

internal sealed class OutOfBandStreamJsonConverterFactory(OutOfBandStreamManager manager) : Nerdbank.Json.IJsonConverterFactory
{
	public Nerdbank.Json.JsonConverter? CreateConverter(Type type, PolyType.ITypeShape? shape, in Nerdbank.Json.JsonConverterFactoryContext context)
		=> type == typeof(Stream) ? new StreamConverter(manager) : type == typeof(IDuplexPipe) ? new DuplexPipeConverter(manager) : type == typeof(PipeReader) ? new PipeReaderConverter(manager) : type == typeof(PipeWriter) ? new PipeWriterConverter(manager) : null;

	private abstract class Converter<T>(OutOfBandStreamManager manager) : Nerdbank.Json.JsonConverter<T>
	{
		public override T? Read(ref Nerdbank.Json.JsonReader reader, Nerdbank.Json.SerializationContext context)
		{
			string raw = reader.ReadRawValue();
			return raw == "null" ? default : this.FromPipe(manager.Unmarshal(JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(raw)), RpcCallState.Current));
		}

		public override void Write(ref Nerdbank.Json.JsonWriter writer, T? value, Nerdbank.Json.SerializationContext context)
		{
			if (value is null)
			{
				writer.WriteNullValue();
				return;
			}

			JsonRpcValue token = manager.Marshal(this.ToPipe(value), JsonRpcEncoding.Json, RpcCallState.Current);
			writer.WriteRawValue(Encoding.UTF8.GetString(token.OwnedBytes.Span));
		}

		protected abstract IDuplexPipe ToPipe(T value);

		protected abstract T FromPipe(IDuplexPipe pipe);
	}

	private sealed class StreamConverter(OutOfBandStreamManager manager) : Converter<Stream>(manager)
	{
		protected override IDuplexPipe ToPipe(Stream value) => value.UsePipe();

		protected override Stream FromPipe(IDuplexPipe pipe) => pipe.AsStream();
	}

	private sealed class DuplexPipeConverter(OutOfBandStreamManager manager) : Converter<IDuplexPipe>(manager)
	{
		protected override IDuplexPipe ToPipe(IDuplexPipe value) => value;

		protected override IDuplexPipe FromPipe(IDuplexPipe pipe) => pipe;
	}

	private sealed class PipeReaderConverter(OutOfBandStreamManager manager) : Converter<PipeReader>(manager)
	{
		protected override IDuplexPipe ToPipe(PipeReader value) => new DuplexPipe(value);

		protected override PipeReader FromPipe(IDuplexPipe pipe)
		{
			pipe.Output.Complete();
			return pipe.Input;
		}
	}

	private sealed class PipeWriterConverter(OutOfBandStreamManager manager) : Converter<PipeWriter>(manager)
	{
		protected override IDuplexPipe ToPipe(PipeWriter value) => new DuplexPipe(value);

		protected override PipeWriter FromPipe(IDuplexPipe pipe)
		{
			pipe.Input.Complete();
			return pipe.Output;
		}
	}
}
