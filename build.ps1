param(
    [switch]$Install,
    [switch]$Package,
    [string]$Version = "1.0.0"
)

$name = "Flow.Launcher.Plugin.SpeedTest"
$pluginsDir = "$env:APPDATA\FlowLauncher\Plugins"
$installPath = "$pluginsDir\$name"

if (-not $Install -and -not $Package) {
    Write-Host "`nBuild Options:" -ForegroundColor Cyan
    Write-Host "1. Build & Install"
    Write-Host "2. Package for Release`n"
    
    $opt = Read-Host "Pick one (1/2)"
    
    if ($opt -eq "2") {
        $Package = $true
    } else {
        $Install = $true
    }
}

Write-Host "`nBuilding..." -ForegroundColor Yellow
dotnet build -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed" -ForegroundColor Red
    exit 1
}

Write-Host "Build ok" -ForegroundColor Green

if ($Package) {
    $temp = ".\package"
    $rootName = "Speed Test-$Version"
    $zip = "$name-$Version.zip"
    
    Write-Host "Creating package..." -ForegroundColor Yellow
    
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
    New-Item -ItemType Directory -Path $temp | Out-Null

    $packageRoot = Join-Path $temp $rootName
    New-Item -ItemType Directory -Path $packageRoot | Out-Null

    $dllPath = Get-ChildItem ".\bin\Release" -Recurse -Filter "Flow.Launcher.Plugin.SpeedTest.dll" | Select-Object -First 1
    Copy-Item $dllPath.FullName $packageRoot -Force
    Copy-Item ".\plugin.json" $packageRoot -Force
    Copy-Item ".\icon-light.png" $packageRoot -Force
    Copy-Item ".\icon-dark.png" $packageRoot -Force
    
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$temp\*" -DestinationPath $zip -Force
    Remove-Item $temp -Recurse -Force
    
    Write-Host "Package ready: $zip" -ForegroundColor Green
}

elseif ($Install) {
    Write-Host "Installing..." -ForegroundColor Yellow
    
    $proc = Get-Process "Flow.Launcher" -ErrorAction SilentlyContinue
    if ($proc) {
        Stop-Process -Name "Flow.Launcher" -Force
        Start-Sleep 2
    }
    
    if (Test-Path $installPath) {
        Remove-Item $installPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $installPath -Force | Out-Null
    
    $dllPath = Get-ChildItem ".\bin\Release" -Recurse -Filter "Flow.Launcher.Plugin.SpeedTest.dll" | Select-Object -First 1
    Copy-Item $dllPath.FullName $installPath -Force
    Copy-Item ".\plugin.json" $installPath -Force
    Copy-Item ".\icon-light.png" $installPath -Force
    Copy-Item ".\icon-dark.png" $installPath -Force
    
    Write-Host "Installed to $installPath" -ForegroundColor Green
    Write-Host "Restart Flow Launcher" -ForegroundColor Cyan
}
