# Contributing

This project has adopted the [Microsoft Open Source Code of
Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct
FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com)
with any additional questions or comments.

## Best practices

* Use Windows PowerShell or [PowerShell Core][pwsh] (including on Linux/OSX) to run .ps1 scripts.
  Some scripts set environment variables to help you, but they are only retained if you use PowerShell as your shell.

## Prerequisites

All dependencies can be installed by running the `init.ps1` script at the root of the repository
using Windows PowerShell or [PowerShell Core][pwsh] (on any OS).
Some dependencies installed by `init.ps1` may only be discoverable from the same command line environment the init script was run from due to environment variables, so be sure to launch Visual Studio or build the repo from that same environment.
Alternatively, run `init.ps1 -InstallLocality Machine` (which may require elevation) in order to install dependencies at machine-wide locations so Visual Studio and builds work everywhere.

The only prerequisite for building, testing, and deploying from this repository
is the [.NET SDK](https://get.dot.net/).
You should install the version specified in `global.json` or a later version within
the same major.minor.Bxx "hundreds" band.
For example if 2.2.300 is specified, you may install 2.2.300, 2.2.301, or 2.2.310
while the 2.2.400 version would not be considered compatible by .NET SDK.
See [.NET Core Versioning](https://learn.microsoft.com/dotnet/core/versions/) for more information.

## Package restore

The easiest way to restore packages may be to run `init.ps1` which automatically authenticates
to the feeds that packages for this repo come from, if any.
`dotnet restore` or `nuget restore` also work but may require extra steps to authenticate to any applicable feeds.

## Building

This repository can be built on Windows, Linux, and OSX.

Building, testing, and packing this repository can be done by using the standard dotnet CLI commands (e.g. `dotnet build`, `dotnet test`, `dotnet pack`, etc.).

## Testing

You can use `dotnet test` to build and/or test the repo.

There may be tests that are known to be unstable or have special requirements. These can be avoided by running tests using the [dotnet-test-cloud.ps1](tools/dotnet-test-cloud.ps1) script *after* running `dotnet build`.

To validate the shipping library with the NativeAOT compiler, run `dotnet publish test -c Release`.
Then run `./tools/dotnet-test-cloud.ps1 -Configuration Release -IncludeNativeAOT`.
The `test/AotCompatibilityTest` project roots the shipping assembly and is published by the traversal.
The runtime tests are also published and executed as NativeAOT on net10.0. With the current dependencies,
the runtime suite passes on Windows using .NET SDK 10.0.402 and the .NET 10.0.13 NativeAOT toolchain.
The StreamJsonRpc interoperability tests run in their own managed-only project because StreamJsonRpc
requires dynamic code. The Roslyn-based generator tests also remain managed-only.
Managed builds are RID-neutral by default and retain dynamic code, startup hooks, and event tracing for code coverage.
Keep test projects in the solution as well as the traversal so managed testing continues to cover them.

## Performance investigations

The `test/Benchmarks` project uses BenchmarkDotNet and `MemoryDiagnoser`. Build it in Release, then run a focused workload without rebuilding:

```powershell
dotnet build .\test\Benchmarks\Benchmarks.csproj -c Release
dotnet run --project .\test\Benchmarks\Benchmarks.csproj --no-build -c Release -- --filter "*RpcTransportBenchmarks.Ping*" "*RpcTransportBenchmarks.Add" --iterationCount 12 --warmupCount 6 --invocationCount 32768
```

The transport workload covers JSON and MessagePack over in-memory pipes, named pipes, and shared-memory IPC. `Ping` uses a Task-returning proxy contract; `PingValueTask` uses a ValueTask-returning contract; `Add` covers an adjacent small-argument request. Setup validates every result outside the measured operations. `RpcValueTaskPingBenchmarks` also compares ValueTask contracts with StreamJsonRpc.

The `RpcTransportBenchmarks.Public*` workloads await 32 operations in a Task-returning loop and report per-operation values using `OperationsPerInvoke`. This amortizes the benchmark runner's own awaitable consumption and exposes the allocation difference between directly awaiting a pooled ValueTask and a Task-returning proxy's adapter. Run them with `--invocationCount 1024` for 32768 RPC calls per iteration. `RpcNotificationSubmissionBenchmarks` isolates synchronous and deliberately suspended outbound queue acceptance through the encoded API, serialized API, and void proxy; it does not measure wire serialization or remote execution.

`RpcRequestTimeoutBenchmarks` compares direct requests, generated ValueTask proxies, and four-request batches with the outbound timeout disabled or armed for one minute. Each scenario runs with and without a cancellable caller token, over JSON and MessagePack in-memory pipes. Results are per request, including for batches; each invocation executes 32 requests. For example:

```powershell
dotnet run --project .\test\Benchmarks\Benchmarks.csproj --no-build -c Release -- --filter "*RpcRequestTimeoutBenchmarks*" --affinity 2 --warmupCount 20 --iterationCount 20 --invocationCount 2048 --outliers DontRemove --allStats
```

This pins execution to one logical processor and retains outliers. Inspect the warmup and measurement distributions: insufficient warmup can include tiered-compilation transitions in the reported results, even with fixed affinity.

Compare baseline and candidate with the same runtime, parameters, operation counts, and CPU affinity. For allocation optimizations, use sampled allocation stacks for attribution and `Allocated` bytes per operation for the quantitative result. Repeat comparisons when scheduling or multimodal distributions obscure latency changes. Keep generated reports and traces outside source control; record the measured result and important tradeoffs in the change description.

## Releases

Use `nbgv tag` to create a tag for a particular commit that you mean to release.
[Learn more about `nbgv` and its `tag` and `prepare-release` commands](https://dotnet.github.io/Nerdbank.GitVersioning/docs/nbgv-cli.html).

Push the tag.

### GitHub Actions

When your repo is hosted by GitHub and you are using GitHub Actions, you should create a GitHub Release using the standard GitHub UI.
Having previously used `nbgv tag` and pushing the tag will help you identify the precise commit and name to use for this release.

After publishing the release, the `.github/workflows/release.yml` workflow will be automatically triggered, which will:

1. Find the most recent `.github/workflows/build.yml` GitHub workflow run of the tagged release.
1. Upload the `deployables-Linux` artifact from that workflow run to your GitHub Release.
1. Any nuget packages in the `deployables-Linux` artifact will be pushed to nuget.org.

The workflow is written to leverage NuGet.org Trusted Publishing.
You should set `NUGET_USER` as a repo secret to satisfy Trusted Publishing requirements.

## Tutorial and API documentation

Markup links are checked in CI with [mlc](https://github.com/becheran/mlc).
Run `mlc` from the repository root to use the same settings, configured in `.mlc.toml`.

API and hand-written docs are found under the `docfx/` directory and are built by [docfx](https://dotnet.github.io/docfx/).

You can make changes and host the site locally to preview them by switching to that directory and running the `dotnet docfx --serve` command.
After making a change, you can rebuild the docs site while the localhost server is running by running `dotnet docfx` again from a separate terminal.

The `.github/workflows/docs.yml` GitHub Actions workflow publishes the content of these docs to github.io if the workflow itself and [GitHub Pages is enabled for your repository](https://docs.github.com/en/pages/quickstart).

## Updating dependencies

This repo uses Renovate to keep dependencies current.
Configuration is in the `.github/renovate.json` file.
[Learn more about configuring Renovate](https://docs.renovatebot.com/configuration-options/).

When changing the renovate.json file, follow [these validation steps](https://docs.renovatebot.com/config-validation/).

If Renovate is not creating pull requests when you expect it to, check that the [Renovate GitHub App](https://github.com/apps/renovate) is configured for your account or repo.

## Merging latest from Library.Template

### Maintaining your repo based on this template

The best way to keep your repo in sync with Library.Template's evolving features and best practices is to periodically merge the template into your repo:

```ps1
git fetch
git checkout origin/main
./tools/MergeFrom-Template.ps1
# resolve any conflicts, then commit the merge commit.
git push origin -u HEAD
```

[pwsh]: https://learn.microsoft.com/powershell/scripting/install/installing-powershell
