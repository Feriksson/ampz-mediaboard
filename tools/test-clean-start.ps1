# Prueba del ARRANQUE LIMPIO:
#  1. abrir el exe pelado da SIEMPRE un board vacio
#  2. la app NO escribe nada en %APPDATA% (ni recrea la carpeta si no existe)
#  3. abrir por argumento (doble click) sigue funcionando
#
# ⚠ La etapa 3 estuvo ROTA y siempre daba "REVISAR": el .mboard y el clip salian de rutas
# hardcodeadas de un scratchpad de sesion que ya no existe. Encima el script terminaba con
# exit 0 pasara lo que pasara, asi que el fallo no frenaba nada. Ahora todo sale de $env:TEMP
# y el script devuelve 0/1 de verdad.
#
# ⚠ El .mboard se genera con ConvertTo-Json, NUNCA a mano: un path de Windows tipeado dentro
# de un JSON lleva los backslashes sin escapar = JSON invalido, la app lo rechaza como board
# corrupto y la prueba mide el rechazo. Mismo gotcha que documenta test-missing.ps1.
#
#   powershell -File tools/test-clean-start.ps1
#
# Sale 0 si pasa, 1 si fallo.

$repo  = Split-Path -Parent $PSScriptRoot
$exe   = Join-Path $repo 'bin\Debug\net10.0-windows\AmpzMediaBoard.exe'
$board = Join-Path $env:TEMP 'board-limpio.mboard'
$datos = Join-Path $env:APPDATA 'AmpzMediaBoard'

if (-not (Test-Path $exe)) {
    Write-Output "FALLO: no existe $exe -- compila primero con dotnet build."
    exit 1
}

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 1000

# Borramos la carpeta de datos ENTERA. Si la app vuelve a crearla, esta escribiendo estado.
Remove-Item -Path $datos -Recurse -Force -ErrorAction SilentlyContinue
Write-Output ('Carpeta de datos borrada: ' + $datos)
Write-Output ('existe antes de arrancar: ' + (Test-Path $datos))
Write-Output ''

# --- 1) exe pelado ---
Start-Process $exe
Start-Sleep -Seconds 6
$p = Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue
if (-not $p) { Write-Output 'FALLO: no arranco'; exit 1 }

Write-Output '=== 1. ARRANQUE SIN ARGUMENTOS ==='
Write-Output ('  titulo: ' + $p.MainWindowTitle)
$vacio = $p.MainWindowTitle -like '*sin guardar*'
Write-Output ('  arranco con board VACIO: ' + $(if ($vacio) { 'SI' } else { 'NO' }))

# Cierre LIMPIO (WM_CLOSE): es el unico que dispararia una escritura de estado si existiera.
$null = $p.CloseMainWindow()
Start-Sleep -Seconds 4
Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

Write-Output ''
Write-Output '=== 2. NO ESCRIBE ESTADO ==='
$recreo = Test-Path $datos
Write-Output ('  recreo %APPDATA%\AmpzMediaBoard al cerrar: ' + $(if ($recreo) { 'SI -- ESCRIBE ESTADO' } else { 'NO' }))
if ($recreo) { Get-ChildItem $datos -Recurse | ForEach-Object { Write-Output ('    ' + $_.Name) } }

# --- 3) apertura por argumento (doble click) ---
# Un sector con un path fantasma alcanza: lo que se prueba es que el ARGUMENTO llegue y el
# board se abra, no que reproduzca. Asi la prueba no necesita ningun clip real.
$doc = [ordered]@{
    Type        = 'sector'
    Path        = 'D:\clips-que-ya-no-estan\take-000.mp4'
    LoopStart   = 100
    LoopEnd     = 800
    LoopEnabled = $true
}
[System.IO.File]::WriteAllText($board, ($doc | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))

Start-Process -FilePath $exe -ArgumentList "`"$board`""
Start-Sleep -Seconds 7
$p2 = Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue

Write-Output ''
Write-Output '=== 3. APERTURA POR ARGUMENTO (doble click) ==='
if ($p2) {
    Write-Output ('  titulo: ' + $p2.MainWindowTitle)
    # ⚠ Si el titulo dice "Ampz MediaBoard" a secas, eso NO es la ventana: es el caption de un
    # MessageBox (board corrupto). Ver "El titulo de la ventana" en el CLAUDE.md.
    $abrio = $p2.MainWindowTitle -like '*board-limpio*'
    Write-Output ('  abrio el archivo: ' + $(if ($abrio) { 'SI' } else { 'NO' }))
} else {
    Write-Output '  no arranco'
    $abrio = $false
}

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Remove-Item $board -Force -ErrorAction SilentlyContinue

Write-Output ''
Write-Output '=== VEREDICTO ==='
if ($vacio -and (-not $recreo) -and $abrio) {
    Write-Output '  ARRANQUE LIMPIO OK - CERO ESTADO OCULTO'
    exit 0
}
Write-Output '  REVISAR'
exit 1
