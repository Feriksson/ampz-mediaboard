# Prueba de regresion del CIERRE RAPIDO con varios videos corriendo.
#
# Bug reportado: cerrar un board con varios clips tardaba segundos y se veia cerrar "video por
# video". Causa: MainWindow.OnClosing -> BoardViewModel.Dispose -> SectorNode.Unload hacia un
# MediaPlayer.Stop() BLOQUEANTE de libvlc (~100-500ms: espera decoder, vout y WASAPI) por
# sector, EN FILA y en el hilo de UI, con la ventana visible y congelada. N sectores = N veces.
# El arreglo: la ventana se esconde ya, los Stop corren en paralelo en otros hilos
# (VlcEngine.Release) y el runtime se libera recien cuando terminaron (VlcEngine.Shutdown).
#
# Mide DOS cosas, desde el CloseMainWindow():
#   (a) cuanto tarda en IRSE la ventana  -> esto es lo que ve el usuario, y lo que se juzga;
#   (b) cuanto tarda en SALIR el proceso -> informativo: con el arreglo es ~el Stop mas lento,
#       no la suma. No se le pone umbral: depende de la maquina y no es lo que se reporto.
#
# ⚠ El .mboard se genera con ConvertTo-Json, NUNCA a mano: un path de Windows tipeado en un
# JSON lleva los backslashes sin escapar -> JSON invalido -> "board corrupto" y la prueba mide
# el rechazo. Ver test-missing.ps1.
#
# ⚠ LoopEnd va FIJO y mayor que LoopStart. Con LoopEnd=0 la app inicializa la zona al clip
# entero en el primer tick -> el board en memoria ya no coincide con el archivo -> al cerrar
# salta "cambios sin guardar" y la prueba mediria un MessageBox en vez del cierre.
#
# El umbral de (a) sale de MEDIR las dos versiones con 8 sectores (2026-09-30), no de ojo:
#   codigo viejo (Stop en fila en el hilo de UI) .. 1030 / 1252 / 1298 ms  -> ROJO
#   codigo nuevo (esconder + Stop en paralelo) ....   59 /   88 /  141 ms  -> VERDE
# 400 ms queda con ~3x de aire sobre el peor verde y ~2.5x por debajo del mejor rojo. Con menos
# sectores el rojo se acerca al umbral (es N x ~150ms): por eso el default es 8, el tope de uso.
#
#   powershell -File tools/test-close.ps1
#   powershell -File tools/test-close.ps1 -Sectors 8
#
# Sale 0 si pasa, 1 si fallo.

param([int]$Sectors = 8, [int]$ThresholdMs = 400)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'bin\Debug\net10.0-windows\AmpzMediaBoard.exe'
if (-not (Test-Path $exe)) { Write-Host "No esta compilado: $exe"; exit 1 }

# Clip PROPIO, largo y animado en toda la imagen: la prueba necesita que los clips esten
# DECODIFICANDO de verdad al cerrar (un Stop sobre un clip terminado no cuesta nada) y que no
# lleguen al final durante la medicion.
$clip = Join-Path $env:TEMP 'ampz-close-clip.mp4'
if (-not (Test-Path $clip)) {
    if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { Write-Host 'No hay ffmpeg para generar el clip.'; exit 1 }
    ffmpeg -v error -y -f lavfi -i 'mandelbrot=size=640x360:rate=30' -t 60 -pix_fmt yuv420p $clip
    Write-Host "clip generado: $clip"
}

# Arbol balanceado de N sectores, alternando la orientacion por nivel (como se ve un board real).
function New-Tree([int]$n, [int]$depth) {
    if ($n -le 1) {
        # Muteado: la prueba no tiene por que hacer ruido. El audio igual se abre (WASAPI), que
        # es parte de lo que el Stop tiene que desarmar.
        return [ordered]@{ Type = 'sector'; Path = $clip; LoopStart = 0; LoopEnd = 50000; LoopEnabled = $true; Volume = 0; Muted = $true }
    }
    $left = [math]::Floor($n / 2)
    return [ordered]@{
        Type        = 'split'
        Orientation = $(if ($depth % 2 -eq 0) { 'Horizontal' } else { 'Vertical' })
        Ratio       = $left / $n
        First       = New-Tree $left ($depth + 1)
        Second      = New-Tree ($n - $left) ($depth + 1)
    }
}

