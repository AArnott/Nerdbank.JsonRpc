// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO;
using System.IO.Pipelines;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc;

internal sealed class OutOfBandStreamMessagePackConverterFactory(OutOfBandStreamManager manager) : IMessagePackConverterFactory
{
	public MessagePackConverter? CreateConverter(Type type, PolyType.ITypeShape? shape, in ConverterContext context)
		=> type == typeof(Stream) ? new StreamConverter(manager) : type == typeof(IDuplexPipe) ? new DuplexPipeConverter(manager) : type == typeof(PipeReader) ? new PipeReaderConverter(manager) : type == typeof(PipeWriter) ? new PipeWriterConverter(manager) : null;

	private abstract class Converter<T>(OutOfBandStreamManager manager) : MessagePackConverter<T>
	{
		public override T? Read(ref MessagePackReader reader, SerializationContext context)
			=> reader.TryReadNil() ? default : this.FromPipe(manager.Unmarshal(JsonRpcValue.FromMessagePack(reader.ReadRaw(context))));

		public override void Write(ref MessagePackWriter writer, in T? value, SerializationContext context)
		{
			if (value is null)
			{
				writer.WriteNil();
				return;
			}

			writer.Write(manager.Marshal(this.ToPipe(value), JsonRpcEncoding.MessagePack).AsOwnedMessagePack());
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
