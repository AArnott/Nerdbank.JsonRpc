# Copilot instructions for this repository

## High level guidance

* Review the `CONTRIBUTING.md` file for instructions to build and test the software.
* Run the `.github/Prime-ForCopilot.ps1` script (once) before running any `dotnet` or `msbuild` commands.
  If you see any build errors about not finding git objects or a shallow clone, it may be time to run this script again.

## Software Design

* Design APIs to be highly testable, and all functionality should be tested.
* Avoid introducing binary breaking changes in public APIs of projects under `src` unless their project files have `IsPackable` set to `false`.

## Testing

**IMPORTANT**: This repository uses [TUnit](https://tunit.dev) on Microsoft.Testing.Platform (MTP v2). Neither the traditional VSTest `--filter` syntax nor xunit's `--filter-method`/`--filter-trait` options work. Use `--treenode-filter` as shown below.

* There should generally be one test project (under the `test` directory) per shipping project (under the `src` directory). Test projects are named after the project being tested with a `.Tests` suffix.
* Tests are written with TUnit (`[Test]`, `[Arguments(...)]`, etc.). Assertions use the `xunit.v3.assert` package (`Assert.Equal`, `Assert.ThrowsAsync`, etc.).
* Some tests are known to be unstable. When running tests, you should skip the unstable ones by using `-- --treenode-filter "/**[Category!=FailsInCloudTest]"`.
* Test every new feature first with code that resembles what its users will likely (or may) write.
  Where applicable, test from both parties' perspectives (e.g. client and server).
  Then add tests that focus on the feature's underlying mechanics where necessary for full coverage.
* Avoid reflection in tests. Base assertions on user-observable behavior (including the wire protocol) rather than private fields or other implementation details.
  Do not use `InternalsVisibleTo` between product and test assemblies.

### Running Tests

**Run all tests**:
```bash
dotnet test --no-build -c Release
```

**Run tests for a specific test project**:
```bash
dotnet test --project test/Library.Tests/Library.Tests.csproj --no-build -c Release
```

**Run a single test method**:
```bash
dotnet test --project test/Library.Tests/Library.Tests.csproj --no-build -c Release -- --treenode-filter "/*/*/ClassName/MethodName"
```

**Run all tests in a test class**:
```bash
dotnet test --project test/Library.Tests/Library.Tests.csproj --no-build -c Release -- --treenode-filter "/*/*/ClassName/*"
```

**Run tests with wildcard matching**:
```bash
dotnet test --project test/Library.Tests/Library.Tests.csproj --no-build -c Release -- --treenode-filter "/*/*/*/*Pattern*"
```

**Run tests with a specific property** (e.g. `[Category("value")]`):
```bash
dotnet test --project test/Library.Tests/Library.Tests.csproj --no-build -c Release -- --treenode-filter "/**[Category=value]"
```

**Exclude tests with a specific property** (skip unstable tests):
```bash
dotnet test --project test/Library.Tests/Library.Tests.csproj --no-build -c Release -- --treenode-filter "/**[Category!=FailsInCloudTest]"
```

**Run tests for a specific framework only**:
```bash
dotnet test --project test/Library.Tests/Library.Tests.csproj --no-build -c Release --framework net9.0
```

**List all available tests without running them**:
```bash
cd test/Library.Tests
dotnet run --no-build -c Release --framework net9.0 -- --list-tests
```

**Key points about test filtering with TUnit / MTP v2**:
- Options after `--` are passed to the test runner, not to `dotnet test`
- `--treenode-filter` paths have the form `/Assembly/Namespace/Class/Method`; use `*` for any single segment and `/**` to match any depth
- Test classes in the global namespace still need a namespace segment, so use `/*/*/Class/Method`
- Append `[Property=value]` or `[Property!=value]` to filter on test properties such as `Category`
- A test run that matches zero tests exits with code 5, which usually indicates a malformed filter
- Traditional VSTest `--filter` expressions and xunit's `--filter-*` options do NOT work

## Coding style

* Honor StyleCop rules and fix any reported build warnings *after* getting tests to pass.
* In C# files, use namespace *statements* instead of namespace *blocks* for all new files.
* Add API doc comments to all new public and internal members.
