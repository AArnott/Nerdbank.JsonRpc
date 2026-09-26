// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Globalization;
using System.Text;

namespace Nerdbank.JsonRpc.SourceGeneration;

/// <summary>Writes deterministic UTF-8 C# source with LF newlines and balanced code blocks.</summary>
internal sealed class SourceWriter
{
	private readonly StringBuilder builder = new();

	/// <summary>Gets the current nested block depth.</summary>
	public int Indentation { get; private set; }

	/// <summary>Appends text without changing its contents.</summary>
	/// <param name="value">The text to append.</param>
	/// <returns>This writer.</returns>
	public SourceWriter Append(string? value)
	{
		this.builder.Append(value);
		return this;
	}

	/// <summary>Appends an integer using invariant culture.</summary>
	/// <param name="value">The integer to append.</param>
	/// <returns>This writer.</returns>
	public SourceWriter Append(int value)
	{
		this.builder.Append(value.ToString(CultureInfo.InvariantCulture));
		return this;
	}

	/// <summary>Appends a character without changing its contents.</summary>
	/// <param name="value">The character to append.</param>
	/// <returns>This writer.</returns>
	public SourceWriter Append(char value)
	{
		this.builder.Append(value);
		return this;
	}

	/// <summary>Appends a line using a platform-independent LF newline.</summary>
	/// <returns>This writer.</returns>
	public SourceWriter AppendLine()
	{
		this.builder.Append('\n');
		return this;
	}

	/// <summary>Appends text followed by a platform-independent LF newline.</summary>
	/// <param name="value">The text to append.</param>
	/// <returns>This writer.</returns>
	public SourceWriter AppendLine(string? value)
	{
		this.builder.Append(value).Append('\n');
		return this;
	}

	/// <summary>Writes an opening brace and increases the block depth.</summary>
	/// <param name="prefix">Optional existing indentation prefix.</param>
	/// <returns>This writer.</returns>
	public SourceWriter OpenBlock(string prefix = "")
	{
		this.Append(prefix).AppendLine("{");
		this.Indentation++;
		return this;
	}

	/// <summary>Writes a closing brace and decreases the block depth.</summary>
	/// <param name="prefix">Optional existing indentation prefix.</param>
	/// <returns>This writer.</returns>
	public SourceWriter CloseBlock(string prefix = "")
	{
		if (this.Indentation == 0)
		{
			throw new InvalidOperationException("No open source block is available to close.");
		}

		this.Indentation--;
		this.Append(prefix).AppendLine("}");
		return this;
	}

	/// <summary>Returns the generated source, verifying that all opened blocks were closed.</summary>
	/// <returns>The generated source.</returns>
	public override string ToString()
	{
		if (this.Indentation != 0)
		{
			throw new InvalidOperationException("Generated source contains an unclosed block.");
		}

		return this.builder.ToString();
	}
}
