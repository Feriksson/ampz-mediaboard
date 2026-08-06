# Prueba end-to-end del caso "el archivo del sector ya no existe".
#
# Lo que se verifica NO es que la app no crashee (eso es el piso), sino que la REFERENCIA al
# archivo y los MARKERS DE LOOP sobrevivan al ciclo abrir -> cerrar (autoguardado). Si el
# autoguardado los borrara, el board se degradaria solo con abrir la app UNA vez.
#
# ⚠ Esta prueba estuvo MUERTA un tiempo y no se noto: apuntaba a
# %APPDATA%\AmpzMediaBoard\board.json, el estado de sesion que se elimino a proposito (ver
# "ARRANQUE LIMPIO" en el CLAUDE.md). Como la app dejo de escribir ahi, fallaba SIEMPRE, en
# cualquier version -- y una prueba que no puede pasar nunca no avisa nada: se aprende a
# ignorarla. Hoy va contra un .mboard, que es el unico lugar donde vive un board.
#
# ⚠ ALCANCE. Esta prueba es END-TO-END y cubre: que la app no se caiga con un archivo
# inexistente, que el board CARGUE igual (no lo rechace como corrupto), y que cerrar NO
# degrade el .mboard. NO cubre el round-trip guardar->leer de la referencia y los markers:
# eso lo cubre tools/BoardProbe (caso 3), que SI se vio fallar rompiendo a proposito el
# MarkMissing de BoardStore.FromDto. No se intenta guardar desde aca con SendKeys: no llega
# a la ventana de forma confiable y daria un verde hueco, que es peor que no probar.
#
# ⚠ El .mboard se genera con ConvertTo-Json, NO a mano. Un path de Windows escrito a mano en
# JSON ("D:\clips\x.mp4") tiene backslashes SIN ESCAPAR = JSON invalido: la app lo rechaza con
# "board corrupto" y la prueba mide el rechazo en vez de medir el autoguardado. Ya paso.
#
#   powershell -File tools/test-missing.ps1
#
# Sale 0 si pasa, 1 si fallo.

$repo  = Split-Path -Parent $PSScriptRoot
$exe   = Join-Path $repo 'bin\Debug\net10.0-windows\AmpzMediaBoard.exe'
$board = Join-Path $env:TEMP 'ampz-fantasma.mboard'
$ghost = 'D:\clips-que-ya-no-estan\take-042-final.mp4'

if (-not (Test-Path $exe)) {
    Write-Output "FALLO: no existe $exe -- compila primero con dotnet build."
    exit 1
}

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# Board con DOS sectores: uno fantasma (archivo inexistente, con markers puestos) y uno vacio.
# Asi se prueba tambien que el arbol con split se restaure bien.
$doc = [ordered]@{
    Type        = 'split'
    Orientation = 'Horizontal'
    Ratio       = 0.6
    First       = [ordered]@{ Type = 'sector'; Path = $ghost; LoopStart = 1500; LoopEnd = 4200; LoopEnabled = $true }
    Second      = [ordered]@{ Type = 'sector'; LoopStart = 0; LoopEnd = 0; LoopEnabled = $true }
}
[System.IO.File]::WriteAllText($board, ($doc | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))

Write-Output '=== .mboard ANTES (archivo fantasma + markers 1500/4200) ==='
Write-Output ('  ' + [System.IO.File]::ReadAllText($board))
Write-Output ''

Start-Process -FilePath $exe -ArgumentList "`"$board`""
Start-Sleep -Seconds 8

$p = Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue
if (-not $p) {
    Write-Output 'FALLO: la app se cayo al abrir un board con un archivo inexistente'
    exit 1
}

# El titulo delata si el board cargo de verdad. Si LoadFrom hubiera devuelto null, lo que se
# ve es el MessageBox de "board corrupto" -- cuyo caption es "Ampz MediaBoard" a secas -- y el
# resto de la prueba mediria el rechazo, no el autoguardado.
$titulo = $p.MainWindowTitle
Write-Output ("La app SOBREVIVIO al archivo inexistente. PID: $($p.Id) -- titulo: $titulo")
if ($titulo -notlike '*ampz-fantasma*') {
    Write-Output "FALLO: el board NO cargo (titulo inesperado: '$titulo')."
    Write-Output '       Si dice "Ampz MediaBoard" a secas, hay un MessageBox de board corrupto arriba.'
    Stop-Process -Id $p.Id -Force
    exit 1
}

# Cierre LIMPIO (WM_CLOSE): lo que se verifica es que NO reescriba el archivo degradandolo.
$null = $p.CloseMainWindow()
Start-Sleep -Seconds 4
$p.Refresh()
if (-not $p.HasExited) { Write-Output 'AVISO: no cerro solo'; Stop-Process -Id $p.Id -Force }

Write-Output ''
Write-Output '=== .mboard DESPUES del ciclo abrir/cerrar ==='
$after = [System.IO.File]::ReadAllText($board)
Write-Output $after
Write-Output ''

Write-Output '=== VEREDICTO ==='
$okPath  = $after.Contains('take-042-final.mp4')
$okStart = $after -match '"LoopStart":\s*1500'
$okEnd   = $after -match '"LoopEnd":\s*4200'
# ⚠ Regex, NO Contains con un espacio fijo. El archivo puede quedar con el formato de quien lo
# escribio ultimo: si el board no cambio, la app NO lo reescribe (MatchesFile compara el JSON
# NORMALIZADO, ver BoardStore), asi que sobrevive la indentacion de ConvertTo-Json -- que usa
# DOS espacios despues de los dos puntos. Un Contains('"Type": "split"') falla por eso y hace
# creer que se perdio el split cuando esta intacto.
$okSplit = $after -match '"Type":\s*"split"'
Write-Output ('  referencia al archivo conservada  : ' + $(if ($okPath)  { 'SI' } else { 'NO -- SE PERDIO' }))
Write-Output ('  marker de inicio (1500) conservado: ' + $(if ($okStart) { 'SI' } else { 'NO -- SE PERDIO' }))
Write-Output ('  marker de fin (4200) conservado   : ' + $(if ($okEnd)   { 'SI' } else { 'NO -- SE PERDIO' }))
Write-Output ('  estructura del split conservada   : ' + $(if ($okSplit) { 'SI' } else { 'NO -- SE PERDIO' }))

if ($okPath -and $okStart -and $okEnd -and $okSplit) { Write-Output '  TODO OK'; exit 0 }
Write-Output '  FALLO'
exit 1
