# Builds the release APKs into out/.
#
#   out/CelesteAndroid-<version>.apk           public build: no official art, safe to share
#   out/CelesteAndroid-<version>-personal.apk  (-Personal) official icon/logo from your own game files
#                                              (+ -EmbedGame: your game inside the APK) - NEVER share it
#
# Signing: reads %USERPROFILE%\.celeste-android\keystore.env (CELESTE_KEYSTORE, CELESTE_KEYSTORE_ALIAS,
# CELESTE_KEYSTORE_PASS). Without it, the APK is signed with the debug key.
param(
	[switch]$Personal,
	[switch]$EmbedGame
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Celeste.Android\Celeste.Android.csproj'
$out = Join-Path $root 'out'

# --- Preconditions ------------------------------------------------------------------------------
# Check everything the build needs before it starts: otherwise MSBuild fails with an obscure error
# about a file the user doesn't know is needed (or, worse, silently builds an APK without the game).
# Every message says which script produces the missing piece.

function Assert-Exists([string]$what, [string[]]$paths, [string]$hint) {
	$missing = @($paths | Where-Object { $_ -and -not (Test-Path $_) })
	if ($missing.Count -gt 0) {
		throw "$what not found:`n  $($missing -join "`n  ")`n$hint"
	}
}

# The User-level value wins, but a variable already exported in this shell is kept as fallback.
foreach ($k in 'JAVA_HOME', 'ANDROID_HOME', 'ANDROID_NDK_HOME') {
	$value = [Environment]::GetEnvironmentVariable($k, 'User')
	if ($value) { Set-Item "env:$k" $value }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
	throw "dotnet not found on PATH.`n  Install the .NET SDK 10 (winget install Microsoft.DotNet.SDK.10) and the Android workload (dotnet workload install android)."
}

if (-not $env:JAVA_HOME -or -not (Test-Path (Join-Path $env:JAVA_HOME 'bin\java.exe'))) {
	throw "Java (JDK 17) not found.`n  JAVA_HOME is '$env:JAVA_HOME'.`n  Install it and set the variable, e.g.: winget install Microsoft.OpenJDK.17"
}

if (-not $env:ANDROID_HOME -or -not (Test-Path $env:ANDROID_HOME)) {
	throw "Android SDK not found.`n  ANDROID_HOME is '$env:ANDROID_HOME'.`n  Install the SDK (cmdline-tools) and set ANDROID_HOME."
}
elseif (-not (Test-Path (Join-Path $env:ANDROID_HOME 'platforms\android-36'))) {
	Write-Warning "platforms\android-36 not found under $env:ANDROID_HOME: install it with sdkmanager ""platforms;android-36"" ""build-tools;36.0.0""."
}

# Only scripts/build-natives-android.ps1 (not the APK build) needs the NDK; a warning is enough.
if (-not $env:ANDROID_NDK_HOME -or -not (Test-Path $env:ANDROID_NDK_HOME)) {
	Write-Warning "ANDROID_NDK_HOME is '$env:ANDROID_NDK_HOME': needed only to rebuild the native libraries (scripts\build-natives-android.ps1)."
}

# Native libraries the APK links against (they live in the classpath, not in git).
Assert-Exists 'Android native libraries' @(
	(Join-Path $root 'natives\android-arm64\libSDL3.so'),
	(Join-Path $root 'natives\android-arm64\libFNA3D.so'),
	(Join-Path $root 'natives\android-arm64\libFAudio.so')
) '  Build them with: scripts\build-natives-android.ps1'

# FMOD 1.10.14 for Android (proprietary; see docs/BUILDING.md section 3).
Assert-Exists 'FMOD for Android' @(
	(Join-Path $root 'fmod\libs\android-arm64\libfmod.so'),
	(Join-Path $root 'fmod\libs\android-arm64\libfmodstudio.so'),
	(Join-Path $root 'fmod\libs\android-arm64\fmod.jar')
) "  Copy them from the FMOD Engine 1.10.14 Android package (see docs/BUILDING.md)."

if ($EmbedGame) {
	Assert-Exists 'Game files for -EmbedGame' @(
		(Join-Path $root 'Celeste\Celeste.exe'),
		(Join-Path $root 'Celeste\Content')
	) "  -EmbedGame needs your Celeste PC copy (FNA build) in 'Celeste\' (or set CelesteGameDir).`n  Without it the APK would be built without the game."
}

New-Item -ItemType Directory -Force $out | Out-Null
$version = ([xml](Get-Content $project)).Project.PropertyGroup.ApplicationDisplayVersion | Where-Object { $_ } | Select-Object -First 1

$signing = @()
$keyEnv = Join-Path $env:USERPROFILE '.celeste-android\keystore.env'
if (Test-Path $keyEnv) {
	$keys = @{}
	Get-Content $keyEnv | Where-Object { $_ -match '=' } | ForEach-Object { $name, $value = $_ -split '=', 2; $keys[$name.Trim()] = $value.Trim() }
	$missing = @('CELESTE_KEYSTORE', 'CELESTE_KEYSTORE_ALIAS', 'CELESTE_KEYSTORE_PASS') | Where-Object { -not $keys[$_] }
	if ($missing.Count -gt 0) {
		throw "$keyEnv is missing: $($missing -join ', ').`n  Expected lines like CELESTE_KEYSTORE=C:\path\release.keystore."
	}
	if (-not (Test-Path $keys.CELESTE_KEYSTORE)) {
		throw "The signing keystore in ${keyEnv} doesn't exist:`n  $($keys.CELESTE_KEYSTORE)"
	}
	$signing = @(
		"-p:AndroidSigningKeyStore=$($keys.CELESTE_KEYSTORE)",
		"-p:AndroidSigningKeyAlias=$($keys.CELESTE_KEYSTORE_ALIAS)",
		"-p:AndroidSigningStorePass=$($keys.CELESTE_KEYSTORE_PASS)",
		"-p:AndroidSigningKeyPass=$($keys.CELESTE_KEYSTORE_PASS)"
	)
}
else {
	Write-Warning "No release keystore at ${keyEnv}: signing with the debug key.`nKeep the same key across builds, or Android won't let you update without uninstalling (which deletes saves)."
}

# The icon (art\icon.*) goes into every build; key art and logo only into -Personal ones.
& (Join-Path $PSScriptRoot 'generate-game-art.ps1') | Out-Null

if ($Personal) {
	$name = "CelesteAndroid-$version-personal.apk"
	$flags = @('-p:UseGameArt=true')
	if ($EmbedGame) { $flags += '-p:EmbedGame=true' }
}
else {
	$name = "CelesteAndroid-$version.apk"
	$flags = @('-p:UseGameArt=false')
}

$publish = Join-Path $root 'build\publish'
Remove-Item -Recurse -Force $publish -ErrorAction SilentlyContinue
# Always a clean build: incremental builds reuse the intermediate assets, so a public APK built right
# after a personal -EmbedGame one would still contain the game.
Remove-Item -Recurse -Force (Join-Path $root 'src\Celeste.Android\obj\Release'), (Join-Path $root 'src\Celeste.Android\bin\Release') -ErrorAction SilentlyContinue
dotnet publish $project -c Release -o $publish @flags @signing -nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$apk = Get-ChildItem $publish -Filter *-Signed.apk -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $apk) {
	throw "publish succeeded but no *-Signed.apk was produced in $publish.`n  Check the dotnet output above (the signing parameters are a common cause)."
}

if (-not $Personal) {
	# Safety net: a public APK must never contain game files, the key art or the logo.
	Add-Type -AssemblyName System.IO.Compression.FileSystem
	$zip = [IO.Compression.ZipFile]::OpenRead($apk.FullName)
	try {
		$bad = $zip.Entries | Where-Object { $_.FullName -like 'assets/game/*' -or $_.FullName -match 'celeste_art|celeste_logo' }
		if ($bad) { throw "Public APK contains game files/key art/logo ($(@($bad).Count) entries); aborting." }
	}
	finally { $zip.Dispose() }
}
Copy-Item $apk.FullName (Join-Path $out $name) -Force
Get-Item (Join-Path $out $name) | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
