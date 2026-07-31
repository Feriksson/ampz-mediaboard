# Verifica que la app siga siendo MULTI-INSTANCIA (requisito del usuario: correr dos o mas
# boards a la vez). Si alguien le mete un mutex de instancia unica, esta prueba lo caza.
#
# Nota: hasta v1.2.0 este script tambien verificaba a quien le pertenecia el estado de sesion.
# Esa verificacion murio junto con la sesion — ver tools/test-clean-start.ps1.

$exe   = 'C:\Users\ampz\Desktop\Repos personales\ampz-mediaboard -dev\bin\Debug\net10.0-windows\AmpzMediaBoard.exe'
$clip  = 'C:\Users\ampz\AppData\Local\Temp\claude\C--Users-ampz-Desktop-Repos-personales-ampz-mediaboard--dev\0dbfb6b6-ec30-4f72-8818-d75d565ae8a0\scratchpad\test-clip.gif'
$board = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'board-multi.mboard')

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 1000

$doc = [ordered]@{ Type='sector'; Path=$clip; LoopStart=100; LoopEnd=800; LoopEnabled=$true }
[System.IO.File]::WriteAllText($board, ($doc | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))

Start-Process -FilePath $exe -ArgumentList "`"$board`""   # instancia A: abierta desde archivo
Start-Sleep -Seconds 6
Start-Process $exe                                        # instancia B: board vacio
Start-Sleep -Seconds 6

$procs = @(Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue)
[Console]::WriteLine('=== MULTI-INSTANCIA ===')
[Console]::WriteLine('  instancias corriendo: ' + $procs.Count + ' (esperado 2)')
foreach ($x in $procs) { [Console]::WriteLine('    PID ' + $x.Id + ' -> ' + $x.MainWindowTitle) }

$titulos = @($procs | ForEach-Object { $_.MainWindowTitle } | Sort-Object -Unique)
$ok = ($procs.Count -eq 2) -and ($titulos.Count -eq 2)

[Console]::WriteLine('')
[Console]::WriteLine('=== VEREDICTO ===')
[Console]::WriteLine('  ' + $(if ($ok) { 'DOS BOARDS SIMULTANEOS, CADA UNO CON SU TITULO' } else { 'REVISAR -- se agrego un mutex de instancia unica?' }))

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Remove-Item $board -Force -ErrorAction SilentlyContinue
