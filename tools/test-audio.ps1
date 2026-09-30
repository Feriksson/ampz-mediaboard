# Prueba de regresion del AUDIO POR SECTOR: cada MediaPlayer tiene que conservar SU volumen y SU
# silencio, independientes de los demas sectores.
#
# Bug encontrado (2026-09-30): con la salida de audio por defecto de VLC en Windows
# (mmdevice/WASAPI) el volumen y el mute que se le piden a un player se aplican a la SESION de
# audio del PROCESO, que es una sola. Todos los players comparten el mismo control y gana el
# ultimo que escribe: un sector a 80 leia 10, uno sin mute leia Mute=True. Volumen por sector,
# SOLO y volumen general del board quedaban muertos. El arreglo es `--aout=directsound` en
# VlcEngine.Create (cada player con su buffer y su volumen).
#
# Como se mide: un board de 3 sectores con volumenes y mutes DISTINTOS entre si, y la linea
# `audiostate` del DiagLog (SectorNode.DiagAudioState), que LEE lo que VLC dice tener sin escribir
# nada antes, cada ~2 s y despues de que todos los sectores aplicaron el suyo. Con el control
# compartido todos leen el valor del ultimo; con players independientes cada uno lee el suyo.
# ⚠ NO sirve la linea `audio` (ApplyAudio): lee justo DESPUES de escribir, y con el control
#   compartido igual devuelve lo recien puesto — es un verde que no puede ponerse rojo.
#
# Chequeo de cordura de que el audio SIGUE SONANDO: `vlc` distinto de -1 significa que la salida
# de audio existe (VLC devuelve -1 antes de crearla), y `audiostate` solo se escribe con el
# player reproduciendo. (Mirar que plugin de salida cargo el proceso NO sirve: el escaneo de
# VLC carga todos los DLL de plugins, directsound y mmdevice incluidos, con o sin la opcion.)
#
# ⚠ Hace un poco de ruido: un solo sector suena, con un tono de 440 Hz al 8 %. Los otros dos van
#   silenciados, que es justo lo que se verifica.
# ⚠ El .mboard se genera con ConvertTo-Json (backslashes escapados), nunca a mano. LoopEnd FIJO:
#   con LoopEnd=0 el primer tick completa la zona y el board queda "modificado".
#
#   powershell -File tools/test-audio.ps1
#
# Sale 0 si pasa, 1 si fallo.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'bin\Debug\net10.0-windows\AmpzMediaBoard.exe'
if (-not (Test-Path $exe)) { Write-Host "No esta compilado: $exe"; exit 1 }

# Clip PROPIO con pista de AUDIO (los de las otras pruebas no tienen: sin audio VLC no crea la
# salida y no hay volumen que leer). Tres copias con nombre distinto: el DiagLog identifica al
# sector por el nombre del archivo.
$clip = Join-Path $env:TEMP 'ampz-audio-clip.mp4'
if (-not (Test-Path $clip)) {
    if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { Write-Host 'No hay ffmpeg para generar el clip.'; exit 1 }
    ffmpeg -v error -y -f lavfi -i 'testsrc2=size=320x240:rate=25' -f lavfi -i 'sine=frequency=440:sample_rate=48000' `
        -t 60 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest $clip
}
$clips = 1..3 | ForEach-Object {
    $c = Join-Path $env:TEMP "ampz-audio-$_.mp4"
    Copy-Item $clip $c -Force
    $c
}

# Lo que cada sector PIDE. Volumenes y mutes distintos entre todos: si el control fuera
# compartido, no hay forma de que los tres lean lo suyo a la vez.
$esperado = @(
    @{ Clip = $clips[0]; Volume = 20; Muted = $true  },
    @{ Clip = $clips[1]; Volume = 8;  Muted = $false },
    @{ Clip = $clips[2]; Volume = 3;  Muted = $true  }
)
function Sector($e) { [ordered]@{ Type = 'sector'; Path = $e.Clip; LoopStart = 0; LoopEnd = 50000; LoopEnabled = $true; Volume = $e.Volume; Muted = $e.Muted } }
$tree = [ordered]@{
    Type = 'split'; Orientation = 'Horizontal'; Ratio = 0.34
    First = Sector $esperado[0]
    Second = [ordered]@{ Type = 'split'; Orientation = 'Vertical'; Ratio = 0.5; First = Sector $esperado[1]; Second = Sector $esperado[2] }
}
$board = Join-Path $env:TEMP 'ampz-audio.mboard'
[System.IO.File]::WriteAllText($board, ($tree | ConvertTo-Json -Depth 30), (New-Object System.Text.UTF8Encoding($false)))

$diagLog = Join-Path $env:TEMP 'ampz-audio-diag.log'
Remove-Item $diagLog -ErrorAction SilentlyContinue

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$env:AMPZ_DIAG_LOG = $diagLog
$p = Start-Process -FilePath $exe -ArgumentList "`"$board`"" -PassThru
Remove-Item Env:\AMPZ_DIAG_LOG
$null = $p.Handle

# Que abran los tres y pasen varias rondas de readback (una cada ~2 s).
Start-Sleep -Seconds 12
$p.Refresh()
if ($p.HasExited) { Write-Host 'FALLO: el proceso murio'; exit 1 }
if ($p.MainWindowTitle -eq 'Ampz MediaBoard') { Write-Host 'FALLO: hay un MessageBox arriba (board corrupto?)'; Stop-Process -Id $p.Id -Force; exit 1 }

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue

$lineas = if (Test-Path $diagLog) { @(Get-Content $diagLog | Where-Object { $_ -like 'audiostate *' }) } else { @() }
Write-Host ''
Write-Host '=== AUDIO POR SECTOR (readback sin escribir) ==='
Write-Host "  lineas audiostate: $($lineas.Count)"

$fallas = 0
foreach ($e in $esperado) {
    $nombre = Split-Path $e.Clip -Leaf
    $ultima = $lineas | Where-Object { $_ -like "audiostate $nombre *" } | Select-Object -Last 1
    if (-not $ultima) { Write-Host "  [FALLA] $nombre nunca informo estado (no reprodujo?)"; $fallas++; continue }
    if ($ultima -notmatch 'req=(\d+) vlc=(-?\d+) mute=(\w+) vlcmute=(\w+)') { Write-Host "  [FALLA] linea ilegible: $ultima"; $fallas++; continue }
    $req = [int]$Matches[1]; $vlc = [int]$Matches[2]; $mute = $Matches[3]; $vlcMute = $Matches[4]
    Write-Host "  $nombre  pedido: vol=$req mute=$mute   VLC dice: vol=$vlc mute=$vlcMute"
    if ($vlc -lt 0) { Write-Host '    [FALLA] VLC devuelve -1: la salida de audio nunca se creo (el audio no suena)'; $fallas++ }
    elseif ([math]::Abs($vlc - $req) -gt 1) { Write-Host '    [FALLA] el volumen no es el SUYO: el control de volumen es compartido entre players'; $fallas++ }
    if ($vlcMute -ne $mute) { Write-Host '    [FALLA] el silencio no es el SUYO: el mute es compartido entre players'; $fallas++ }
}

Remove-Item $board, $diagLog -ErrorAction SilentlyContinue
$clips | ForEach-Object { Remove-Item $_ -ErrorAction SilentlyContinue }

Write-Host ''
if ($fallas -eq 0) { Write-Host '=== TODO OK ==='; exit 0 }
Write-Host "=== $fallas FALLA(S) ==="
exit 1
