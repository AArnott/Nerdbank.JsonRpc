// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Shares call-scoped argument ownership between a call and its returned enumerations.</summary>
internal sealed class CallScopedLifetime : IDisposable
{
	private State? state;

	/// <summary>Initializes a new instance of the <see cref="CallScopedLifetime"/> class.</summary>
	/// <param name="release">Releases the resource after the last owner finishes.</param>
	internal CallScopedLifetime(Action release) => this.state = new(release);

	private CallScopedLifetime(State state) => this.state = state;

	/// <summary>Releases this owner's lease exactly once.</summary>
	public void Dispose()
	{
		if (Interlocked.Exchange(ref this.state, null) is not State state)
		{
			return;
		}

		bool release;
		lock (state)
		{
			release = --state.References == 0;
		}

		if (release)
		{
			state.Release();
		}
	}

	/// <summary>Creates an independent lease for another enumeration.</summary>
	/// <returns>The lease that the enumeration must dispose.</returns>
	internal CallScopedLifetime Retain()
	{
		State state = this.state ?? throw new ObjectDisposedException(nameof(CallScopedLifetime));
		lock (state)
		{
			if (state.References == 0)
			{
				throw new ObjectDisposedException(nameof(CallScopedLifetime));
			}

			state.References++;
		}

		return new(state);
	}

	private sealed class State(Action release)
	{
		/// <summary>Gets or sets the number of remaining owners.</summary>
		internal int References { get; set; } = 1;

		/// <summary>Releases the shared resource.</summary>
		internal void Release() => release();
	}
}
