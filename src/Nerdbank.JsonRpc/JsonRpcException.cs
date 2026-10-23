// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;

namespace Nerdbank.JsonRpc;

/// <summary>Represents a JSON-RPC error response.</summary>
/// <remarks>Throw this exception from an RPC target method to send <see cref="ErrorDetails"/> as the error response.</remarks>
public class JsonRpcException : Exception
{
	/// <summary>Initializes a new instance of the <see cref="JsonRpcException"/> class.</summary>
	/// <param name="errorDetails">The JSON-RPC error details.</param>
	public JsonRpcException(JsonRpcErrorDetails errorDetails)
		: this(errorDetails, null)
	{
	}

	/// <summary>Initializes a new instance of the <see cref="JsonRpcException"/> class.</summary>
	/// <param name="errorDetails">The JSON-RPC error details.</param>
	/// <param name="innerException">The exception that caused this exception.</param>
	public JsonRpcException(JsonRpcErrorDetails errorDetails, Exception? innerException)
		: base(Requires.NotNull(errorDetails).Message, innerException)
	{
		this.ErrorDetails = errorDetails;
	}

	/// <summary>Gets the JSON-RPC error details.</summary>
	public JsonRpcErrorDetails ErrorDetails { get; }
}
