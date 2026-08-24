param(
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
Push-Location $projectRoot
try {
    if (-not $NoRestore) {
        dotnet restore R-fbm.sln
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }
    }

    dotnet build R-fbm.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed." }

    $runners = @(
        "tests\Rfbm.Core.Tests\Rfbm.Core.Tests.csproj",
        "tests\Rfbm.Data.Tests\Rfbm.Data.Tests.csproj",
        "tests\Rfbm.BrowserBridge.Tests\Rfbm.BrowserBridge.Tests.csproj",
        "tests\Rfbm.Collection.Tests\Rfbm.Collection.Tests.csproj",
        "tests\Rfbm.Native.Tests\Rfbm.Native.Tests.csproj"
    )
    foreach ($runner in $runners) {
        dotnet run --project $runner -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "Test runner failed: $runner" }
    }

    node --check extension\edge\content.js
    if ($LASTEXITCODE -ne 0) { throw "Edge content script syntax check failed." }
    node --check extension\edge\background.js
    if ($LASTEXITCODE -ne 0) { throw "Edge background script syntax check failed." }

    Write-Host "R-fbm verification passed." -ForegroundColor Green
}
finally {
    Pop-Location
}
