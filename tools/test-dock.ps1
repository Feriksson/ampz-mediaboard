# Prueba end-to-end del PANEL FIJADO: vive FUERA de las pestanas, asi que cambiar de pestana NO
# lo puede tocar. Ver Board/PinnedDock.cs y "Panel fijado" en el CLAUDE.md.
#
# UN archivo .mboard (v2) con TRES pestanas (A y B con N sectores de video, C vacia) y un PANEL
# con un clip propio (ampz-dock-clip.mp4, nombre distinto al de las pestanas para poder
# distinguirlo en el DiagLog). Se abre por linea de comandos y se va y viene entre pestanas por UI
# Automation. Verifica, con el DiagLog (AMPZ_DIAG_LOG):
#   0. El archivo abre con el panel y el titulo SIN "•": el panel hace ida y vuelta limpio (ancho
#      como proporcion, sectores completos) y recien abierto no hay nada que guardar.
#   1. El clip del panel se abre UNA sola vez (linea `open`): ningun cambio de pestana lo reabre.
#      Si el panel viviera dentro del contenedor de una pestana, colapsarla lo re-montaria.
#   2. El clip del panel NUNCA se congela (linea `freeze`): ni pausa ni velo por un cambio de
#      pestana. Y, como control de que la sonda ve algo, los clips de las pestanas SI se congelan
#      al pasar a segundo plano (si no hubiera ninguna linea `freeze`, el paso 2 no probaria nada).
#   3. El clip del panel sigue REPRODUCIENDO con la pestana vacia al frente: su latido propio
#      escribe `audiostate` cada ~2 s solo mientras reproduce (SectorNode.DiagAudioState).
#   4. Ninguna ventana de VLC suelta (owner = 0): bug #1. El clip del panel se cargo con el panel
#      todavia colapsado y solo puede arrancar cuando tiene superficie visible.
#   5. Ir y volver entre pestanas no marca el archivo como modificado.
#
# ⚠ Se maneja por UI Automation (InvokePattern sobre el boton de la pestana), NO con
#   SendKeys/AppActivate: esos no llegan a la ventana de forma confiable (ver CLAUDE.md). No
#   necesita el escritorio libre: no mueve el mouse ni captura pantalla.
# ⚠ El .mboard se genera con ConvertTo-Json (backslashes escapados) y con LoopEnd FIJO: con
#   LoopEnd=0 el primer tick completa la zona y el board queda "modificado".
#
#   powershell -File tools/test-dock.ps1
#
# Sale 0 si pasa, 1 si fallo.

param([int]$Sectors = 2)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'bin\Debug\net10.0-windows\AmpzMediaBoard.exe'
if (-not (Test-Path $exe)) { Write-Host "No esta compilado: $exe"; exit 1 }
$fallas = 0
function Fallo([string]$m) { Write-Host "  [FALLA] $m"; $script:fallas++ }
function Ok([string]$m)    { Write-Host "  [OK ] $m" }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class DockWin32 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
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

# Clip PROPIO, largo y animado (mismo que test-close / test-tabs). El del panel es una COPIA con
# otro nombre: el DiagLog identifica los sectores por el nombre del archivo.
$clip = Join-Path $env:TEMP 'ampz-close-clip.mp4'
if (-not (Test-Path $clip)) {
    if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { Write-Host 'No hay ffmpeg para generar el clip.'; exit 1 }
    ffmpeg -v error -y -f lavfi -i 'mandelbrot=size=640x360:rate=30' -t 60 -pix_fmt yuv420p $clip
}
$dockClip = Join-Path $env:TEMP 'ampz-dock-clip.mp4'
Copy-Item $clip $dockClip -Force
$tabTitle  = Split-Path -Leaf $clip
$dockTitle = Split-Path -Leaf $dockClip

function New-Sector([string]$path) {
    [ordered]@{ Type = 'sector'; Path = $path; LoopStart = 0; LoopEnd = 50000; LoopEnabled = $true; Volume = 0; Muted = $true }
}
function New-Tree([int]$n, [int]$depth) {
    if ($n -le 1) { return New-Sector $clip }
    $left = [math]::Floor($n / 2)
    return [ordered]@{
        Type = 'split'; Orientation = $(if ($depth % 2 -eq 0) { 'Horizontal' } else { 'Vertical' }); Ratio = $left / $n
        First = New-Tree $left ($depth + 1); Second = New-Tree ($n - $left) ($depth + 1)
    }
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$board = Join-Path $env:TEMP 'ampz-dock.mboard'
$doc = [ordered]@{
    Version = 2; ActiveTab = 0
    Tabs = @(
        [ordered]@{ Name = 'A'; Root = New-Tree $Sectors 0 },
        [ordered]@{ Name = 'B'; Root = New-Tree $Sectors 0 },
        [ordered]@{ Name = 'C'; Root = [ordered]@{ Type = 'sector' } }
    )
    Dock = [ordered]@{ Width = 0.3; Sectors = @(New-Sector $dockClip) }
}
[System.IO.File]::WriteAllText($board, ($doc | ConvertTo-Json -Depth 40), $utf8)

$diagLog = Join-Path $env:TEMP 'ampz-dock-diag.log'
Remove-Item $diagLog -ErrorAction SilentlyContinue
function Get-Diag([string]$prefix) { if (Test-Path $diagLog) { @(Get-Content $diagLog | Where-Object { $_ -like "$prefix*" }) } else { @() } }

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

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
function Title { [DockWin32]::Text($h) }
$punto = [string][char]0x2022

# Se espera a que el clip del panel este REPRODUCIENDO (su primera linea audiostate), no a ciegas.
$deadline = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $deadline -and (Get-Diag "audiostate $dockTitle").Count -lt 1) { Start-Sleep -Milliseconds 250 }

