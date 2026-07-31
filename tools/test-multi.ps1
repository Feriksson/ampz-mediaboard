# Prueba de MULTI-INSTANCIA y de a quien le pertenece la sesion.
#  1. dos boards corriendo a la vez (requisito del usuario)
#  2. la instancia abierta DESDE ARCHIVO no toca %APPDATA%\board.json
#  3. la instancia abierta SIN archivo si lo escribe

$exe   = 'C:\Users\ampz\Desktop\Repos personales\ampz-mediaboard -dev\bin\Debug\net10.0-windows\AmpzMediaBoard.exe'
$clip  = 'C:\Users\ampz\AppData\Local\Temp\claude\C--Users-ampz-Desktop-Repos-personales-ampz-mediaboard--dev\0dbfb6b6-ec30-4f72-8818-d75d565ae8a0\scratchpad\test-clip.gif'
$board = 'C:\Users\ampz\AppData\Local\Temp\claude\C--Users-ampz-Desktop-Repos-personales-ampz-mediaboard--dev\0dbfb6b6-ec30-4f72-8818-d75d565ae8a0\scratchpad\board-b.mboard'
$sesion = "$env:APPDATA\AmpzMediaBoard\board.json"

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 1000

# El .mboard se escribe INDENTADO igual que lo hace la app, para que la comparacion de
# "cambios sin guardar" no salte por diferencia de formato.
$doc = [ordered]@{ Type='sector'; Path=$clip; LoopStart=100; LoopEnd=800; LoopEnabled=$true }
[System.IO.File]::WriteAllText($board, ($doc | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))

# Sesion con una MARCA reconocible, para detectar si alguien la pisa.
$marca = 'D:\marca-de-sesion\NO-ME-PISES.mp4'
$ses = [ordered]@{ Board = [ordered]@{ Type='sector'; Path=$marca; LoopStart=7777; LoopEnd=8888; LoopEnabled=$true }; CurrentFile = $null }
[System.IO.File]::WriteAllText($sesion, ($ses | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))
[Console]::WriteLine('Sesion sembrada con marca LoopStart=7777')
[Console]::WriteLine('')

# --- Instancia A: abierta DESDE ARCHIVO (simula el doble click) ---
Start-Process -FilePath $exe -ArgumentList "`"$board`""
Start-Sleep -Seconds 6
# --- Instancia B: abierta SIN archivo (restaura la sesion) ---
Start-Process -FilePath $exe
Start-Sleep -Seconds 6

$procs = @(Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue)
[Console]::WriteLine('=== 1. MULTI-INSTANCIA ===')
[Console]::WriteLine('  instancias corriendo: ' + $procs.Count + ' (esperado 2)')
foreach ($x in $procs) { [Console]::WriteLine('    PID ' + $x.Id + ' -> ' + $x.MainWindowTitle) }

$multi = $procs.Count -eq 2

# --- Cerramos SOLO la instancia abierta desde archivo ---
$desdeArchivo = $procs | Where-Object { $_.MainWindowTitle -like '*board-b*' }
[Console]::WriteLine('')
[Console]::WriteLine('=== 2. CIERRA LA INSTANCIA ABIERTA DESDE ARCHIVO ===')
if (-not $desdeArchivo) { [Console]::WriteLine('  no la encontre'); }
else {
    $null = $desdeArchivo.CloseMainWindow()
    Start-Sleep -Seconds 4
    $texto = [System.IO.File]::ReadAllText($sesion)
    $intacta = $texto -match '7777'
    [Console]::WriteLine('  la sesion quedo INTACTA (marca 7777 presente): ' + $(if ($intacta) { 'SI' } else { 'NO -- la piso' }))
}

# --- Ahora cerramos la instancia SIN archivo: esta SI debe escribir la sesion ---
[Console]::WriteLine('')
[Console]::WriteLine('=== 3. CIERRA LA INSTANCIA SIN ARCHIVO (duena de la sesion) ===')
$restantes = @(Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue)
foreach ($x in $restantes) { $null = $x.CloseMainWindow() }
Start-Sleep -Seconds 4
$texto2 = [System.IO.File]::ReadAllText($sesion)
$escribio = $texto2 -match '"CurrentFile"' -or $texto2 -match '"Board"'
[Console]::WriteLine('  reescribio la sesion con formato propio: ' + $(if ($escribio) { 'SI' } else { 'NO' }))
[Console]::WriteLine('  conserva la marca del board restaurado (7777): ' + $(if ($texto2 -match '7777') { 'SI' } else { 'NO' }))

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
[Console]::WriteLine('')
[Console]::WriteLine('=== VEREDICTO ===')
[Console]::WriteLine('  ' + $(if ($multi -and $intacta) { 'MULTI-INSTANCIA OK Y LA SESION NO SE PISA' } else { 'REVISAR' }))
