#!/usr/bin/env pwsh
# 构建 Android APK。
#
# 依赖：.NET 10 SDK + android 工作负载（dotnet workload install android）、JDK 17+、Android SDK。
# 若系统 dotnet 没有 android 工作负载，会自动回退到用户级 SDK（%USERPROFILE%\.dotnet-cv）。
#
# 用法：
#   pwsh scripts/build-android.ps1                 # Release，产物复制到 publish/
#   pwsh scripts/build-android.ps1 -Debug          # Debug 构建

param(
    [switch]$Debug,
    [string]$DotNet = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$configuration = if ($Debug) { "Debug" } else { "Release" }
$project = Join-Path $root "src/ComicVerse.Android/ComicVerse.Android.csproj"

function Test-AndroidWorkload([string]$exe) {
    if (-not (Test-Path $exe)) { return $false }
    $list = & $exe workload list 2>$null | Out-String
    return $list -match "(?m)^\s*android\s"
}

if (-not $DotNet) {
    $system = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
    $userLocal = Join-Path $env:USERPROFILE ".dotnet-cv\dotnet.exe"
    $DotNet = if (Test-AndroidWorkload $system) { $system }
              elseif (Test-AndroidWorkload $userLocal) { $userLocal }
              else { $system }
}

$env:DOTNET_ROOT = Split-Path -Parent $DotNet
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
if (-not $env:ANDROID_HOME) { $env:ANDROID_HOME = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
if (-not $env:JAVA_HOME) {
    $jdk = Get-ChildItem "C:\Program Files\Eclipse Adoptium" -Directory -ErrorAction SilentlyContinue |
        Where-Object Name -like "jdk-*" | Sort-Object Name -Descending | Select-Object -First 1
    if ($jdk) { $env:JAVA_HOME = $jdk.FullName }
}

Write-Host "dotnet      : $DotNet"
Write-Host "Android SDK : $env:ANDROID_HOME"
Write-Host "JAVA_HOME   : $env:JAVA_HOME"

& $DotNet build $project -c $configuration -f net10.0-android -t:SignAndroidPackage --nologo
if ($LASTEXITCODE -ne 0) { throw "APK 构建失败（退出码 $LASTEXITCODE）" }

$apk = Join-Path $root "src/ComicVerse.Android/bin/$configuration/net10.0-android/com.comicverse.reader-Signed.apk"
if (-not (Test-Path $apk)) { throw "未找到 APK：$apk" }

$target = Join-Path $root "publish/ComicVerse-Android.apk"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
Copy-Item $apk $target -Force
$size = [math]::Round((Get-Item $target).Length / 1MB, 1)
Write-Host "`n完成：$target（$size MB）"
Write-Host "安装： adb install -r `"$target`""
