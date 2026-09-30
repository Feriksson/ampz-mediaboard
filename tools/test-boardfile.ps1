# Prueba end-to-end de los archivos .mboard:
#  1. auto-registro de la extension en HKCU al arrancar
#  2. apertura por argumento de linea de comandos (= lo que hace el doble click)
#  3. que el titulo de la ventana refleje el board abierto

# Rutas DERIVADAS del script, nunca tipeadas: la version anterior apuntaba a la carpeta vieja del
# repo y a un clip en el scratchpad de una sesion ya borrada, y fallaba en cualquier maquina.
$repo  = Split-Path -Parent $PSScriptRoot
$exe   = Join-Path $repo 'bin\Debug\net10.0-windows\AmpzMediaBoard.exe'
$clip  = Join-Path $env:TEMP 'ampz-test-clip.gif'
if (-not (Test-Path $clip)) {
    if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { [Console]::WriteLine('No hay ffmpeg para generar el clip.'); exit 1 }
    ffmpeg -v error -y -f lavfi -i 'testsrc=size=160x120:rate=10' -t 2 $clip
}
$board = Join-Path $env:TEMP 'mi-board-de-prueba.mboard'

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# Borramos la asociacion previa para probar el AUTO-REGISTRO desde cero.
Remove-Item -Path 'HKCU:\Software\Classes\.mboard' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path 'HKCU:\Software\Classes\AmpzMediaBoard.Board.1' -Recurse -Force -ErrorAction SilentlyContinue
[Console]::WriteLine('Asociacion previa borrada del registro')

# Un board de prueba: split horizontal, el clip a la izquierda con markers, vacio a la derecha.
$doc = [ordered]@{
    Type='split'; Orientation='Horizontal'; Ratio=0.65
    First  = [ordered]@{ Type='sector'; Path=$clip; LoopStart=200; LoopEnd=900; LoopEnabled=$true }
    Second = [ordered]@{ Type='sector'; LoopStart=0; LoopEnd=0; LoopEnabled=$true }
}
[System.IO.File]::WriteAllText($board, ($doc | ConvertTo-Json -Depth 10 -Compress), (New-Object System.Text.UTF8Encoding($false)))
[Console]::WriteLine('Board de prueba escrito: ' + (Split-Path $board -Leaf))
[Console]::WriteLine('')

# Simulamos el doble click: el shell invoca "<exe>" "<archivo>"
Start-Process -FilePath $exe -ArgumentList "`"$board`""
Start-Sleep -Seconds 7

$p = Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue
if (-not $p) { [Console]::WriteLine('FALLO: no arranco'); exit 1 }

[Console]::WriteLine('=== 1. APERTURA POR ARGUMENTO (doble click) ===')
[Console]::WriteLine('  titulo de la ventana: ' + $p.MainWindowTitle)
$abrio = $p.MainWindowTitle -like '*mi-board-de-prueba*'
[Console]::WriteLine('  abrio el board pasado por argumento: ' + $(if ($abrio) { 'SI' } else { 'NO' }))

[Console]::WriteLine('')
[Console]::WriteLine('=== 2. AUTO-REGISTRO DE LA EXTENSION ===')
$progId  = (Get-ItemProperty -Path 'HKCU:\Software\Classes\.mboard' -Name '(default)' -ErrorAction SilentlyContinue).'(default)'
$cmd     = (Get-ItemProperty -Path 'HKCU:\Software\Classes\AmpzMediaBoard.Board.1\shell\open\command' -Name '(default)' -ErrorAction SilentlyContinue).'(default)'
$icono   = (Get-ItemProperty -Path 'HKCU:\Software\Classes\AmpzMediaBoard.Board.1\DefaultIcon' -Name '(default)' -ErrorAction SilentlyContinue).'(default)'
$desc    = (Get-ItemProperty -Path 'HKCU:\Software\Classes\AmpzMediaBoard.Board.1' -Name '(default)' -ErrorAction SilentlyContinue).'(default)'
[Console]::WriteLine('  .mboard -> ProgId : ' + $progId)
[Console]::WriteLine('  descripcion       : ' + $desc)
[Console]::WriteLine('  icono             : ' + $icono)
[Console]::WriteLine('  comando de apertura: ' + $cmd)

[Console]::WriteLine('')
[Console]::WriteLine('=== 3. VERIFICACION DEL COMANDO ===')
$comillas = $cmd -match '"%1"'
[Console]::WriteLine('  el %1 va entre comillas (paths con espacios): ' + $(if ($comillas) { 'SI' } else { 'NO -- se romperia' }))

[Console]::WriteLine('')
[Console]::WriteLine('=== VEREDICTO ===')
$ok = $abrio -and ($progId -eq 'AmpzMediaBoard.Board.1') -and $comillas
[Console]::WriteLine('  ' + $(if ($ok) { 'TODO OK' } else { 'HAY ALGO MAL' }))

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
