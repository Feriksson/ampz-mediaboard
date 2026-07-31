# Prueba end-to-end del caso "el archivo ya no existe".
# Lo que se verifica NO es que la app no crashee (eso es el piso), sino que la REFERENCIA
# al archivo y los MARKERS DE LOOP sobrevivan al ciclo abrir -> cerrar (autoguardado).
# Si el autoguardado los borrara, el board se degradaria solo con solo abrir la app una vez.

$boardFile = "$env:APPDATA\AmpzMediaBoard\board.json"
$exe = 'C:\Users\ampz\Desktop\Repos personales\ampz-mediaboard -dev\bin\Debug\net10.0-windows\AmpzMediaBoard.exe'
$ghost = 'D:\clips-que-ya-no-estan\take-042-final.mp4'

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# Board con DOS sectores: uno fantasma (archivo inexistente, con markers puestos) y uno vacio.
# Asi se prueba tambien que el arbol con split se restaure bien.
$board = [ordered]@{
    Type        = 'split'
    Orientation = 'Horizontal'
    Ratio       = 0.6
    First       = [ordered]@{ Type = 'sector'; Path = $ghost; LoopStart = 1500; LoopEnd = 4200; LoopEnabled = $true }
    Second      = [ordered]@{ Type = 'sector'; LoopStart = 0; LoopEnd = 0; LoopEnabled = $true }
}
$json = $board | ConvertTo-Json -Depth 10 -Compress
[System.IO.File]::WriteAllText($boardFile, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Output '=== board.json ANTES (archivo fantasma + markers 1500/4200) ==='
Write-Output ('  ' + [System.IO.File]::ReadAllText($boardFile))
Write-Output ''

Start-Process $exe
Start-Sleep -Seconds 6

$p = Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue
if (-not $p) {
    Write-Output 'FALLO: la app se cayo al abrir un board con un archivo inexistente'
    exit 1
}
Write-Output ('La app SOBREVIVIO al archivo inexistente. PID: ' + $p.Id)

# Cierre LIMPIO (WM_CLOSE) para que dispare el evento Closing -> autoguardado.
# Un Stop-Process no dispararia Closing y la prueba no probaria nada.
$null = $p.CloseMainWindow()
Start-Sleep -Seconds 3
$p.Refresh()
if (-not $p.HasExited) { Write-Output 'AVISO: no cerro solo'; Stop-Process -Id $p.Id -Force }

Write-Output ''
Write-Output '=== board.json DESPUES del ciclo abrir/cerrar ==='
$after = [System.IO.File]::ReadAllText($boardFile)
Write-Output $after
Write-Output ''

Write-Output '=== VEREDICTO ==='
$okPath  = $after.Contains('take-042-final.mp4')
$okStart = $after -match '"LoopStart":\s*1500'
$okEnd   = $after -match '"LoopEnd":\s*4200'
$okSplit = $after.Contains('"Type": "split"') -or $after.Contains('"Type":"split"')
Write-Output ('  referencia al archivo conservada : ' + $(if ($okPath)  { 'SI' } else { 'NO -- SE PERDIO' }))
Write-Output ('  marker de inicio (1500) conservado: ' + $(if ($okStart) { 'SI' } else { 'NO -- SE PERDIO' }))
Write-Output ('  marker de fin (4200) conservado   : ' + $(if ($okEnd)   { 'SI' } else { 'NO -- SE PERDIO' }))
Write-Output ('  estructura del split conservada   : ' + $(if ($okSplit) { 'SI' } else { 'NO -- SE PERDIO' }))
