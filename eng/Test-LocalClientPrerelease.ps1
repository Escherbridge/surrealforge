[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+-[0-9A-Za-z][0-9A-Za-z.-]*$')]
    [string]$Version = '0.4.0-authscope.1',
    [switch]$KeepSmokeWorkspace
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-DotNet {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Write-Utf8File {
    param(
        [string]$Path,
        [string]$Content
    )

    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $repositoryRoot 'artifacts/local-prerelease'
$packageName = "SurrealForge.Client.$Version.nupkg"
$packagePath = Join-Path $artifactDirectory $packageName

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
Invoke-DotNet @(
    'pack',
    (Join-Path $repositoryRoot 'src/SurrealForge.Client/SurrealForge.Client.csproj'),
    '--configuration', 'Release',
    '--output', $artifactDirectory,
    "-p:Version=$Version"
)

if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "Expected package '$packagePath' was not produced."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entryNames = $archive.Entries.FullName
    foreach ($requiredEntry in @('lib/net10.0/SurrealForge.Client.dll', 'lib/netstandard2.0/SurrealForge.Client.dll')) {
        if ($entryNames -notcontains $requiredEntry) {
            throw "Package is missing required asset '$requiredEntry'."
        }
    }

    $nuspecEntry = $archive.Entries | Where-Object { $_.FullName -match '\.nuspec$' } | Select-Object -First 1
    if ($null -eq $nuspecEntry) {
        throw 'Package is missing its nuspec.'
    }

    $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
    try {
        [xml]$nuspec = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }

    if ($nuspec.package.metadata.id -ne 'SurrealForge.Client' -or $nuspec.package.metadata.version -ne $Version) {
        throw "Package metadata did not resolve to SurrealForge.Client $Version."
    }
}
finally {
    $archive.Dispose()
}

$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) "surrealforge-client-smoke-$([Guid]::NewGuid().ToString('N'))"
$feedDirectory = Join-Path $smokeRoot 'feed'
$packageCache = Join-Path $smokeRoot 'packages'
$projectPath = Join-Path $smokeRoot 'Consumer.csproj'
$programPath = Join-Path $smokeRoot 'Program.cs'
$nugetConfigPath = Join-Path $smokeRoot 'NuGet.Config'
$priorNuGetPackages = $env:NUGET_PACKAGES

try {
    New-Item -ItemType Directory -Force -Path $feedDirectory, $packageCache | Out-Null
    Copy-Item -LiteralPath $packagePath -Destination $feedDirectory

    $escapedFeed = [System.Security.SecurityElement]::Escape($feedDirectory)
    Write-Utf8File -Path $nugetConfigPath -Content @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="isolated-local-feed" value="$escapedFeed" />
  </packageSources>
</configuration>
"@

    Write-Utf8File -Path $projectPath -Content @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SurrealForge.Client" Version="$Version" />
  </ItemGroup>
</Project>
"@

    Write-Utf8File -Path $programPath -Content @"
using SurrealForge.Client;

var defaults = new SurrealConnectionOptions();
if (defaults.AuthenticationScope != SurrealAuthenticationScope.Root)
    throw new InvalidOperationException("Root must remain the backward-compatible default.");

var databaseUser = new SurrealConnectionOptions
{
    Namespace = "tenant",
    Database = "ledger",
    User = "runtime",
    Password = "not-a-real-secret",
    AuthenticationScope = SurrealAuthenticationScope.Database,
};

if (databaseUser.AuthenticationScope != SurrealAuthenticationScope.Database)
    throw new InvalidOperationException("Database authentication scope was not preserved.");

Console.WriteLine("SurrealForge.Client prerelease consumer smoke passed.");
"@

    $env:NUGET_PACKAGES = $packageCache
    Invoke-DotNet @('restore', $projectPath, '--configfile', $nugetConfigPath, '--packages', $packageCache)

    $assetsPath = Join-Path $smokeRoot 'obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $libraryKey = "SurrealForge.Client/$Version"
    if ($null -eq $assets.libraries.$libraryKey) {
        throw "The consumer assets file does not resolve $libraryKey from the local package."
    }

    $resolvedDll = Join-Path $packageCache "surrealforge.client/$Version/lib/net10.0/SurrealForge.Client.dll"
    if (-not (Test-Path -LiteralPath $resolvedDll -PathType Leaf)) {
        throw "Consumer restore did not materialize '$resolvedDll'."
    }

    Invoke-DotNet @('run', '--project', $projectPath, '--no-restore')
}
finally {
    $env:NUGET_PACKAGES = $priorNuGetPackages
    if (-not $KeepSmokeWorkspace -and (Test-Path -LiteralPath $smokeRoot)) {
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force
    }
}

Write-Host "Local prerelease package: $packagePath"
