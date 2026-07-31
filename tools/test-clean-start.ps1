# Prueba del ARRANQUE LIMPIO:
#  1. la app NO escribe nada en %APPDATA% (ni recrea la carpeta si no existe)
#  2. abrir el exe pelado da SIEMPRE un board vacio, aunque haya un board.json viejo
#  3. abrir por argumento (doble click) sigue funcionando

$exe    = 'C:\Users\ampz\Desktop\Repos personales\ampz-mediaboard -dev\bin\Debug\net10.0-windows\AmpzMediaBoard.exe'
$clip   = 'C:\Users\ampz\AppData\Local\Temp\claude\C--Users-ampz-Desktop-Repos-personales-ampz-mediaboard--dev\0dbfb6b6-ec30-4f72-8818-d75d565ae8a0\scratchpad\test-clip.gif'
$board  = 'C:\Users\ampz\AppData\Local\Temp\claude\C--Users-ampz-Desktop-Repos-personales-ampz-mediaboard--dev\0dbfb6b6-ec30-4f72-8818-d75d565ae8a0\scratchpad\board-limpio.mboard'
$datos  = "$env:APPDATA\AmpzMediaBoard"

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 1000

# Borramos la carpeta de datos ENTERA. Si la app vuelve a crearla, esta escribiendo estado.
Remove-Item -Path $datos -Recurse -Force -ErrorAction SilentlyContinue
[Console]::WriteLine('Carpeta de datos borrada: ' + $datos)
[Console]::WriteLine('existe antes de arrancar: ' + (Test-Path $datos))
[Console]::WriteLine('')

# --- 1) exe pelado ---
Start-Process $exe
Start-Sleep -Seconds 6
$p = Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue
if (-not $p) { [Console]::WriteLine('FALLO: no arranco'); exit 1 }

[Console]::WriteLine('=== 1. ARRANQUE SIN ARGUMENTOS ===')
[Console]::WriteLine('  titulo: ' + $p.MainWindowTitle)
$vacio = $p.MainWindowTitle -like '*sin guardar*'
[Console]::WriteLine('  arranco con board VACIO: ' + $(if ($vacio) { 'SI' } else { 'NO' }))

# Cierre LIMPIO (WM_CLOSE): es el unico que dispararia un autoguardado si existiera.
$null = $p.CloseMainWindow()
Start-Sleep -Seconds 4
Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

[Console]::WriteLine('')
[Console]::WriteLine('=== 2. NO ESCRIBE ESTADO ===')
$recreo = Test-Path $datos
[Console]::WriteLine('  recreo %APPDATA%\AmpzMediaBoard al cerrar: ' + $(if ($recreo) { 'SI -- ESCRIBE ESTADO' } else { 'NO' }))
if ($recreo) { Get-ChildItem $datos -Recurse | ForEach-Object { [Console]::WriteLine('    ' + $_.Name) } }

# --- 3) apertura por argumento (doble click) ---
$doc = [ordered]@{ Type='sector'; Path=$clip; LoopStart=100; LoopEnd=800; LoopEnabled=$true }
[System.IO.File]::WriteAllText($board, ($doc | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))
Start-Process -FilePath $exe -ArgumentList "`"$board`""
Start-Sleep -Seconds 6
$p2 = Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue

[Console]::WriteLine('')
[Console]::WriteLine('=== 3. APERTURA POR ARGUMENTO (doble click) ===')
if ($p2) {
    [Console]::WriteLine('  titulo: ' + $p2.MainWindowTitle)
    $abrio = $p2.MainWindowTitle -like '*board-limpio*'
    [Console]::WriteLine('  abrio el archivo: ' + $(if ($abrio) { 'SI' } else { 'NO' }))
} else { [Console]::WriteLine('  no arranco'); $abrio = $false }

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue

[Console]::WriteLine('')
[Console]::WriteLine('=== VEREDICTO ===')
[Console]::WriteLine('  ' + $(if ($vacio -and (-not $recreo) -and $abrio) { 'ARRANQUE LIMPIO OK - CERO ESTADO OCULTO' } else { 'REVISAR' }))
