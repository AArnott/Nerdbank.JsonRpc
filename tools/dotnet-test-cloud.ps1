#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Runs tests as they are run in cloud test runs.
.PARAMETER Configuration
    The configuration within which to run tests
.PARAMETER IncludeNativeAOT
    Runs the NativeAOT-compiled runtime tests and fails if the expected image is missing.
.PARAMETER Agent
    The name of the agent. This is used in preparing test run titles.
.PARAMETER PublishResults
    A switch to publish results to Azure Pipelines.
.PARAMETER x86
    A switch to run the tests in an x86 process.
.PARAMETER dotnet32
    The path to a 32-bit dotnet executable to use.
.PARAMETER NoCoverage
    A switch to skip code coverage collection.
#>
[CmdletBinding()]
Param(
    [string]$Configuration='Debug',
    [switch]$IncludeNativeAOT,
    [string]$Agent='Local',
    [switch]$PublishResults,
    [switch]$x86,
    [string]$dotnet32,
    [switch]$NoCoverage
)

$RepoRoot = (Resolve-Path "$PSScriptRoot/..").Path
$ArtifactStagingFolder = & "$PSScriptRoot/Get-ArtifactsStagingDirectory.ps1"
$OnCI = ($env:CI -or $env:TF_BUILD)

$dotnet = 'dotnet'
if ($x86) {
  $x86RunTitleSuffix = ", x86"
  if ($dotnet32) {
    $dotnet = $dotnet32
  } else {
    $dotnet32Possibilities = "$PSScriptRoot\../obj/tools/x86/.dotnet/dotnet.exe", "$env:AGENT_TOOLSDIRECTORY/x86/dotnet/dotnet.exe", "${env:ProgramFiles(x86)}\dotnet\dotnet.exe"
    $dotnet32Matches = $dotnet32Possibilities |? { Test-Path $_ }
    if ($dotnet32Matches) {
      $dotnet = Resolve-Path @($dotnet32Matches)[0]
      Write-Host "Running tests using `"$dotnet`"" -ForegroundColor DarkGray
    } else {
      Write-Error "Unable to find 32-bit dotnet.exe"
      exit 1
    }
  }
}

$testBinLog = Join-Path $ArtifactStagingFolder (Join-Path build_logs test.binlog)
$testLogs = Join-Path $ArtifactStagingFolder test_logs
if (Test-Path -LiteralPath $testLogs) {
    Remove-Item -LiteralPath $testLogs -Recurse -Force
}

$globalJson = Get-Content $PSScriptRoot/../global.json | ConvertFrom-Json
$isMTP = $globalJson.test.runner -eq 'Microsoft.Testing.Platform'
$extraArgs = @()
$failedTests = 0

if ($isMTP) {
    if ($OnCI) { $extraArgs += '--no-progress' }

    $dumpSwitches = @(
        ,'--hangdump'
        ,'--hangdump-timeout','5m'
        ,'--crashdump'
        ,'--crashdump-type','Heap'
        ,'--crash-report-if-supported'
    )
    $mtpArgs = @(
        ,'--diagnostic'
        ,'--diagnostic-output-directory',$testLogs
        ,'--diagnostic-verbosity','Information'
        ,'--results-directory',$testLogs
        ,'--report-trx'
    )
    $tunitArgs = @($mtpArgs)

    if (-not $NoCoverage) {
        $coverageArgs = @(
            ,'--coverage'
            ,'--coverage-output-format','cobertura'
        )
        $mtpArgs += $coverageArgs + @(
            ,'--coverage-settings',"$PSScriptRoot/test.runsettings"
        )
        $tunitArgs += $coverageArgs
    }

    $testProjects = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'test') -Recurse -Filter '*.csproj')
    $nonTUnitProjects = @(
        foreach ($testProject in $testProjects) {
            $isTestProject = (& $dotnet msbuild $testProject.FullName -getProperty:IsTestProject -nologo).Trim()
            if ($isTestProject -eq 'true' -and -not (Select-String -LiteralPath $testProject.FullName -Pattern 'PackageReference Include="TUnit.Engine"' -Quiet)) {
                $testProject
            }
        }
    )
    foreach ($testProject in $nonTUnitProjects) {
        & $dotnet test $testProject.FullName `
            --no-build `
            -c $Configuration `
            -bl:"$testBinLog" `
            -- `
            --filter-not-trait 'TestCategory=FailsInCloudTest' `
            @mtpArgs `
            @dumpSwitches `
            @extraArgs
        if ($LASTEXITCODE -ne 0) { $failedTests += 1 }
    }

    $tunitProjects = @($testProjects | Where-Object { Select-String -LiteralPath $_.FullName -Pattern 'PackageReference Include="TUnit.Engine"' -Quiet })
    foreach ($project in $tunitProjects) {
        $projectName = $project.BaseName
        Write-Host "Running TUnit project '$($project.FullName)'." -ForegroundColor Cyan
        $frameworkInfo = (& $dotnet msbuild $project.FullName -getProperty:TargetFrameworks -getProperty:TargetFramework -nologo | ConvertFrom-Json).Properties
        $frameworks = @($frameworkInfo.TargetFrameworks, $frameworkInfo.TargetFramework) | Where-Object { $_ } | ForEach-Object { $_ -split ';' } | Select-Object -Unique
        foreach ($framework in $frameworks) {
            if ($framework -eq 'net472' -and -not $IsWindows) { continue }

            $testAssemblyName = if ($framework -eq 'net472') { "$projectName.exe" } else { "$projectName.dll" }
            $frameworkOutput = Join-Path $RepoRoot "bin/$projectName/$Configuration/$framework"
            $testAssemblies = @(
                Get-ChildItem -Path $frameworkOutput -Recurse -File -Filter $testAssemblyName -ErrorAction SilentlyContinue |
                    Where-Object { $_.FullName -notmatch '[\\/](native|nativeaot|publish)[\\/]' }
            )
            if ($testAssemblies.Count -ne 1) {
                Write-Error "Expected exactly one IL TUnit test assembly for $projectName $framework under '$frameworkOutput', but found $($testAssemblies.Count)."
                $failedTests += 1
                continue
            }

            $ilRunArgs = @($tunitArgs) + @('--report-trx-filename', "${projectName}_${framework}_IL_{arch}.trx")
            if (-not $NoCoverage) {
                $ilRunArgs += @('--coverage-output', "${projectName}_${framework}_IL.cobertura.xml")
            }

            Write-Host "Running IL TUnit tests for $projectName $framework from '$($testAssemblies[0].FullName)'." -ForegroundColor Cyan
            if ($framework -eq 'net472') {
                & $testAssemblies[0].FullName `
                    '--treenode-filter=/*/*/*/*[TestCategory!=FailsInCloudTest]' `
                    @ilRunArgs `
                    @dumpSwitches `
                    @extraArgs
            } else {
                & $dotnet $testAssemblies[0].FullName `
                    '--treenode-filter=/*/*/*/*[TestCategory!=FailsInCloudTest]' `
                    @ilRunArgs `
                    @dumpSwitches `
                    @extraArgs
            }
            if ($LASTEXITCODE -ne 0) { $failedTests += 1 }
        }
    }

    if ($IncludeNativeAOT) {
        $projectName = 'Nerdbank.JsonRpc.Tests'
        $framework = 'net10.0'
        $testExecutableName = if ($IsMacOS -or $IsLinux) { $projectName } else { "$projectName.exe" }
        $nativeAotExecutables = @(
            Get-ChildItem -Path (Join-Path $RepoRoot "bin/$projectName/$Configuration/$framework/*/publish/$testExecutableName") -File -ErrorAction SilentlyContinue
        )
        if ($nativeAotExecutables.Count -ne 1) {
            Write-Error "Expected exactly one NativeAOT test executable for $projectName $framework, but found $($nativeAotExecutables.Count)."
            $failedTests += 1
        } else {
            $nativeAotArgs = @($tunitArgs) + @('--report-trx-filename', "${projectName}_${framework}_NativeAOT_{arch}.trx")
            if (-not $NoCoverage) {
                $nativeAotArgs += @('--coverage-output', "${projectName}_${framework}_NativeAOT.cobertura.xml")
            }
            if ($IsWindows) {
                $nativeAotArgs += $dumpSwitches
            }

            Write-Host "Running NativeAOT tests for $framework from '$($nativeAotExecutables[0].FullName)'." -ForegroundColor Cyan
            & $nativeAotExecutables[0].FullName '--treenode-filter=/*/*/*/*[TestCategory!=FailsInCloudTest]' @nativeAotArgs @extraArgs
            if ($LASTEXITCODE -ne 0) { $failedTests += 1 }
        }
    }
    $trxFiles = Get-ChildItem -Recurse -Path $testLogs\*.trx
} else {
    $testDiagLog = Join-Path $ArtifactStagingFolder (Join-Path test_logs diag.log)
    $coverageArgs = @()
    if (-not $NoCoverage) {
        $coverageArgs = @(
            ,'--collect','Code Coverage;Format=cobertura'
            ,'--settings',"$PSScriptRoot/test.runsettings"
        )
    }

    & $dotnet test $RepoRoot `
        --no-build `
        -c $Configuration `
        --filter "TestCategory!=FailsInCloudTest" `
        --blame-hang-timeout 60s `
        --blame-crash `
        -bl:"$testBinLog" `
        --diag "$testDiagLog;TraceLevel=info" `
        --logger trx `
        @coverageArgs `
        @extraArgs
    if ($LASTEXITCODE -ne 0) { $failedTests += 1 }

    $trxFiles = Get-ChildItem -Recurse -Path $RepoRoot\test\*.trx
}

$unknownCounter = 0
$trxFiles |% {
  New-Item $testLogs -ItemType Directory -Force | Out-Null
  if (!($_.FullName.StartsWith($testLogs, [StringComparison]::OrdinalIgnoreCase))) {
    Copy-Item $_ -Destination $testLogs
  }

  if ($PublishResults) {
    $x = [xml](Get-Content -LiteralPath $_)
    $runTitle = $null
    if ($x.TestRun.TestDefinitions -and $x.TestRun.TestDefinitions.GetElementsByTagName('UnitTest')) {
      $storage = $x.TestRun.TestDefinitions.GetElementsByTagName('UnitTest')[0].storage -replace '\\','/'
      if ($storage -match '/(?<tfm>net[^/]+)/(?:(?<rid>[^/]+)/)?(?<lib>[^/]+)\.(dll|exe)$') {
        if ($matches.rid) {
          $runTitle = "$($matches.lib) ($($matches.tfm), $($matches.rid), $Agent)"
        } else {
          $runTitle = "$($matches.lib) ($($matches.tfm)$x86RunTitleSuffix, $Agent)"
        }
      }
    }
    if (!$runTitle) {
      $unknownCounter += 1;
      $runTitle = "unknown$unknownCounter ($Agent$x86RunTitleSuffix)";
    }

    Write-Host "##vso[results.publish type=VSTest;runTitle=$runTitle;publishRunAttachments=true;resultFiles=$_;failTaskOnFailedTests=true;testRunSystem=VSTS - PTR;]"
  }
}

if ($failedTests -ne 0) {
    exit $failedTests
}
