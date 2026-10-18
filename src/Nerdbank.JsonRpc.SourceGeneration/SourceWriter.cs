// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Globalization;
using System.Text;

namespace Nerdbank.JsonRpc.SourceGeneration;

/// <summary>Writes deterministic UTF-8 C# source with LF newlines, balanced code blocks, and automatic indentation.</summary>
/// <remarks>
/// Each non-empty line is prefixed with one tab per open block (plus any <see cref="Indent"/> levels),
/// so callers should not include leading indentation in the text they append.
/// </remarks>
internal sealed class SourceWriter
{
	private readonly StringBuilder builder = new();

	private bool atLineStart = true;

	/// <summary>Gets the current indentation depth.</summary>
	public int Indentation { get; private set; }

	/// <summary>Gets the current nested block depth.</summary>
	public int BlockDepth { get; private set; }

	/// <summary>Appends text without changing its contents.</summary>
	/// <param name="value">The text to append.</param>
	/// <returns>This writer.</returns>
	public SourceWriter Append(string? value)
	{
		if (!string.IsNullOrEmpty(value))
		{
			this.WriteIndentationIfAtLineStart();
			this.builder.Append(value);
		}

		return this;
	}

	/// <summary>Appends an integer using invariant culture.</summary>
	/// <param name="value">The integer to append.</param>
	/// <returns>This writer.</returns>
	public SourceWriter Append(int value) => this.Append(value.ToString(CultureInfo.InvariantCulture));

	/// <summary>Appends a character without changing its contents.</summary>
	/// <param name="value">The character to append.</param>
	/// <returns>This writer.</returns>
	public SourceWriter Append(char value)
	{
		this.WriteIndentationIfAtLineStart();
		this.builder.Append(value);
		return this;
	}

	/// <summary>Appends a line using a platform-independent LF newline.</summary>
	/// <returns>This writer.</returns>
	public SourceWriter AppendLine()
	{
		this.builder.Append('\n');
		this.atLineStart = true;
		return this;
	}

	/// <summary>Appends text followed by a platform-independent LF newline.</summary>
	/// <param name="value">The text to append.</param>
	/// <returns>This writer.</returns>
	public SourceWriter AppendLine(string? value) => this.Append(value).AppendLine();

	/// <summary>Increases the indentation for subsequent lines without opening a block.</summary>
	/// <returns>A value that restores the previous indentation when disposed.</returns>
	public IndentScope Indent()
	{
		this.Indentation++;
		return new IndentScope(this);
	}

	/// <summary>Writes an opening brace and increases the block depth and indentation.</summary>
	/// <returns>This writer.</returns>
	public SourceWriter OpenBlock()
	{
		this.AppendLine("{");
		this.BlockDepth++;
		this.Indentation++;
		return this;
	}

	/// <summary>Writes a closing brace and decreases the block depth and indentation.</summary>
	/// <returns>This writer.</returns>
	public SourceWriter CloseBlock()
	{
		if (this.BlockDepth == 0)
		{
			throw new InvalidOperationException("No open source block is available to close.");
		}

		this.BlockDepth--;
		this.Indentation--;
		return this.AppendLine("}");
	}

	/// <summary>Returns the generated source, verifying that all opened blocks were closed.</summary>
	/// <returns>The generated source.</returns>
	public override string ToString()
	{
		if (this.BlockDepth != 0)
		{
			throw new InvalidOperationException("Generated source contains an unclosed block.");
		}

		return this.builder.ToString();
	}

	private void WriteIndentationIfAtLineStart()
	{
		if (this.atLineStart)
		{
			this.builder.Append('\t', this.Indentation);
			this.atLineStart = false;
		}
	}

	/// <summary>Restores the indentation of a <see cref="SourceWriter"/> when disposed.</summary>
	/// <param name="writer">The writer whose indentation to restore.</param>
	internal readonly struct IndentScope(SourceWriter writer) : IDisposable
	{
		/// <summary>Decreases the indentation of the writer.</summary>
		public void Dispose() => writer.Indentation--;
	}
}
