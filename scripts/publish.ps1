param(
    [switch]$SelfContained,
    [string]$OutputDirectory = "dist\win-x64"
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "dist"))
$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
}

$distPrefix = $distRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith($distPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish output must be a child directory of $distRoot"
}

Push-Location $projectRoot
try {
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify.ps1
    if ($LASTEXITCODE -ne 0) { throw "Verification failed." }

    if (Test-Path -LiteralPath $outputPath) {
        $resolvedOutput = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $outputPath).Path)
        if (-not $resolvedOutput.StartsWith($distPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clear an output directory outside dist."
        }
        Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
    }

    $selfContainedValue = $SelfContained.IsPresent.ToString().ToLowerInvariant()
    dotnet publish src\Rfbm.Native\Rfbm.Native.csproj `
        -c Release `
        -r win-x64 `
        --self-contained $selfContainedValue `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $outputPath
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    $nativeExe = Join-Path $outputPath "Rfbm.Native.exe"
    $extensionManifest = Join-Path $outputPath "edge-extension\manifest.json"
    if (-not (Test-Path -LiteralPath $nativeExe) -or
        -not (Test-Path -LiteralPath $extensionManifest)) {
        throw "Published app or bundled Edge extension is missing."
    }

    Write-Host "Published R-fbm to $outputPath" -ForegroundColor Green
}
finally {
    Pop-Location
}
