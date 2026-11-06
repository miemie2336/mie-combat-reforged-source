$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The 64-bit .NET Framework compiler was not found. Install .NET Framework 4.8.'
}
$buildDir = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
$output = Join-Path $buildDir 'PBZSuiteHost.fixed.exe'
$sourceFiles = 'PBZSuiteHost.cs', 'PerilousAudioHost.cs', 'WasapiOutput.cs' |
    ForEach-Object { Join-Path $PSScriptRoot $_ }
$compilerArgs = @(
    '/nologo'
    '/target:winexe'
    '/main:PBZSuiteHost'
    '/define:WASAPI'
    '/optimize-'
    '/platform:x64'
    "/out:$output"
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    '/reference:System.Web.Extensions.dll'
)
& $compiler @compilerArgs @sourceFiles
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed ($LASTEXITCODE)." }
$packagedName = Join-Path $buildDir 'PBZSuiteHost.exe'
Copy-Item -LiteralPath $output -Destination $packagedName -Force
Write-Output "Built $packagedName"

