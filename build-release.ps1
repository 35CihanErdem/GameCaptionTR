<#
    GameCaptionTR — Yayına hazır tek dosya EXE üretir.

    Adımlar:
      1) Uygulamayı self-contained derler (.NET kurulu olmayan PC'lerde de çalışır).
      2) Ana assembly'yi Obfuscar ile obfuscate eder (kod okunmasını zorlaştırır).
      3) Obfuscate edilmiş assembly'yi tek bir .exe içinde paketler.

    Sonuç:  dist\GameCaptionTR.exe   (dağıtabileceğin tek dosya)

    Kullanım:
      powershell -ExecutionPolicy Bypass -File .\build-release.ps1
#>

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$project = Join-Path $root "GameCaptionTR\GameCaptionTR.csproj"
$rid = "win-x64"
$configuration = "Release"
$tfmGuess = "net8.0-windows10.0.19041.0"

Write-Host "==> Eski çıktı temizleniyor..." -ForegroundColor Cyan
Get-Process -Name GameCaptionTR -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
Remove-Item -Recurse -Force (Join-Path $root "dist"), (Join-Path $root "obf_in"), (Join-Path $root "obf_out") -ErrorAction SilentlyContinue

Write-Host "==> Derleniyor (self-contained)..." -ForegroundColor Cyan
dotnet build $project -c $configuration -r $rid --self-contained true -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Derleme başarısız." }

$binDir = Join-Path $root "GameCaptionTR\bin\$configuration\$tfmGuess\$rid"
$mainDll = Join-Path $binDir "GameCaptionTR.dll"
if (-not (Test-Path $mainDll)) {
    # TFM klasör adı farklıysa otomatik bul
    $found = Get-ChildItem (Join-Path $root "GameCaptionTR\bin\$configuration") -Recurse -Filter "GameCaptionTR.dll" |
             Where-Object { $_.FullName -like "*\$rid\*" } | Select-Object -First 1
    if ($null -eq $found) { throw "GameCaptionTR.dll bulunamadı." }
    $mainDll = $found.FullName
    $binDir = Split-Path $mainDll -Parent
}

Write-Host "==> Obfuscation uygulanıyor..." -ForegroundColor Cyan
$obfIn = Join-Path $root "obf_in"
$obfOut = Join-Path $root "obf_out"
New-Item -ItemType Directory -Force -Path $obfIn, $obfOut | Out-Null
Copy-Item $mainDll (Join-Path $obfIn "GameCaptionTR.dll") -Force

$toolsPath = Join-Path $env:USERPROFILE ".dotnet\tools"
$env:Path = "$env:Path;$toolsPath"
obfuscar.console (Join-Path $root "Obfuscar.xml")
if ($LASTEXITCODE -ne 0) { throw "Obfuscation başarısız." }

Write-Host "==> Obfuscate edilmiş assembly geri yazılıyor..." -ForegroundColor Cyan
Copy-Item (Join-Path $obfOut "GameCaptionTR.dll") $mainDll -Force

Write-Host "==> Tek dosya EXE paketleniyor..." -ForegroundColor Cyan
dotnet publish $project -c $configuration -r $rid --self-contained true --no-build `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -o (Join-Path $root "dist")
if ($LASTEXITCODE -ne 0) { throw "Paketleme başarısız." }

Remove-Item -Recurse -Force $obfIn, $obfOut -ErrorAction SilentlyContinue

$exe = Join-Path $root "dist\GameCaptionTR.exe"
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "TAMAM. Dagitima hazir dosya:" -ForegroundColor Green
Write-Host ("   {0}  ({1} MB)" -f $exe, $size) -ForegroundColor Green
Write-Host "Bu tek .exe dosyasini paylasman yeterli; kullanicida .NET kurulu olmasina gerek yok." -ForegroundColor Green