Write-Host ''
Write-Host "=== 0. ARRANQUE (panel + $Sectors sectores por pestana) ==="
Write-Host "  titulo: '$(Title)'"
if ((Title) -eq 'Ampz MediaBoard') { Write-Host 'FALLO: hay un MessageBox arriba.'; Stop-Process -Id $p.Id -Force; exit 1 }
if ((Get-Diag "audiostate $dockTitle").Count -lt 1) { Fallo 'el clip del panel nunca empezo a reproducir' }
else { Ok 'el clip del panel reproduce' }
Start-Sleep -Milliseconds 700
if ((Title).Contains($punto)) { Fallo 'recien abierto, el archivo ya figura con cambios sin guardar (el panel no hace ida y vuelta limpio)' }
else { Ok 'recien abierto, sin cambios sin guardar' }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'BoardTab')
$tabs = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
if ($tabs.Count -ne 3) { Write-Host "FALLO: se esperaban 3 pestanas y hay $($tabs.Count)"; Stop-Process -Id $p.Id -Force; exit 1 }

function Switch-To([int]$index) {
    $antes = (Get-Diag 'switch').Count
    $tabs[$index].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ((Get-Diag 'switch').Count -le $antes -and $sw.ElapsedMilliseconds -lt 5000) { Start-Sleep -Milliseconds 5 }
}

Write-Host ''
Write-Host '=== 1-3. IDA Y VUELTA ENTRE PESTANAS (A, B, C x3) ==='
Switch-To 1; Start-Sleep -Seconds 3      # B arranca sus clips: despues tiene algo que pausar
for ($k = 0; $k -lt 3; $k++) {
    Switch-To 2; Start-Sleep -Milliseconds 700
    Switch-To 0; Start-Sleep -Milliseconds 700
    Switch-To 1; Start-Sleep -Milliseconds 700
}
# Con la pestana VACIA al frente: A y B pausadas; el panel tiene que seguir reproduciendo.
Switch-To 2
$estadoAntes = (Get-Diag "audiostate $dockTitle").Count
Start-Sleep -Seconds 5
$estadoDespues = (Get-Diag "audiostate $dockTitle").Count
Switch-To 0
Start-Sleep -Milliseconds 700

$opensDock   = (Get-Diag "open $dockClip").Count
$freezeDock  = (Get-Diag "freeze $dockTitle").Count
$freezeTabs  = (Get-Diag "freeze $tabTitle").Count
$switches    = (Get-Diag 'switch').Count
Write-Host "  cambios de pestana: $switches | aperturas del clip del panel: $opensDock | congelados del panel: $freezeDock | congelados de pestanas: $freezeTabs"

if ($switches -lt 10) { Fallo "se registraron $switches cambios de pestana; la prueba no cambio de pestana" }
if ($opensDock -ne 1) { Fallo "el clip del panel se abrio $opensDock veces: un cambio de pestana lo RE-MONTO" }
else { Ok 'el clip del panel se abrio UNA sola vez' }
if ($freezeTabs -lt 1) { Fallo 'ningun clip de pestana se congelo: la sonda no esta viendo los congelados (control)' }
else { Ok 'los clips de las pestanas de fondo se congelan (control de la sonda)' }
if ($freezeDock -ne 0) { Fallo "el clip del panel se congelo $freezeDock veces al cambiar de pestana" }
else { Ok 'el clip del panel NUNCA se congelo' }
Write-Host "  audiostate del panel con C al frente: $estadoAntes -> $estadoDespues (5 s)"
if ($estadoDespues - $estadoAntes -lt 1) { Fallo 'con la pestana vacia al frente el panel dejo de reproducir' }
else { Ok 'el panel sigue reproduciendo con otra pestana al frente' }

Write-Host ''
Write-Host '=== 4. NINGUNA VENTANA DE VLC SUELTA (bug #1) ==='
$sueltas = [DockWin32]::Unowned($p.Id, $h)
if ($sueltas.Count -gt 0) { Fallo "ventanas sin duenio: $($sueltas -join ' | ')" }
else { Ok 'todas las ventanas extra son owned por la principal' }

Write-Host ''
Write-Host '=== 5. IR Y VOLVER NO ES UN CAMBIO SIN GUARDAR ==='
Start-Sleep -Milliseconds 700
Write-Host "  titulo: '$(Title)'"
if ((Title).Contains($punto)) { Fallo 'despues de ir y volver el archivo figura modificado' }
else { Ok 'el archivo sigue sin cambios' }

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Remove-Item $board, $diagLog, $dockClip -ErrorAction SilentlyContinue

Write-Host ''
if ($fallas -eq 0) { Write-Host '=== TODO OK ==='; exit 0 }
Write-Host "=== $fallas FALLA(S) ==="
exit 1
