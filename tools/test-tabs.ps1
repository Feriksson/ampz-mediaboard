# Prueba end-to-end de las PESTANAS: cambiar de pestana tiene que ser INSTANTANEO, y lo es porque
# la pestana que se va se PAUSA y se COLAPSA (no se destruye) y la que vuelve se DESPAUSA (no se
# reconstruye). Ver BoardTab.Activate/Deactivate y BoardView.SetSuspended.
#
# UN archivo .mboard (v2) con TRES pestanas, abierto por linea de comandos; la activa guardada es
# la primera: A y B con N sectores de video cada uno, C VACIA. Verifica:
#   0. El archivo abre con sus tres pestanas y el titulo es el del ARCHIVO, SIN la marca "•" de
#      cambios: recien abierto no hay nada que guardar. Al final se vuelve a mirar: cambiar de
#      pestana es mirar, no editar, y tampoco puede marcar el archivo como modificado.
#   1. Tiempo de cambio de pestana, medido ADENTRO de la app (linea `switch` del DiagLog: desde
#      que arranca SwitchTo hasta despues del layout de la pestana nueva). El tiempo de punta a
#      punta por UI Automation (Invoke -> titulo nuevo) se informa pero NO se juzga: la llamada
#      Invoke sola llego a tardar 250ms por el marshaling entre procesos, y con la maquina
#      cargada hubo corridas enteras de ~2s por cambio que no eran de la app.
#      ⚠ El PRIMER cambio a B no se juzga: B se abrio en segundo plano y nunca se mostro, asi que
#      su primera pasada de layout construye los VideoView y sus ventanas nativas (medido:
#      1.0-3.5 s con 4 sectores; el tramo sincronico de SwitchTo son 0-15 ms, el resto es el
#      layout). Es el costo de ABRIR un board, diferido — el mismo que paga Ctrl+O al abrirlo en
#      la pestana activa. Se informa aparte. Lo que se juzga es cambiar entre pestanas ya vistas.
#   2. Las pestanas inactivas estan PAUSADAS: con C (vacia) visible, despues de que A y B
#      reprodujeron, el proceso tiene que quemar MENOS que con A sola reproduciendo. Si no se
#      pausaran, A y B decodificarian a la vez: ~2x lo de A sola.
#      Numeros medidos (4 sectores, clip mandelbrot 640x360), para calibrar:
#        board vacio solo (el piso) ................ 0-25 ms/s
#        C visible, A y B PAUSADAS ................. 88-229 ms/s  (un player pausado no es gratis)
#        A sola reproduciendo ...................... 216-1115 ms/s (muy ruidoso entre corridas)
#      ⚠ Por ese ruido NO se compara "A visible" contra "B visible" (dio 306 / 539 / 450 con la
#      misma carga) ni contra un porcentaje chico de A: el unico corte con margen de los dos
#      lados es "menos que UNA pestana reproduciendo" contra el rojo de "DOS reproduciendo".
#   3. Volver a una pestana NO reabre archivos (sin Remount -> sin frames negros ni salto): se
#      cuentan las creaciones de Media con AMPZ_MEDIA_LOG (ver VlcEngine.NewMedia). Al abrir se
#      crean 2N (los de B quedan PENDIENTES); despues de ir y volver varias veces tiene que seguir
#      en 2N. Contar aperturas no depende de "agarrar" un negro de 200ms con capturas.
#   4. La pestana que vuelve REANUDA (CPU de vuelta a regimen) y su video sigue VIVO (dos
#      capturas separadas tienen que diferir).
#   5. Ninguna ventana de VLC suelta (owner = 0): el bug #1 del CLAUDE.md. Los clips de B se
#      cargaron con la pestana en segundo plano y solo pueden arrancar cuando B tiene superficie.
#
# ⚠ Se maneja por UI Automation (InvokePattern sobre el boton de la pestana), NO con
#   SendKeys/AppActivate: esos no llegan a la ventana de forma confiable (ver CLAUDE.md).
# ⚠ El titulo ya NO sirve para saber que pestana se ve: es el del ARCHIVO, igual para todas. El
#   cambio se detecta por la linea `switch` nueva del DiagLog.
# ⚠ El .mboard se genera con ConvertTo-Json (backslashes escapados) y con LoopEnd FIJO: con
#   LoopEnd=0 el primer tick completa la zona y el board queda "modificado" -> dialogo al cerrar.
#
#   powershell -File tools/test-tabs.ps1
#   powershell -File tools/test-tabs.ps1 -Sectors 4 -SwitchThresholdMs 250
#
# Sale 0 si pasa, 1 si fallo.

