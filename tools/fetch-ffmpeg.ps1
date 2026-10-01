# Baja ffmpeg (build LGPL, FIJADO) a third_party\ffmpeg\ para el loop ida y vuelta (ping-pong).
#
#   powershell -File tools/fetch-ffmpeg.ps1          (no hace nada si ya esta)
#   powershell -File tools/fetch-ffmpeg.ps1 -Force   (lo vuelve a bajar)
#
# Por que asi, y no de otra forma:
#   - NUNCA se commitea el binario (134 MB): third_party/ esta en .gitignore. Este script es la
#     unica forma de conseguirlo, y build-installer.ps1 lo corre solo si falta.
#   - Build LGPL, no GPL: la app no es GPL y no puede distribuir un ffmpeg GPL (gyan.dev solo
#     ofrece GPL). El precio: no trae libx264. No hace falta: el ping-pong usa MJPEG (intra-only),
#     ver Media/PingPongMath.cs.
#   - Build ESTATICO (un solo ffmpeg.exe, sin DLLs): es mas chico que el "shared" (134 MB contra
#     ~167 MB de exe + 7 DLLs) y no hay DLLs que puedan chocar con nada.
#   - FIJADO a un autobuild con fecha de BtbN (no "latest", que cambia todos los dias) y
#     verificado por SHA256: el binario que se prueba es el que se distribuye. BtbN conserva los
#     autobuilds con fecha por mucho tiempo (hay de 2024), pero si algun dia este desaparece, el
#     script falla CLARO: se actualizan URL + hash juntos, a mano, y se vuelve a probar
#     (tools/PingPongProbe).
#   - Se queda SOLO con ffmpeg.exe y el LICENSE; se escribe un NOTICE con la version exacta y
#     donde esta el codigo fuente (lo que pide la LGPL al redistribuir). El .csproj copia los tres
#     a <output>\ffmpeg\ y el instalador los empaqueta.
#
# Sale 0 si quedo instalado, 1 si fallo.

param([switch]$Force)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # la barra de progreso de PS 5.1 hace la descarga 10x mas lenta

$Version = 'n8.1.3-9-g29e619e767'
$Url     = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-30-13-08/ffmpeg-n8.1.3-9-g29e619e767-win64-lgpl-8.1.zip'
$Sha256  = '4a7642b2264c03e8a0ce8a3825b933ee5580656f45695a086fe7e294045ffc0a'

$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root 'third_party\ffmpeg'
$exe  = Join-Path $dest 'ffmpeg.exe'
$stamp = Join-Path $dest 'VERSION.txt'

if (-not $Force -and (Test-Path $exe) -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $Version)) {
    Write-Host "ffmpeg $Version ya esta en $dest"
    exit 0
}

# PS 5.1 negocia TLS 1.0 por defecto y GitHub lo rechaza.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$zip = Join-Path $env:TEMP "ampz-ffmpeg-$Version.zip"
try {
    Write-Host "Bajando $Url"
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing

    # SHA256 por .NET y no con Get-FileHash: ese cmdlet vive en un modulo que no siempre carga
    # (visto: "no se reconoce" al correr desde Git Bash con otro PSModulePath).
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($zip)
    try { $hash = -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($hash -ne $Sha256) {
        Write-Host "FALLO: el SHA256 no coincide. Esperado $Sha256, bajado $hash."
        Write-Host 'No se instala nada: un binario que no es el probado no se distribuye.'
        exit 1
    }

    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    New-Item -ItemType Directory -Path $dest | Out-Null

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) {
            $target = $null
            if ($entry.FullName -like '*/bin/ffmpeg.exe') { $target = 'ffmpeg.exe' }
            elseif ($entry.FullName -like '*/LICENSE.txt') { $target = 'LICENSE.txt' }
            if ($target) { [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $dest $target), $true) }
        }
    } finally { $archive.Dispose() }

    if (-not (Test-Path $exe)) { Write-Host 'FALLO: el zip no traia bin/ffmpeg.exe'; exit 1 }

    $notice = @"
ffmpeg $Version (build LGPL estatico para win64)

Ampz MediaBoard usa este ffmpeg como un PROGRAMA APARTE (se ejecuta como proceso, no se enlaza)
para generar el archivo del loop "ida y vuelta". Se distribuye sin modificar.

FFmpeg es software libre. Este build esta configurado con --enable-version3, asi que su
licencia es la GNU Lesser General Public License VERSION 3 (ver
LICENSE.txt en esta misma carpeta). No incluye componentes GPL ni nonfree (sin --enable-gpl
ni --enable-nonfree: verificable con "ffmpeg -version").

Codigo fuente de esta version exacta:
  https://github.com/FFmpeg/FFmpeg/tree/$($Version.Split('-')[-1].Substring(1))
Scripts con los que se compilo el build (BtbN/FFmpeg-Builds):
  https://github.com/BtbN/FFmpeg-Builds
Binario original:
  $Url
  SHA256 del zip: $Sha256
"@
    [System.IO.File]::WriteAllText((Join-Path $dest 'NOTICE.txt'), $notice, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText($stamp, $Version)

    & $exe -hide_banner -version | Select-Object -First 1
    Write-Host "OK: ffmpeg en $dest"
    exit 0
}
finally {
    Remove-Item $zip -ErrorAction SilentlyContinue
}
