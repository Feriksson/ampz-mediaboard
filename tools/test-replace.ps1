# Prueba de regresion: REEMPLAZAR el clip de un sector OCUPADO no puede sacar el video afuera.
#
# Bug reportado (2026-09-30): soltar un video sobre un sector vacio andaba; soltar OTRO sobre el
# mismo sector ya ocupado abria una VENTANA PROPIA de VLC, flotando fuera de la app. Es el bug #1
# del CLAUDE.md ("Play() sin HWND -> VLC abre su propia ventana") volviendo por otro camino.
# Causa (medida, no supuesta): la ventana suelta mostraba el clip NUEVO, y el player nuevo SI
# tenia el HWND bien puesto (linea `play ... hwnd=` del DiagLog, el mismo del clip viejo). Con
# el log de libvlc prendido: "drawable: HWND 0x... is busy". El Stop() del player viejo corre en
# otro hilo desde el cierre rapido (VlcEngine.Release) y su vout seguia siendo duenio de la
# ventana; libvlc no comparte un HWND ocupado y cae a una ventana propia. Arreglo: el Play()
# espera a que terminen las liberaciones (SectorView.StartWhenSurfaceReady).
# Visto ROJO 2 de 2 corridas con el arreglo revertido (ventana 'VLC (Direct3D11 output)' con
# owner 0, azul = clip B); VERDE con el arreglo, 5 reemplazos seguidos sin ventana suelta.
#
# ⚠ Los controles del dialogo le llegan a UIA como Pane sin patrones: se los encuentra por UIA
#   y se les habla con WM_SETTEXT / BM_CLICK. Ver Replace-With.
#
# Que hace:
#   1. Abre un board de UN sector con el clip A (mandelbrot) y lo deja reproducir.
#   2. Reemplaza el clip por el B (azul, con un cuadro blanco que se mueve) en el MISMO sector,
#      por el boton "..." de la cabecera + el dialogo de archivo, manejados por UI Automation.
#      Termina en SectorNode.Adopt -> Load sobre un sector ocupado: el mismo camino que el drop.
#   3. Durante unos segundos (la ventana suelta puede aparecer tarde) busca ventanas top-level
#      VISIBLES del proceso SIN DUENIO (owner = 0) que no sean la principal. Si aparece una, la
#      fotografia y dice de QUE clip es por el color: A es multicolor, B es azul.
#   4. El clip B esta decodificando ADENTRO de la app: el DiagLog registro su apertura, y dos
#      capturas del area del board separadas en el tiempo difieren y son mayormente azules.
#
# ⚠ Se maneja por UI Automation, NO con SendKeys/AppActivate (no llegan de forma confiable, ver
#   CLAUDE.md). Sin clicks ni teclas reales, pero las CAPTURAS
#   de pantalla (color de la ventana suelta y del board) piden un escritorio desbloqueado. El dialogo
#   de archivo es el comun de Windows: el "Nombre" es el Edit 1148 y "Abrir" el boton 1.
# ⚠ El .mboard se genera con ConvertTo-Json (backslashes escapados) y con LoopEnd FIJO: ver
#   test-close.ps1.
#
#   powershell -File tools/test-replace.ps1
#   powershell -File tools/test-replace.ps1 -Rounds 5   (impar: la ultima vuelta deja el clip B)
#
# Sale 0 si pasa, 1 si fallo.