param([int]$Sectors = 4, [int]$SwitchThresholdMs = 100, [double]$PausedCeiling = 1.0)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'bin\Debug\net10.0-windows\AmpzMediaBoard.exe'
if (-not (Test-Path $exe)) { Write-Host "No esta compilado: $exe"; exit 1 }
$fallas = 0
function Fallo([string]$m) { Write-Host "  [FALLA] $m"; $script:fallas++ }
function Ok([string]$m)    { Write-Host "  [OK ] $m" }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public struct TRECT { public int Left, Top, Right, Bottom; }
public static class TabsWin32 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out TRECT r);
    public static string Text(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
    // Ventanas top-level VISIBLES del proceso sin duenio (GW_OWNER = 4), excepto la principal.
    public static List<string> Unowned(int pid, IntPtr main) {
        var found = new List<string>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && h != main && IsWindowVisible(h) && GetWindow(h, 4) == IntPtr.Zero) found.Add(Text(h));
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

# Clip PROPIO, largo y animado en toda la imagen (mismo que test-close): tiene que estar
# decodificando de verdad y no llegar al final durante la prueba.
$clip = Join-Path $env:TEMP 'ampz-close-clip.mp4'
if (-not (Test-Path $clip)) {
    if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { Write-Host 'No hay ffmpeg para generar el clip.'; exit 1 }
    ffmpeg -v error -y -f lavfi -i 'mandelbrot=size=640x360:rate=30' -t 60 -pix_fmt yuv420p $clip
}

function New-Tree([int]$n, [int]$depth) {
    if ($n -le 1) { return [ordered]@{ Type = 'sector'; Path = $clip; LoopStart = 0; LoopEnd = 50000; LoopEnabled = $true; Volume = 0; Muted = $true } }
    $left = [math]::Floor($n / 2)
    return [ordered]@{
        Type = 'split'; Orientation = $(if ($depth % 2 -eq 0) { 'Horizontal' } else { 'Vertical' }); Ratio = $left / $n
        First = New-Tree $left ($depth + 1); Second = New-Tree ($n - $left) ($depth + 1)
    }
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$board = Join-Path $env:TEMP 'ampz-tabs.mboard'
$doc = [ordered]@{
    Version = 2; ActiveTab = 0
    Tabs = @(
        [ordered]@{ Name = 'A'; Root = New-Tree $Sectors 0 },
        [ordered]@{ Name = 'B'; Root = New-Tree $Sectors 0 },
        [ordered]@{ Name = 'C'; Root = [ordered]@{ Type = 'sector' } }
    )
}
[System.IO.File]::WriteAllText($board, ($doc | ConvertTo-Json -Depth 40), $utf8)

$diagLog = Join-Path $env:TEMP 'ampz-tabs-diag.log'
Remove-Item $diagLog -ErrorAction SilentlyContinue
function Get-Diag([string]$kind) { if (Test-Path $diagLog) { @(Get-Content $diagLog | Where-Object { $_ -like "$kind *" }) } else { @() } }
function Get-Opens { (Get-Diag 'open').Count }

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# El proceso hijo hereda la variable: asi se prende el DiagLog SOLO para esta corrida.
$env:AMPZ_DIAG_LOG = $diagLog
$p = Start-Process -FilePath $exe -ArgumentList "`"$board`"" -PassThru
Remove-Item Env:\AMPZ_DIAG_LOG
$null = $p.Handle

$h = [IntPtr]::Zero
for ($i = 0; $i -lt 80; $i++) {
    Start-Sleep -Milliseconds 250
    $p.Refresh()
    if ($p.HasExited) { Write-Host 'FALLO: el proceso murio al arrancar'; exit 1 }
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $h = $p.MainWindowHandle; break }
}
if ($h -eq [IntPtr]::Zero) { Write-Host 'FALLO: la ventana nunca aparecio'; exit 1 }

function Title { [TabsWin32]::Text($h) }
# La marca de cambios sin guardar, por codigo: el .ps1 va sin BOM y PowerShell 5.1 lo leeria como
# ANSI, asi que un "•" literal en un string no coincidiria nunca con el del titulo.
$punto = [string][char]0x2022

# CPU del proceso en una ventana de tiempo, en ms de CPU por segundo de reloj.
function Get-CpuRate([double]$seconds = 3) {
    $p.Refresh(); $a = $p.TotalProcessorTime.TotalMilliseconds
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Milliseconds ([int]($seconds * 1000))
    $p.Refresh(); $b = $p.TotalProcessorTime.TotalMilliseconds
    return [math]::Round(($b - $a) / $sw.Elapsed.TotalSeconds)
}

# Firma de la imagen del area del board (misma idea que test-fullscreen).
function Get-Firma {
    $r = New-Object TRECT; [void][TabsWin32]::GetWindowRect($h, [ref]$r)
    $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $hh))
    $suma = 0; $distintos = @{}
    for ($y = [int]($hh * 0.2); $y -lt $hh - 4; $y += [math]::Max(1, [int]($hh / 40))) {
        for ($x = 4; $x -lt $w - 4; $x += [math]::Max(1, [int]($w / 40))) {
            $px = $bmp.GetPixel($x, $y).ToArgb(); $suma = ($suma * 31 + $px) % 2147483647; $distintos[$px] = $true
        }
    }
    $g.Dispose(); $bmp.Dispose()
    [pscustomobject]@{ Hash = $suma; Colores = $distintos.Count }
}

# Se espera a que A este reproduciendo (CPU), no a ciegas.
Start-Sleep -Seconds 3
$deadline = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $deadline -and (Get-CpuRate 1) -lt 150) { }
Start-Sleep -Seconds 2

Write-Host ''
Write-Host "=== 0. ARRANQUE ($Sectors sectores por pestana) ==="
Write-Host "  titulo: '$(Title)'"
if ((Title) -eq 'Ampz MediaBoard') { Write-Host 'FALLO: hay un MessageBox arriba.'; Stop-Process -Id $p.Id -Force; exit 1 }
if ((Title) -notlike 'ampz-tabs*') { Write-Host 'FALLO: el titulo no es el del archivo.'; Stop-Process -Id $p.Id -Force; exit 1 }
if ((Title).Contains($punto)) { Fallo 'recien abierto, el archivo ya figura con cambios sin guardar' }
else { Ok 'recien abierto, sin cambios sin guardar' }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'BoardTab')
$tabs = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
$nombres = @($tabs | ForEach-Object { $_.Current.Name }) -join ', '
Write-Host "  pestanas: $($tabs.Count)  ($nombres)"
if ($tabs.Count -ne 3 -or $nombres -ne 'A, B, C') { Write-Host 'FALLO: se esperaban las 3 pestanas del archivo (A, B, C).'; Stop-Process -Id $p.Id -Force; exit 1 }

$opensInicio = Get-Opens
Write-Host "  Media creados al abrir: $opensInicio (esperado: $(2 * $Sectors))"
if ($opensInicio -ne 2 * $Sectors) { Fallo "al abrir se crearon $opensInicio Media, no $(2 * $Sectors)" }

# ⚠ La linea de base se toma en REGIMEN, no recien abierto: los primeros segundos incluyen abrir
# los archivos e inicializar decoders, y con eso inflado (medido: ~900 contra ~230 ms/s en
# regimen) el "no se reanudo" daba falso rojo.
Start-Sleep -Seconds 5
$cpuA = Get-CpuRate 5
$switchesInicio = (Get-Diag 'switch').Count
Write-Host "  CPU con A visible (B pendiente), en regimen: $cpuA ms/s"

$script:invokeMs = @()
# Invoke + espera de la linea `switch` nueva. Devuelve los ms del cambio.
function Switch-To([int]$index) {
    $antes = (Get-Diag 'switch').Count
    $inv = $tabs[$index].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $inv.Invoke()
    $script:invokeMs += $sw.ElapsedMilliseconds
    while ((Get-Diag 'switch').Count -le $antes -and $sw.ElapsedMilliseconds -lt 5000) { Start-Sleep -Milliseconds 5 }
    return $sw.ElapsedMilliseconds
}

Write-Host ''
Write-Host '=== 1. A -> B (primera vez: los clips de B arrancan recien ahora) ==='
$t1 = Switch-To 1
Write-Host "  cambio en $t1 ms"
Start-Sleep -Seconds 4   # que B abra sus clips y reproduzca: B tiene que haber estado VIVA antes de pausarse

Write-Host ''
Write-Host '=== 2. B -> C (vacia): A y B tienen que estar PAUSADAS ==='
$t2 = Switch-To 2
Write-Host "  cambio en $t2 ms"
Start-Sleep -Seconds 2
$cpuC = Get-CpuRate 5
$techo = [math]::Round($cpuA * $PausedCeiling)
Write-Host "  CPU con C (vacia) visible: $cpuC ms/s  (techo: $techo = A sola reproduciendo; sin pausa serian ~$(2 * $cpuA))"
if ($cpuC -gt $techo) { Fallo "con la pestana vacia visible el proceso quema $cpuC ms/s, mas que UNA pestana reproduciendo: las de fondo siguen decodificando" }
else { Ok 'las pestanas inactivas (A y B) estan pausadas' }

Write-Host ''
Write-Host '=== 3. IDA Y VUELTA (A, B, C, 3 veces): tiempos y aperturas ==='
$tiempos = @($t1, $t2)
for ($k = 0; $k -lt 3; $k++) {
    $tiempos += Switch-To 0; Start-Sleep -Milliseconds 700
    $tiempos += Switch-To 1; Start-Sleep -Milliseconds 700
    $tiempos += Switch-To 2; Start-Sleep -Milliseconds 700
}
$tiempos += Switch-To 0
Start-Sleep -Milliseconds 500
$todos = @(Get-Diag 'switch' | Select-Object -Skip $switchesInicio | ForEach-Object { [int]($_ -split ' ')[1] })
Write-Host "  primera vez que se muestra B (apertura diferida, informativo): $($todos[0]) ms"
$enApp = @($todos | Select-Object -Skip 1)
$tiempos = @($tiempos | Select-Object -Skip 1)
Write-Host "  en la app (ms) ........... $($enApp -join ', ')"
Write-Host "  punta a punta UIA (ms) ... $($tiempos -join ', ')   (informativo)"
Write-Host "  de eso, la llamada Invoke  $($script:invokeMs -join ', ')"
if ($enApp.Count -ne $tiempos.Count) { Fallo "se esperaban $($tiempos.Count) cambios registrados y hay $($enApp.Count)" }
$peor = ($enApp | Measure-Object -Maximum).Maximum
if ($peor -gt $SwitchThresholdMs) { Fallo "un cambio tardo $peor ms en la app (umbral $SwitchThresholdMs ms)" }
else { Ok "todos los cambios por debajo de $SwitchThresholdMs ms en la app (peor: $peor ms)" }

Start-Sleep -Seconds 1
$opensFin = Get-Opens
Write-Host "  Media creados: $opensFin (al abrir: $opensInicio)"
if ($opensFin -ne $opensInicio) { Fallo "volver a una pestana creo $($opensFin - $opensInicio) Media nuevos: se RE-MONTARON los clips (VLC reabrio archivos)" }
else { Ok 'volver a una pestana NO reabrio ningun archivo' }

Start-Sleep -Seconds 2
$cpuA2 = Get-CpuRate 5
Write-Host "  CPU con A visible otra vez: $cpuA2 ms/s (en regimen antes: $cpuA)"
if ($cpuA2 -lt $cpuA / 2) { Fallo "A no se REANUDO al volver" }
else { Ok 'A se reanudo al volver' }

Write-Host ''
Write-Host '=== 4. EL VIDEO DE LA PESTANA QUE VOLVIO SIGUE VIVO ==='
$f1 = Get-Firma; Start-Sleep -Milliseconds 900; $f2 = Get-Firma
Write-Host "  capturas: $($f1.Colores) / $($f2.Colores) colores"
if ($f1.Colores -le 2) { Fallo 'el board quedo en un color plano (negro)' }
elseif ($f1.Hash -eq $f2.Hash) { Fallo 'la imagen no cambio: el video quedo congelado' }
else { Ok 'el video sigue decodificando' }

Write-Host ''
Write-Host '=== 5. NINGUNA VENTANA DE VLC SUELTA (bug #1) ==='
$sueltas = [TabsWin32]::Unowned($p.Id, $h)
if ($sueltas.Count -gt 0) { Fallo "ventanas sin duenio: $($sueltas -join ' | ')" }
else { Ok 'todas las ventanas extra son owned por la principal' }

Write-Host ''
Write-Host '=== 6. CAMBIAR DE PESTANA NO ES UN CAMBIO SIN GUARDAR ==='
Start-Sleep -Milliseconds 700   # la marca se refresca cada 500 ms
Write-Host "  titulo: '$(Title)'"
if ((Title).Contains($punto)) { Fallo 'despues de ir y volver entre pestanas el archivo figura modificado' }
else { Ok 'el archivo sigue sin cambios' }

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Remove-Item $board, $diagLog -ErrorAction SilentlyContinue

Write-Host ''
if ($fallas -eq 0) { Write-Host '=== TODO OK ==='; exit 0 }
Write-Host "=== $fallas FALLA(S) ==="
exit 1
