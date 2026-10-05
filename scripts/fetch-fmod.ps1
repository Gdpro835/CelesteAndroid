# Copia os tres arquivos do FMOD Engine 1.10.14 (Android) para fmod/libs/android-arm64.
#
# O FMOD é proprietário: o pacote é baixado de fmod.com (conta gratuita → Download →
# versão 1.10.14 → FMOD Studio API → Android) e este script coloca os arquivos no lugar,
# sem depender de saber os caminhos dentro do .tar.gz (ver docs/BUILDING.md, seção 3).
#
# Uso: scripts\fetch-fmod.ps1 -Archive "$env:USERPROFILE\Downloads\fmodstudioapi11014android.tar.gz"
param(
	[Parameter(Mandatory = $true)][string]$Archive,
	[string]$Dest
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Dest) { $Dest = Join-Path $root 'fmod\libs\android-arm64' }

if (-not (Test-Path $Archive)) { throw "Arquivo não encontrado: $Archive" }
$tmp = Join-Path $env:TEMP ('fmod-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null

try {
	Write-Host "Extraindo $Archive..."
	& tar.exe -xf $Archive -C $tmp
	if ($LASTEXITCODE -ne 0) { throw "tar falhou ($LASTEXITCODE). O tar.exe do Windows 10+ abre .tar.gz e .zip." }

	# Só o arm64-v8a interessa (RuntimeIdentifier android-arm64): o pacote traz outras ABIs também.
	$wanted = @(
		@{ Name = 'libfmod.so';       Arm64 = $true;  Kind = 'so' },
		@{ Name = 'libfmodstudio.so'; Arm64 = $true;  Kind = 'so' },
		@{ Name = 'fmod.jar';         Arm64 = $false; Kind = 'jar' }
	)
	New-Item -ItemType Directory -Force $Dest | Out-Null

	foreach ($item in $wanted) {
		$found = Get-ChildItem $tmp -Recurse -File -Filter $item.Name |
			Where-Object { -not $item.Arm64 -or $_.FullName -match 'arm64' } |
			Select-Object -First 1
		if (-not $found) {
			throw "Não achei $($item.Name) no pacote. Confira se é o pacote Android do FMOD Engine 1.10.x (o .tar.gz tem api/lowlevel e api/studio)."
		}
		if ($found.Length -le 0) { throw "$($found.FullName) está vazio." }

		# .so tem que ser aarch64 (ELF e_machine = 0xB7): arm32/x86 não roda no aparelho.
		if ($item.Kind -eq 'so') {
			$head = [IO.File]::ReadAllBytes($found.FullName)[0..19]
			$isElf = $head[0] -eq 0x7F -and $head[1] -eq 0x45 -and $head[2] -eq 0x4C -and $head[3] -eq 0x46
			if (-not $isElf) { throw "$($found.FullName) não é um ELF (.so)." }
			if ($head[18] -ne 0xB7) { throw "$($found.FullName) não é aarch64 (e_machine=$('0x{0:X2}' -f $head[18])). Baixe o pacote Android." }
		}

		Copy-Item $found.FullName (Join-Path $Dest $item.Name) -Force
		Write-Host ("  {0}  <-  {1}" -f $item.Name, $found.FullName.Replace($tmp, '').TrimStart('\'))
	}

	Get-ChildItem $Dest | Select-Object Name, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB) } }
	Write-Host ''
	Write-Host "Pronto: $Dest" -ForegroundColor Green
	Write-Host 'Para o CI: guarde este mesmo pacote (o .tar.gz original serve) no repositório privado de assets'
	Write-Host 'e aponte o segredo FMOD_ANDROID_URL para ele (docs/ci/README.md).'
}
finally {
	Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
