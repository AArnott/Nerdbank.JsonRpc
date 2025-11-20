// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;

namespace Nerdbank.JsonRpc;

public class JsonRpcException : Exception
{
	public JsonRpcException(JsonRpcErrorDetails errorDetails)
		: this(errorDetails, null)
	{
	}

	public JsonRpcException(JsonRpcErrorDetails errorDetails, Exception? innerException)
		: base(Requires.NotNull(errorDetails).Message, innerException)
	{
		this.ErrorDetails = errorDetails;
	}

	public JsonRpcErrorDetails ErrorDetails { get; }
}
