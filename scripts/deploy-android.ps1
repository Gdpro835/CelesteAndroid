# Ciclo de desenvolvimento no aparelho: patcheia o Celeste, compila/instala o APK e envia os arquivos.
# Uso: scripts\deploy-android.ps1 [-Content] [-NoLaunch]
#   -Content   também envia a pasta Content do jogo (1 GB; só na primeira vez ou se mudar)
param(
	[switch]$Content,
	[switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$pkg = 'org.celesteandroid.celeste'
$adb = Join-Path ([Environment]::GetEnvironmentVariable('ANDROID_HOME', 'User')) 'platform-tools\adb.exe'
foreach ($k in 'JAVA_HOME', 'ANDROID_HOME', 'ANDROID_NDK_HOME') { Set-Item "env:$k" ([Environment]::GetEnvironmentVariable($k, 'User')) }

Write-Host '=== patch ===' -ForegroundColor Cyan
dotnet build "$root\src\Celeste.Desktop" -nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'build desktop falhou' }
$patched = & "$root\src\Celeste.Desktop\bin\Debug\net10.0\win-x64\CelesteDesktop.exe" --patch-only | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { throw 'patch falhou' }

Write-Host '=== apk ===' -ForegroundColor Cyan
dotnet build "$root\src\Celeste.Android" -c Debug -nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'build android falhou' }
$apk = (Get-ChildItem "$root\src\Celeste.Android\bin\Debug" -Recurse -Filter *-Signed.apk | Select-Object -First 1).FullName
& $adb install -r $apk | Out-Host
& $adb shell am force-stop $pkg

# /data/local/tmp é legível pelo app; run-as copia para o armazenamento interno (files/).
Write-Host '=== arquivos ===' -ForegroundColor Cyan
$tmp = '/data/local/tmp/celeste'
& $adb shell "rm -rf $tmp && mkdir -p $tmp/patched" | Out-Host
& $adb push $patched "$tmp/patched/" | Out-Host

# Assinatura do módulo de patches que está no APK: sem ela o launcher acharia que o Celeste.dll
# enviado aqui foi gerado por um patch antigo e refaria o patch no aparelho a cada abertura.
$module = Get-ChildItem "$root\src\Celeste.Android.Patches\bin\Debug" -Recurse -Filter 'Celeste.Android.mm.dll' |
	Select-Object -First 1
if (-not $module) { throw 'Celeste.Android.mm.dll não encontrado (compile o Celeste.Android.Patches).' }
$stampFile = Join-Path $env:TEMP 'celeste-patch.stamp'
(Get-FileHash $module.FullName -Algorithm SHA256).Hash.Substring(0, 16) | Set-Content $stampFile -NoNewline -Encoding ASCII
& $adb push $stampFile "$tmp/patched/patch.stamp" | Out-Host
if ($Content) {
	& $adb shell "mkdir -p $tmp/Celeste" | Out-Host
	& $adb push "$root\Celeste\Content" "$tmp/Celeste/" | Out-Host
}
& $adb shell "chmod -R a+rX $tmp && run-as $pkg sh -c 'mkdir -p files && cp -r $tmp/* files/' && rm -rf $tmp" | Out-Host

if (-not $NoLaunch) {
	& $adb logcat -c
	& $adb shell am start -n "$pkg/.LauncherActivity" | Out-Host
}
