# Gera, a partir dos arquivos do jogo do usuário, o ícone e a arte do launcher/splash do app Android.
# A saída (src/Celeste.Android/GameArt) fica fora do git: é arte do jogo, não pode ser redistribuída.
# Uso: scripts\generate-game-art.ps1 [-GameDir <pasta do jogo>] [-Logo <png do logo>]
#   -Logo  padrão: art\logo.png (opcional; sem ele o launcher escreve o nome em texto)
param([string]$GameDir, [string]$Logo)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
if (-not $GameDir) { $GameDir = Join-Path $root 'Celeste' }
$splash = Join-Path $GameDir 'Content\Graphics\SplashScreen.png'
if (-not (Test-Path $splash)) { throw "Não encontrei $splash" }

$out = Join-Path $root 'src\Celeste.Android\GameArt\res'
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue

function Save-Scaled([System.Drawing.Image]$img, [System.Drawing.Rectangle]$src, [int]$w, [int]$h, [string]$path, [int]$offsetY = 0, [string]$format = 'png') {
	New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
	$bmp = New-Object System.Drawing.Bitmap $w, $h
	$g = [System.Drawing.Graphics]::FromImage($bmp)
	$g.InterpolationMode = 'HighQualityBicubic'
	$g.PixelOffsetMode = 'HighQuality'
	$g.DrawImage($img, (New-Object System.Drawing.Rectangle 0, $offsetY, $w, ($h - $offsetY)), $src, 'Pixel')
	if ($format -eq 'jpg') {
		$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
		$params = New-Object System.Drawing.Imaging.EncoderParameters 1
		$params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 88L
		$bmp.Save($path, $codec, $params)
	} else {
		$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
	}
	$g.Dispose(); $bmp.Dispose()
}

$img = [System.Drawing.Image]::FromFile($splash)
try {
	# Ícone adaptativo (camada da frente, 108dp): morango com asas + Madeline.
	# A arte encosta no topo da imagem, então a camada começa 1/6 abaixo e o fundo (cor do céu) completa.
	$iconSrc = New-Object System.Drawing.Rectangle 580, 0, 720, 600
	$densities = @{ 'mdpi' = 108; 'hdpi' = 162; 'xhdpi' = 216; 'xxhdpi' = 324; 'xxxhdpi' = 432 }
	foreach ($d in $densities.Keys) {
		$px = $densities[$d]
		Save-Scaled $img $iconSrc $px $px "$out\mipmap-$d\ic_celeste_fg.png" ([int]($px / 6))
		# Ícone "legado" (48dp) para launchers sem suporte a ícone adaptativo.
		$legacy = [int]($px * 48 / 108)
		Save-Scaled $img (New-Object System.Drawing.Rectangle 640, 40, 640, 640) $legacy $legacy "$out\mipmap-$d\ic_celeste.png"
	}
	New-Item -ItemType Directory -Force "$out\mipmap-anydpi-v26" | Out-Null
	@'
<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
	<background android:drawable="@color/celeste_sky" />
	<foreground android:drawable="@mipmap/ic_celeste_fg" />
</adaptive-icon>
'@ | Set-Content "$out\mipmap-anydpi-v26\ic_celeste.xml" -Encoding utf8

	# Arte de fundo do launcher/splash (1920x1080, JPEG).
	Save-Scaled $img (New-Object System.Drawing.Rectangle 0, 0, $img.Width, $img.Height) 1920 1080 "$out\drawable-nodpi\celeste_art.jpg" 0 'jpg'
}
finally {
	$img.Dispose()
}

if (-not $Logo) { $Logo = Join-Path $root 'art\logo.png' }
if (Test-Path $Logo) {
	$logoImg = [System.Drawing.Image]::FromFile((Resolve-Path $Logo))
	try {
		$w = [Math]::Min(720, $logoImg.Width)
		$h = [int]($logoImg.Height * $w / $logoImg.Width)
		Save-Scaled $logoImg (New-Object System.Drawing.Rectangle 0, 0, $logoImg.Width, $logoImg.Height) $w $h "$out\drawable-nodpi\celeste_logo.png"
	}
	finally {
		$logoImg.Dispose()
	}
}
Get-ChildItem $out -Recurse -File | ForEach-Object { $_.FullName.Replace("$out\", '') }