param([ValidateScript({ $_ % 2 -eq 1 })][int]$Rounds = 3, [int]$WatchSeconds = 4, [switch]$KeepOpen)

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
public struct RRECT { public int Left, Top, Right, Bottom; }
public static class ReplWin32 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RRECT r);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] public static extern IntPtr SendMessageText(IntPtr h, uint m, IntPtr w, string l);
    public static string Text(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
    public static string Class(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    // Ventanas top-level VISIBLES del proceso sin duenio (GW_OWNER = 4), excepto la principal.
    public static List<IntPtr> Unowned(int pid, IntPtr main) {
        var found = new List<IntPtr>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && h != main && IsWindowVisible(h) && GetWindow(h, 4) == IntPtr.Zero) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { Write-Host 'No hay ffmpeg para generar los clips.'; exit 1 }
# A: multicolor y animado en toda la imagen (el mismo de test-close/test-tabs).
$clipA = Join-Path $env:TEMP 'ampz-close-clip.mp4'
if (-not (Test-Path $clipA)) { ffmpeg -v error -y -f lavfi -i 'mandelbrot=size=640x360:rate=30' -t 60 -pix_fmt yuv420p $clipA }
# B: AZUL con un cuadro blanco que se mueve. El color es lo que permite decir de QUE clip es una
# ventana suelta; el cuadro, que la imagen cambie (video vivo y no un frame congelado).
$clipB = Join-Path $env:TEMP 'ampz-replace-blue.mp4'
if (-not (Test-Path $clipB)) {
    # ⚠ El cuadro va por OVERLAY y no por drawbox: drawbox evalua x UNA vez y el cuadro quedaba
    # quieto (visto: dos capturas identicas con el clip reproduciendo). overlay evalua por frame.
    ffmpeg -v error -y -f lavfi -i 'color=c=0x1030E0:size=640x360:rate=30' -f lavfi -i 'color=c=white:size=80x80:rate=30' `
        -filter_complex "[0][1]overlay=x='mod(t*240,560)':y=140:shortest=1" -t 60 -pix_fmt yuv420p $clipB
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$board = Join-Path $env:TEMP 'ampz-replace.mboard'
$doc = [ordered]@{
    Version = 2; ActiveTab = 0
    Tabs = @([ordered]@{ Name = 'R'; Root = [ordered]@{ Type = 'sector'; Path = $clipA; LoopStart = 0; LoopEnd = 50000; LoopEnabled = $true; Volume = 0; Muted = $true } })
}
[System.IO.File]::WriteAllText($board, ($doc | ConvertTo-Json -Depth 20), $utf8)

$diagLog = Join-Path $env:TEMP 'ampz-replace-diag.log'
Remove-Item $diagLog -ErrorAction SilentlyContinue
function Get-Opens([string]$needle) { if (Test-Path $diagLog) { @(Get-Content $diagLog | Where-Object { $_ -like "open *$needle" }).Count } else { 0 } }

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
if ([ReplWin32]::Text($h) -eq 'Ampz MediaBoard') { Write-Host 'FALLO: hay un MessageBox arriba.'; Stop-Process -Id $p.Id -Force; exit 1 }

# Captura de un rect de pantalla -> color promedio + firma (para "cambio" entre dos capturas).
function Get-Shot([IntPtr]$win, [double]$top = 0.0) {
    $r = New-Object RRECT; [void][ReplWin32]::GetWindowRect($win, [ref]$r)
    $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
    if ($w -le 8 -or $hh -le 8) { return [pscustomobject]@{ R = 0; G = 0; B = 0; Hash = 0; W = $w; H = $hh } }
    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $hh))
    $sr = 0; $sg = 0; $sb = 0; $n = 0; $hash = 0
    for ($y = [int]($hh * $top) + 4; $y -lt $hh - 4; $y += [math]::Max(1, [int]($hh / 40))) {
        for ($x = 4; $x -lt $w - 4; $x += [math]::Max(1, [int]($w / 40))) {
            $c = $bmp.GetPixel($x, $y); $sr += $c.R; $sg += $c.G; $sb += $c.B; $n++
            $hash = ($hash * 31 + $c.ToArgb()) % 2147483647
        }
    }
    $g.Dispose(); $bmp.Dispose()
    [pscustomobject]@{ R = [int]($sr / $n); G = [int]($sg / $n); B = [int]($sb / $n); Hash = $hash; W = $w; H = $hh }
}
# "Azul" = el clip B: el canal azul le saca amplia ventaja a los otros dos.
function Is-Blue($s) { $s.B -gt 120 -and $s.B -gt 2 * $s.R -and $s.B -gt 2 * $s.G }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

# Abre el dialogo de archivo del sector y elige $path. Vuelve cuando el dialogo se cerro.
function Replace-With([string]$path) {
    $browse = $root.FindFirst($TS::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, [string][char]0x2026)))
    if ($null -eq $browse) { throw 'no encontre el boton "..." de la cabecera del sector' }
    $browse.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    # El dialogo es top-level del mismo proceso, clase #32770.
    $dlg = $null
    for ($i = 0; $i -lt 60 -and $null -eq $dlg; $i++) {
        Start-Sleep -Milliseconds 200
        # UIA cuelga las ventanas OWNED debajo de su duenia, no del escritorio: se busca en los dos.
        $cls = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, '#32770')
        $dlg = $root.FindFirst($TS::Children, $cls)
        if ($null -eq $dlg) {
            $cands = $AE::RootElement.FindAll($TS::Children,
                (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)))
            foreach ($c in $cands) { if ($c.Current.ClassName -eq '#32770') { $dlg = $c } }
        }
    }
    if ($null -eq $dlg) { throw 'el dialogo de archivo nunca aparecio' }

    # ⚠ Los controles clasicos del dialogo (el Edit del Nombre, el boton Abrir) le llegan a UIA
    # desde PowerShell 5.1 como Pane SIN patrones: ni ValuePattern ni InvokePattern. Se usa UIA
    # solo para ENCONTRARLOS (por AutomationId + clase) y se les habla con mensajes de Win32.
    function Find-Hwnd([string]$id, [string]$cls) {
        $cond = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)),
            (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, $cls)))
        for ($i = 0; $i -lt 30; $i++) {
            $e = $dlg.FindFirst($TS::Descendants, $cond)
            if ($null -ne $e -and $e.Current.NativeWindowHandle -ne 0) { return [IntPtr]$e.Current.NativeWindowHandle }
            Start-Sleep -Milliseconds 200
        }
        throw "no encontre el control $id/$cls del dialogo"
    }
    $edit = Find-Hwnd '1148' 'Edit'
    $open = Find-Hwnd '1' 'Button'
    [void][ReplWin32]::SendMessageText($edit, 0x000C, [IntPtr]::Zero, $path)   # WM_SETTEXT
    [void][ReplWin32]::PostMessage($open, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) # BM_CLICK
    for ($i = 0; $i -lt 50; $i++) {
        Start-Sleep -Milliseconds 100
        if (-not [ReplWin32]::IsWindow([IntPtr]$dlg.Current.NativeWindowHandle)) { return }
    }
    throw 'el dialogo de archivo no se cerro al pedir Abrir'
}

# Mira $seconds segundos si aparece alguna ventana suelta. Devuelve la primera (o Zero).
function Watch-Orphans([int]$seconds) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $seconds) {
        $o = [ReplWin32]::Unowned($p.Id, $h)
        if ($o.Count -gt 0) { Start-Sleep -Milliseconds 400; return $o[0] }
        Start-Sleep -Milliseconds 100
    }
    return [IntPtr]::Zero
}

Start-Sleep -Seconds 3
$antes = Get-Shot $h 0.2
Write-Host ''
Write-Host "=== 0. clip A reproduciendo (color promedio del board: $($antes.R),$($antes.G),$($antes.B)) ==="
if ((Watch-Orphans 1) -ne [IntPtr]::Zero) { Fallo 'ya hay una ventana suelta ANTES de reemplazar (el bug es otro)' }

# Se alterna A -> B -> A -> B...: cada vuelta es "soltar otro clip sobre un sector ocupado".
$suelta = $false
for ($k = 1; $k -le $Rounds -and -not $suelta; $k++) {
    $destino = if ($k % 2 -eq 1) { $clipB } else { $clipA }
    $nombre  = if ($k % 2 -eq 1) { 'B (azul)' } else { 'A (multicolor)' }
    Write-Host ''
    Write-Host "=== $k. reemplazo por el clip $nombre en el MISMO sector ==="
    Replace-With $destino
    $o = Watch-Orphans $WatchSeconds
    if ($o -ne [IntPtr]::Zero) {
        $s = Get-Shot $o
        $quien = if (Is-Blue $s) { 'el NUEVO (B, azul)' } elseif ($destino -eq $clipB) { 'el VIEJO (A)' } else { 'el VIEJO (B)?' }
        Write-Host "  ventana suelta: '$([ReplWin32]::Text($o))' clase=$([ReplWin32]::Class($o)) $($s.W)x$($s.H) color=$($s.R),$($s.G),$($s.B) -> parece $quien"
        Fallo "reemplazar el clip abrio una ventana de VLC SIN DUENIO fuera de la app (bug #1)"
        $suelta = $true
    } else {
        Ok "ninguna ventana suelta en $WatchSeconds s"
    }
}

Write-Host ''
Write-Host '=== clip B decodificando ADENTRO de la app ==='
$opensB = Get-Opens 'ampz-replace-blue.mp4'
if ($opensB -lt 1) { Fallo 'el DiagLog no registro la apertura del clip B' } else { Ok "clip B abierto ($opensB vez/veces)" }
$s1 = Get-Shot $h 0.2; Start-Sleep -Milliseconds 700; $s2 = Get-Shot $h 0.2
Write-Host "  color del board: $($s2.R),$($s2.G),$($s2.B)"
if (-not (Is-Blue $s2)) { Fallo 'el area del board no es la del clip B (azul): el video no esta adentro' }
elseif ($s1.Hash -eq $s2.Hash) { Fallo 'dos capturas iguales: el clip B no avanza adentro de la app' }
else { Ok 'el clip B se ve y avanza adentro de la app' }

# -KeepOpen deja la app abierta para mirarla a mano despues de una falla.
if (-not $KeepOpen) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
Write-Host ''
if ($fallas -gt 0) { Write-Host "RESULTADO: $fallas falla(s)"; exit 1 }
Write-Host 'RESULTADO: OK'
exit 0
