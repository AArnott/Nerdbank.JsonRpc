// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NETWASM

namespace Nerdbank.JsonRpc;

/// <summary>
/// Stand-ins for the few Microsoft.VisualStudio.Threading extension methods this library uses,
/// since that package has not been ported to NetWasm.
/// </summary>
internal static class ThreadingPolyfills
{
	/// <summary>Consumes a task and ignores its result (the equivalent of VS.Threading's <c>Forget()</c>).</summary>
	/// <param name="task">The task to forget.</param>
	internal static void Forget(this Task? task)
	{
		if (task is not null)
		{
			// Observe any exception so it isn't reported as unobserved. (NetWasm has no TaskScheduler.)
			_ = task.NoThrowAwaitable();
		}
	}

	/// <summary>Awaits a task without throwing if it faults or is canceled (the equivalent of VS.Threading's <c>NoThrowAwaitable()</c>).</summary>
	/// <param name="task">The task to await.</param>
	/// <returns>A task that always completes successfully once <paramref name="task"/> completes.</returns>
	internal static async Task NoThrowAwaitable(this Task task)
	{
		try
		{
			await task.ConfigureAwait(false);
		}
		catch
		{
		}
	}
}

#endif
