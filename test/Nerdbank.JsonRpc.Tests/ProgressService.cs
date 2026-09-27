// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class ProgressService : IProgressService
{
	private IProgress<int>? progress;

	public Task<int> ReportProgressAsync(IProgress<int>? progress, CancellationToken cancellationToken)
	{
		this.progress = progress;
		progress?.Report(1);
		progress?.Report(2);
		return Task.FromResult(3);
	}

	public Task<bool> ReportAfterCompletionAsync(CancellationToken cancellationToken)
	{
		this.progress?.Report(3);
		return Task.FromResult(true);
	}

	public Task<int> ReportMultipleProgressAsync(IProgress<int>? numbers, IProgress<string>? messages, CancellationToken cancellationToken)
	{
		numbers?.Report(1);
		messages?.Report("a");
		numbers?.Report(2);
		messages?.Report("b");
		return Task.FromResult(3);
	}
}
