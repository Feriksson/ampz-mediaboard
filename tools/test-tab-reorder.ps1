# Prueba end-to-end de REORDENAR PESTANAS arrastrandolas (y con Ctrl+Shift+RePag/AvPag).
#
# Lo que se juzga es lo caro de equivocarse: reordenar cambia SOLO el orden de la tira. Ninguna
# BoardView se reconstruye ni se re-parenta, VLC no abre nada y ningun clip parpadea. Ver
# MainWindow.MoveTab y Board/TabOrder.cs.
#
# UN .mboard v2 con TRES pestanas (A y B con N sectores de video, C vacia), activa A. Con A
# reproduciendo:
#   1. Un click SIN mover sobre B la ACTIVA y no reordena (el umbral de arrastre existe).
#   2. Arrastre REAL (SendInput) de A hasta el final -> la tira queda B, C, A y el titulo lleva
#      la marca de cambios sin guardar.
#   3. Arrastrarla de vuelta al principio -> A, B, C y la marca se APAGA sola (se compara contra
#      el archivo, no hay flag).
#   4. Soltar en el MISMO lugar (pasado el umbral) no es un cambio.
#   5. Esc a mitad del arrastre CANCELA: vuelve al orden original, aunque la tira ya se habia
#      corrido en vivo.
#   6. Ctrl+Shift+AvPag / RePag mueven la activa un lugar.
#   7. En TODO lo anterior no se creo ningun Media (linea `open` del DiagLog: un Remount la
#      escribiria), ninguna BoardView salio del arbol visual (linea `viewreparent`: re-parentarla
#      la escribiria), el video de A sigue vivo (dos capturas difieren) y no hay ventanas de VLC
#      sueltas (bug #1).
#
# El orden se lee por UI Automation (AutomationId BoardTab, en orden de la tira).
#
# ⚠ NECESITA UNA SESION INTERACTIVA Y DESBLOQUEADA: manda mouse y teclado reales. Si la ventana
#   no se puede traer al frente, sale con 2 ("no se pudo medir"), igual que test-doubleclick.
# ⚠ Coordenadas: el proceso se declara DPI-aware ANTES de medir nada, asi UIA y SetCursorPos
#   hablan en pixeles fisicos. Y antes de arrastrar se confirma con UIA (FromPoint) que abajo
#   del cursor esta la pestaña que se quiere agarrar: si no, la medicion no vale.
#
#   powershell -File tools/test-tab-reorder.ps1
#
# Sale 0 si pasa, 1 si fallo, 2 si no se pudo medir.

param([int]$Sectors = 3, [switch]$KeepLog)

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
public static class Reorder {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    public struct LASTINPUT { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUT li);
    // Cuanto hace que el USUARIO toco el mouse o el teclado. Un arrastre sintetico mientras
    // alguien mueve el mouse de verdad mide la pelea entre los dos, no la app.
    public static int IdleMs() { var li = new LASTINPUT(); li.cbSize = 8; GetLastInputInfo(ref li); return Environment.TickCount - (int)li.dwTime; }
    // SetForegroundWindow a secas lo niega Windows si otra app tiene la ultima entrada del
    // usuario (bloqueo anti robo de foco). Enganchando por un instante la cola de entrada del
    // hilo que tiene el frente, el pedido pasa, sin inyectar teclas en la app del usuario.
    public static bool Front(IntPtr h) {
        uint pid; var fg = GetForegroundWindow();
        uint other = GetWindowThreadProcessId(fg, out pid), me = GetCurrentThreadId();
        bool attached = other != 0 && other != me && AttachThreadInput(me, other, true);
        BringWindowToTop(h); SetForegroundWindow(h);
        if (attached) AttachThreadInput(me, other, false);
        return GetForegroundWindow() == h;
    }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RRECT r);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public static string Text(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
    public static List<string> Unowned(int pid, IntPtr main) {
        var found = new List<string>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && h != main && IsWindowVisible(h) && GetWindow(h, 4) == IntPtr.Zero) found.Add(Text(h));
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static void Down() { mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); }
    public static void Up()   { mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); }
    // Movimiento en pasos, como una mano: un salto de una sola vez no ejercita el reordenado
    // en vivo (cruzar pestaña por pestaña), que es justo lo que se quiere ver.
    public static void Glide(int x0, int y0, int x1, int y1, int steps) {
        for (int i = 1; i <= steps; i++) {
            SetCursorPos(x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps);
            System.Threading.Thread.Sleep(15);
        }
    }
    // Teclas con modificadores. RePag/AvPag son teclas EXTENDIDAS (flag 1): sin el flag, Windows
    // las manda como las del teclado numerico (9 y 3 con BloqNum apagado).
    public static void Chord(byte[] mods, byte key, bool extended) {
        foreach (var m in mods) keybd_event(m, 0, 0, IntPtr.Zero);
        keybd_event(key, 0, extended ? 1u : 0u, IntPtr.Zero);
        keybd_event(key, 0, (extended ? 1u : 0u) | 2u, IntPtr.Zero);
        for (int i = mods.Length - 1; i >= 0; i--) keybd_event(mods[i], 0, 2, IntPtr.Zero);
    }
}
'@
[void][Reorder]::SetProcessDPIAware()

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

