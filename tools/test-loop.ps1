# Prueba del loop por defecto (sin tocar ningun marker).
# Idea: si el loop NO funciona, VLC llega a Ended y DEJA DE DECODIFICAR -> el CPU se aplana
# despues de la primera pasada. Si loopea, sigue consumiendo indefinidamente.
#
# OJO con PowerShell: dentro de una funcion, Write-Output va al MISMO stream que el return.
# Por eso el log va por [Console]::WriteLine y la funcion devuelve UN SOLO valor.
$clip = 'C:\Users\ampz\AppData\Local\Temp\claude\C--Users-ampz-Desktop-Repos-personales-ampz-mediaboard--dev\0dbfb6b6-ec30-4f72-8818-d75d565ae8a0\scratchpad\test-clip.gif'
$exe  = 'C:\Users\ampz\Desktop\Repos personales\ampz-mediaboard -dev\bin\Debug\net10.0-windows\AmpzMediaBoard.exe'

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$board = [ordered]@{ Type='sector'; Path=$clip; LoopStart=0; LoopEnd=0; LoopEnabled=$true }
$json = $board | ConvertTo-Json -Compress
[System.IO.File]::WriteAllText("$env:APPDATA\AmpzMediaBoard\board.json", $json, (New-Object System.Text.UTF8Encoding($false)))
[Console]::WriteLine('board.json: clip cargado con LoopEnd=0 (la app debe inicializar la zona sola)')

Start-Process $exe
Start-Sleep -Seconds 5
$proc = Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue
if (-not $proc) { [Console]::WriteLine('FALLO: no arranco'); exit 1 }

function Measure-Cpu {
    param([string]$Label, [int]$Seconds, $Target)
    $Target.Refresh(); $a = $Target.CPU
    Start-Sleep -Seconds $Seconds
    $Target.Refresh(); $b = $Target.CPU
    $delta = [math]::Round($b - $a, 3)
    [Console]::WriteLine('  ' + $Label.PadRight(38) + $delta + ' s de CPU en ' + $Seconds + 's')
    $delta
}

[Console]::WriteLine('')
[Console]::WriteLine('=== CONSUMO DE CPU EN EL TIEMPO ===')
$v1 = Measure-Cpu -Label 'ventana 1 (primera pasada)'          -Seconds 6 -Target $proc
$v2 = Measure-Cpu -Label 'ventana 2 (ya deberia haber loopeado)' -Seconds 6 -Target $proc
$v3 = Measure-Cpu -Label 'ventana 3 (varias vueltas despues)'    -Seconds 6 -Target $proc

[Console]::WriteLine('')
[Console]::WriteLine('=== VEREDICTO ===')
[Console]::WriteLine('  tipo de v2: ' + $v2.GetType().Name + '  valor: ' + $v2)
[Console]::WriteLine('  tipo de v3: ' + $v3.GetType().Name + '  valor: ' + $v3)
$vivo = ([double]$v2 -gt 0.05) -and ([double]$v3 -gt 0.05)
if ($vivo) {
    [Console]::WriteLine('  SIGUE DECODIFICANDO tras la primera pasada -> el loop reinicia el clip')
} else {
    [Console]::WriteLine('  EL CLIP MURIO al terminar -> el loop NO dispara')
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