$board = Join-Path $env:TEMP 'ampz-close.mboard'
[System.IO.File]::WriteAllText($board, ((New-Tree $Sectors 0) | ConvertTo-Json -Depth 30), (New-Object System.Text.UTF8Encoding($false)))

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$p = Start-Process -FilePath $exe -ArgumentList "`"$board`"" -PassThru
$null = $p.Handle  # sin tomar el handle ya, ExitCode queda vacio al terminar

# Se espera a que los clips esten REPRODUCIENDO, midiendo CPU en vez de dormir a ciegas: con N
# clips decodificando el proceso quema mucho mas que un segundo de CPU por segundo de reloj.
$deadline = (Get-Date).AddSeconds(40)
$playing = $false
Start-Sleep -Seconds 3
while ((Get-Date) -lt $deadline) {
    $p.Refresh(); $a = $p.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Seconds 1
    $p.Refresh(); $b = $p.TotalProcessorTime.TotalMilliseconds
    if (($b - $a) -gt 300) { $playing = $true; break }
}
# Un respiro extra: que todos los sectores (no solo los primeros) hayan abierto su clip.
Start-Sleep -Seconds 2
$p.Refresh()

$title = $p.MainWindowTitle
Write-Host "PID $($p.Id) -- titulo: '$title' -- decodificando: $playing"
if ($title -eq 'Ampz MediaBoard') {
    Write-Host 'FALLO: hay un MessageBox arriba (ese es su caption, no el de la ventana). Board corrupto?'
    Stop-Process -Id $p.Id -Force; exit 1
}
if ($title -notlike '*ampz-close*') {
    Write-Host "FALLO: el board no cargo (titulo inesperado)."
    Stop-Process -Id $p.Id -Force; exit 1
}
if (-not $playing) {
    Write-Host 'FALLO: los clips nunca empezaron a decodificar; la medicion no significaria nada.'
    Stop-Process -Id $p.Id -Force; exit 1
}

# ══ La medicion ══
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$null = $p.CloseMainWindow()

$windowGoneMs = -1
$exitMs = -1
while ($sw.ElapsedMilliseconds -lt 20000) {
    $p.Refresh()
    if ($p.HasExited) {
        if ($windowGoneMs -lt 0) { $windowGoneMs = $sw.ElapsedMilliseconds }
        $exitMs = $sw.ElapsedMilliseconds
        break
    }
    # MainWindowHandle es 0 cuando ya no hay ventana principal VISIBLE (escondida o destruida).
    if ($windowGoneMs -lt 0 -and $p.MainWindowHandle -eq [IntPtr]::Zero) { $windowGoneMs = $sw.ElapsedMilliseconds }
    # Si aparece un dialogo, el cierre no es lo que se esta midiendo.
    if ($p.MainWindowTitle -eq 'Ampz MediaBoard') {
        Write-Host 'FALLO: aparecio un MessageBox al cerrar (cambios sin guardar?). El board deberia estar intacto.'
        Stop-Process -Id $p.Id -Force; exit 1
    }
    Start-Sleep -Milliseconds 5
}

if ($exitMs -lt 0) {
    Write-Host "FALLO: el proceso no salio en 20s (ventana ida a los $windowGoneMs ms)."
    Stop-Process -Id $p.Id -Force; exit 1
}

Write-Host ''
Write-Host "=== CIERRE CON $Sectors SECTORES REPRODUCIENDO ==="
Write-Host "  (a) ventana ida en ........ $windowGoneMs ms   (umbral: $ThresholdMs ms)"
Write-Host "  (b) proceso terminado en .. $exitMs ms   (informativo)"
Write-Host "  codigo de salida .......... $($p.ExitCode)"

if ($windowGoneMs -gt $ThresholdMs) {
    Write-Host '  FALLO: la ventana tarda en irse -> los Stop() volvieron a correr en fila en el hilo de UI.'
    exit 1
}
Write-Host '  TODO OK'
exit 0