# ⚠ Con ConvertTo-Json (backslashes escapados) y LoopEnd FIJO: con LoopEnd=0 el primer tick
# completaria la zona y el board figuraria modificado sin que nadie lo reordene.
$utf8 = New-Object System.Text.UTF8Encoding($false)
$board = Join-Path $env:TEMP 'ampz-reorder.mboard'
$doc = [ordered]@{
    Version = 2; ActiveTab = 0
    Tabs = @(
        [ordered]@{ Name = 'A'; Root = New-Tree $Sectors 0 },
        [ordered]@{ Name = 'B'; Root = New-Tree $Sectors 0 },
        [ordered]@{ Name = 'C'; Root = [ordered]@{ Type = 'sector' } }
    )
}
[System.IO.File]::WriteAllText($board, ($doc | ConvertTo-Json -Depth 40), $utf8)

$diagLog = Join-Path $env:TEMP 'ampz-reorder-diag.log'
Remove-Item $diagLog -ErrorAction SilentlyContinue
function Get-Diag([string]$kind) { if (Test-Path $diagLog) { @(Get-Content $diagLog | Where-Object { $_ -like "$kind *" }) } else { @() } }

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$env:AMPZ_DIAG_LOG = $diagLog
$p = Start-Process -FilePath $exe -ArgumentList "`"$board`"" -PassThru
Remove-Item Env:\AMPZ_DIAG_LOG
$null = $p.Handle

$salida = 1   # pesimista: si el script revienta a mitad, no puede reportar exito
try {
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 250
        $p.Refresh()
        if ($p.HasExited) { Write-Host 'FALLO: el proceso murio al arrancar'; exit 1 }
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $h = $p.MainWindowHandle; break }
    }
    if ($h -eq [IntPtr]::Zero) { Write-Host 'FALLO: la ventana nunca aparecio'; exit 1 }

    $punto = [string][char]0x2022
    function Title { [Reorder]::Text($h) }
    Start-Sleep -Seconds 4
    if ((Title) -eq 'Ampz MediaBoard') { Write-Host 'FALLO: hay un MessageBox arriba.'; exit 1 }

    # Windows puede negar el primer plano (bloqueo anti robo de foco, si el usuario esta usando
    # otra ventana en ese instante): se reintenta un rato antes de declarar "no se pudo medir".
    for ($k = 0; $k -lt 10 -and [Reorder]::GetForegroundWindow() -ne $h; $k++) {
        [void][Reorder]::Front($h)
        Start-Sleep -Milliseconds 500
    }
    if ([Reorder]::GetForegroundWindow() -ne $h) {
        throw 'NO-SE-UBICA: la ventana no pudo pasar al frente (sesion sin escritorio interactivo?)'
    }

    # ⚠ Con alguien usando la maquina, el arrastre sintetico se mezcla con el mouse real y la
    # prueba da rojos que no son de la app (visto: pestañas "no encontradas" bajo el cursor,
    # arrastres cortados). Se espera un escritorio QUIETO; si no llega, no se puede medir.
    $quieto = (Get-Date).AddSeconds(30)
    while ([Reorder]::IdleMs() -lt 3000 -and (Get-Date) -lt $quieto) { Start-Sleep -Milliseconds 250 }
    if ([Reorder]::IdleMs() -lt 3000) { throw 'NO-SE-UBICA: el escritorio esta en uso (entrada del usuario en los ultimos 3 s)' }
    [void][Reorder]::Front($h)
    Start-Sleep -Milliseconds 300

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'BoardTab')
    # Se re-consulta cada vez: el orden de la coleccion de UIA es el de la tira.
    function Get-Tabs { @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) }
    function Order { (Get-Tabs | ForEach-Object { $_.Current.Name }) -join ', ' }
    function Tab([string]$name) { Get-Tabs | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1 }

    # Punto de agarre: a 12px del borde izquierdo (sobre el nombre, lejos del x de cerrar), a
    # media altura. Se CONFIRMA con UIA que ahi abajo esta esa pestaña.
    function Grip([string]$name) {
        $r = (Tab $name).Current.BoundingRectangle
        $pt = [pscustomobject]@{ X = [int]($r.Left + 12); Y = [int]($r.Top + $r.Height / 2); Right = [int]$r.Right }
        $el = [System.Windows.Automation.AutomationElement]::FromPoint((New-Object System.Windows.Point $pt.X, $pt.Y))
        $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
        for ($k = 0; $k -lt 4 -and $el -and $el.Current.AutomationId -ne 'BoardTab'; $k++) { $el = $walker.GetParent($el) }
        if (-not $el -or $el.Current.Name -ne $name) { throw "NO-SE-UBICA: bajo ($($pt.X),$($pt.Y)) no esta la pestana $name" }
        return $pt
    }
    function Wait-Refresh { Start-Sleep -Milliseconds 800 }   # la marca del titulo se refresca cada 500 ms
    function Modified { (Title).Contains($punto) }

    # Firma de la imagen del board: dos capturas separadas tienen que diferir (video vivo).
    function Get-Firma {
        $r = New-Object RRECT; [void][Reorder]::GetWindowRect($h, [ref]$r)
        $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
        $bmp = New-Object System.Drawing.Bitmap $w, $hh
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $hh))
        $suma = 0
        for ($y = [int]($hh * 0.2); $y -lt $hh - 4; $y += [math]::Max(1, [int]($hh / 40))) {
            for ($x = 4; $x -lt $w - 4; $x += [math]::Max(1, [int]($w / 40))) { $suma = ($suma * 31 + $bmp.GetPixel($x, $y).ToArgb()) % 2147483647 }
        }
        $g.Dispose(); $bmp.Dispose()
        return $suma
    }
    function Video-Vivo { $a = Get-Firma; Start-Sleep -Milliseconds 700; return ($a -ne (Get-Firma)) }

    Write-Host ''
    Write-Host "=== 0. ARRANQUE ($Sectors sectores de video en A y en B) ==="
    Write-Host "  titulo: '$(Title)'   pestanas: $(Order)"
    if ((Order) -ne 'A, B, C') { Write-Host 'FALLO: se esperaban A, B, C.'; exit 1 }
    if (Modified) { Fallo 'recien abierto ya figura con cambios' }
    $opens0 = (Get-Diag 'open').Count
    # Linea de base: al arrancar YA hubo uno legitimo (la pestaña en blanco del arranque se
    # descarta al abrir el archivo). Lo que se juzga es lo que agregue reordenar.
    $reparents0 = (Get-Diag 'viewreparent').Count
    Write-Host "  Media creados al abrir: $opens0"

    Write-Host ''
    Write-Host '=== 1. UN CLICK SIN MOVER ACTIVA, NO REORDENA ==='
    $sw0 = (Get-Diag 'switch').Count
    $g = Grip 'B'
    [void][Reorder]::SetCursorPos($g.X, $g.Y); Start-Sleep -Milliseconds 100
    [Reorder]::Down(); Start-Sleep -Milliseconds 60; [Reorder]::Up()
    Start-Sleep -Milliseconds 600
    if ((Get-Diag 'switch').Count -le $sw0) { Fallo 'el click no activo la pestana B' } else { Ok 'el click activo B' }
    if ((Get-Diag 'move').Count -gt 0 -or (Order) -ne 'A, B, C') { Fallo "un click reordeno: $(Order)" } else { Ok 'y no reordeno nada' }
    $g = Grip 'A'
    [void][Reorder]::SetCursorPos($g.X, $g.Y); Start-Sleep -Milliseconds 100
    [Reorder]::Down(); Start-Sleep -Milliseconds 60; [Reorder]::Up()
    Start-Sleep -Seconds 2

    Write-Host ''
    Write-Host '=== 2. ARRASTRAR A HASTA EL FINAL ==='
    $g = Grip 'A'; $fin = (Tab 'C').Current.BoundingRectangle
    [void][Reorder]::SetCursorPos($g.X, $g.Y); Start-Sleep -Milliseconds 100
    [Reorder]::Down(); Start-Sleep -Milliseconds 80
    [Reorder]::Glide($g.X, $g.Y, [int]($fin.Right + 40), $g.Y, 30)
    Start-Sleep -Milliseconds 150
    [Reorder]::Up(); Wait-Refresh
    Write-Host "  orden: $(Order)   titulo: '$(Title)'"
    if ((Order) -ne 'B, C, A') { Fallo "se esperaba B, C, A" } else { Ok 'la tira quedo B, C, A' }
    if (-not (Modified)) { Fallo 'reordenar no marco el archivo como modificado' } else { Ok 'reordenar es un cambio sin guardar' }
    if (-not (Video-Vivo)) { Fallo 'el video de A no sigue vivo despues de arrastrarla' } else { Ok 'el video de A siguio corriendo' }

    Write-Host ''
    Write-Host '=== 3. ARRASTRARLA DE VUELTA AL PRINCIPIO ==='
    $g = Grip 'A'; $ini = (Tab 'B').Current.BoundingRectangle
    [void][Reorder]::SetCursorPos($g.X, $g.Y); Start-Sleep -Milliseconds 100
    [Reorder]::Down(); Start-Sleep -Milliseconds 80
    [Reorder]::Glide($g.X, $g.Y, [int]($ini.Left - 30), $g.Y, 30)
    Start-Sleep -Milliseconds 150
    [Reorder]::Up(); Wait-Refresh
    Write-Host "  orden: $(Order)   titulo: '$(Title)'"
    if ((Order) -ne 'A, B, C') { Fallo 'no volvio a A, B, C' } else { Ok 'volvio a A, B, C' }
    if (Modified) { Fallo 'de vuelta en el orden original sigue marcado como modificado' } else { Ok 'y la marca de cambios se apago sola' }

    Write-Host ''
    Write-Host '=== 4. SOLTAR EN EL MISMO LUGAR NO ES UN CAMBIO ==='
    $drops0 = (Get-Diag 'tabdrag drop').Count
    $g = Grip 'A'
    [void][Reorder]::SetCursorPos($g.X, $g.Y); Start-Sleep -Milliseconds 100
    [Reorder]::Down(); Start-Sleep -Milliseconds 80
    [Reorder]::Glide($g.X, $g.Y, $g.X + 18, $g.Y, 6)
    Start-Sleep -Milliseconds 150
    [Reorder]::Up(); Wait-Refresh
    if ((Get-Diag 'tabdrag drop').Count -le $drops0) { Fallo 'el arrastre corto ni siquiera arranco (no se prueba nada)' }
    elseif ((Order) -ne 'A, B, C' -or (Modified)) { Fallo "soltar en el lugar cambio algo: $(Order) / '$(Title)'" }
    else { Ok 'arranco un arrastre, se solto en el lugar, y nada cambio' }

    Write-Host ''
    Write-Host '=== 5. ESC A MITAD DEL ARRASTRE CANCELA ==='
    $g = Grip 'A'; $fin = (Tab 'C').Current.BoundingRectangle
    [void][Reorder]::SetCursorPos($g.X, $g.Y); Start-Sleep -Milliseconds 100
    [Reorder]::Down(); Start-Sleep -Milliseconds 80
    [Reorder]::Glide($g.X, $g.Y, [int]($fin.Right + 40), $g.Y, 30)
    Start-Sleep -Milliseconds 200
    $enVivo = Order
    [Reorder]::Chord(@(), 0x1B, $false)   # Esc con el boton todavia apretado
    Start-Sleep -Milliseconds 200
    [Reorder]::Up(); Wait-Refresh
    Write-Host "  en vivo, antes del Esc: $enVivo   despues: $(Order)"
    if ($enVivo -ne 'B, C, A') { Fallo 'la tira no se reordeno EN VIVO durante el arrastre' } else { Ok 'la tira se corria en vivo' }
    if ((Order) -ne 'A, B, C' -or (Modified)) { Fallo 'Esc no devolvio el orden original' }
    elseif ((Get-Diag 'tabdrag cancel').Count -lt 1) { Fallo 'no hubo cancelacion registrada' }
    else { Ok 'Esc devolvio A, B, C y no quedo nada modificado' }

    Write-Host ''
    Write-Host '=== 6. CTRL+SHIFT+AVPAG / REPAG ==='
    [Reorder]::Chord(@(0x11, 0x10), 0x22, $true); Wait-Refresh
    $k1 = Order
    [Reorder]::Chord(@(0x11, 0x10), 0x21, $true); Wait-Refresh
    $k2 = Order
    Write-Host "  AvPag: $k1   RePag: $k2"
    if ($k1 -ne 'B, A, C') { Fallo 'Ctrl+Shift+AvPag no movio la activa a la derecha' } else { Ok 'AvPag la movio a la derecha' }
    if ($k2 -ne 'A, B, C' -or (Modified)) { Fallo 'Ctrl+Shift+RePag no la devolvio' } else { Ok 'RePag la devolvio, sin cambios' }

    Write-Host ''
    Write-Host '=== 7. NADA SE RECONSTRUYO ==='
    $opens1 = (Get-Diag 'open').Count
    Write-Host "  Media creados: $opens1 (al abrir: $opens0)   movimientos: $((Get-Diag 'move').Count)"
    if ($opens1 -ne $opens0) { Fallo "reordenar creo $($opens1 - $opens0) Media nuevos: se RECONSTRUYERON vistas (VLC reabrio archivos)" }
    else { Ok 'reordenar no reabrio ningun archivo' }
    # Re-parentar una BoardView (sacarla y volverla a poner en BoardHost) NO reabre archivos y el
    # video lo sobrevive a la vista: sin esta linea, esa mutacion pasaba la prueba en verde. Y NO
    # sirve mirar Unloaded: con Remove + Insert en la misma vuelta WPF ni lo dispara (medido).
    $reparents = (Get-Diag 'viewreparent').Count - $reparents0
    if ($reparents -gt 0) { Fallo "$reparents veces una BoardView cambio de padre: reordenar las RE-PARENTO" }
    else { Ok 'ninguna BoardView salio del arbol visual' }
    if (-not (Video-Vivo)) { Fallo 'el video de la pestana activa quedo congelado o negro' } else { Ok 'el video sigue vivo' }
    $sueltas = [Reorder]::Unowned($p.Id, $h)
    if ($sueltas.Count -gt 0) { Fallo "ventanas sin duenio: $($sueltas -join ' | ')" } else { Ok 'ninguna ventana de VLC suelta' }

    Write-Host ''
    if ($fallas -eq 0) { Write-Host '=== TODO OK ==='; $salida = 0 }
    else { Write-Host "=== $fallas FALLA(S) ===" }
}
catch {
    if ("$_" -like 'NO-SE-UBICA*') { Write-Host "NO SE PUDO MEDIR: $_"; $salida = 2 }
    else { Write-Host "ERROR: $_"; $salida = 1 }
}
finally {
    [Reorder]::Up()
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    if (-not $KeepLog) { Remove-Item $board, $diagLog -ErrorAction SilentlyContinue }
}
exit $salida
