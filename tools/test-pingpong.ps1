# Prueba end-to-end del LOOP IDA Y VUELTA (ping-pong) con la app corriendo.
#
# Que verifica (por DiagLog + UI Automation, ver DiagLog.cs):
#   1. Un .mboard con un sector en ping-pong (zona 2,0-5,0 s) abre, genera el archivo ida y vuelta
#      con el ffmpeg que viaja con la app, y el sector pasa a reproducir EL GENERADO (linea
#      `play ... src=...\pingpong\<clave>.mkv`).
#   2. Ninguna ventana de VLC suelta en ningun momento (bugs 1 y 6: el cambio original -> generado
#      es un player nuevo sobre la MISMA ventana, y tiene que esperar a que el viejo la suelte).
#   3. El playhead que ve el usuario (tiempo ORIGINAL, lineas `pppos`) va PARA ADELANTE hasta
#      cerca de B y despues PARA ATRAS hasta cerca de A, sin salirse nunca de la zona. Si el
#      tiempo del generado se mostrara sin traducir, iria de 0 a 6 s: fuera de la zona.
#   4. El video se mueve ADENTRO de la app (dos capturas del board difieren).
#   5. Apagar con el boton (AutomationId PingPongButton) vuelve al ORIGINAL: `pingpong switch
#      original` + un `play ... src=<clip original>`, y dejan de salir lineas `pppos`.
#
# ⚠ Necesita ffmpeg JUNTO al exe (bin\Debug\...\ffmpeg\ffmpeg.exe): lo copia el .csproj si existe
#   third_party\ffmpeg (tools/fetch-ffmpeg.ps1). Sin el, la feature esta deshabilitada y la prueba
#   sale 1 avisando. El clip de prueba se genera con ESE ffmpeg (no hace falta uno en el PATH).
# ⚠ La captura (paso 3) pide el board AL FRENTE y el escritorio desbloqueado: si otra ventana lo
#   tapa (Windows no deja que un proceso de fondo robe el foco), ese paso queda SIN MEDIR y, si
#   todo lo demas paso, sale 2 ("no se pudo medir"): ni verde ni rojo. El resto es UIA + DiagLog
#   y no depende de la pantalla.
# ⚠ El .mboard se genera con ConvertTo-Json: un path de Windows tipeado a mano en un JSON lleva
#   los backslashes sin escapar y la app lo rechazaria como corrupto.
#
#   powershell -File tools/test-pingpong.ps1
#
# Visto ROJO con el cambio al generado anulado (RunPingPongAsync sin SwitchPlayback) y con el
# playhead sin traducir (PositionMs = tiempo del generado). Sale 0 si pasa, 1 si falla.

param([switch]$KeepOpen)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'bin\Debug\net10.0-windows\AmpzMediaBoard.exe'
$ff   = Join-Path (Split-Path $exe) 'ffmpeg\ffmpeg.exe'
if (-not (Test-Path $exe)) { Write-Host "No esta compilado: $exe"; exit 1 }
if (-not (Test-Path $ff))  { Write-Host "No hay ffmpeg junto al exe ($ff). Corre tools/fetch-ffmpeg.ps1 y recompila."; exit 1 }

$fallas = 0
function Fallo([string]$m) { Write-Host "  [FALLA] $m"; $script:fallas++ }
function Ok([string]$m)    { Write-Host "  [OK ] $m" }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public struct PRECT { public int Left, Top, Right, Bottom; }
public static class PpWin32 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out PRECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(PT p);
    // Lo que se VE en el centro de la ventana, es nuestro? (los HWND hijos de VLC son del mismo proceso)
    public static bool OnTop(int pid, PRECT r) {
        var pt = new PT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        uint p; GetWindowThreadProcessId(WindowFromPoint(pt), out p);
        return p == pid;
    }
    public static string Text(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
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

# Clip con movimiento en TODO el cuadro y con audio (el generado tiene que salir sin el).
$clip = Join-Path $env:TEMP 'ampz-pingpong-clip.mp4'
if (-not (Test-Path $clip)) {
    & $ff -v error -y -f lavfi -i 'testsrc2=size=640x360:rate=30' -f lavfi -i 'sine=f=300' -t 20 -c:v mpeg4 -q:v 3 -g 60 -c:a aac -shortest $clip
    if ($LASTEXITCODE -ne 0) { Write-Host 'No se pudo generar el clip de prueba.'; exit 1 }
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$board = Join-Path $env:TEMP 'ampz-pingpong.mboard'
$doc = [ordered]@{
    Version = 2; ActiveTab = 0
    Tabs = @([ordered]@{ Name = 'P'; Root = [ordered]@{
        Type = 'sector'; Path = $clip; LoopStart = 2000; LoopEnd = 5000; LoopEnabled = $true
        Volume = 0; Muted = $true; PingPong = $true } })
}
[System.IO.File]::WriteAllText($board, ($doc | ConvertTo-Json -Depth 20), $utf8)

$diagLog = Join-Path $env:TEMP 'ampz-pingpong-diag.log'
Remove-Item $diagLog -ErrorAction SilentlyContinue
function Get-Lines { if (Test-Path $diagLog) { @(Get-Content $diagLog) } else { @() } }

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
if ([PpWin32]::Text($h) -eq 'Ampz MediaBoard') { Write-Host 'FALLO: hay un MessageBox arriba (board rechazado?).'; Stop-Process -Id $p.Id -Force; exit 1 }

$sueltas = 0
function Watch-Orphans { $o = [PpWin32]::Unowned($p.Id, $h); if ($o.Count -gt 0) { $script:sueltas++; Write-Host "  ventana suelta: '$([PpWin32]::Text($o[0]))'" } }

# Captura del area del board (debajo de la barra) -> firma para comparar dos instantes.
function Get-Hash {
    $r = New-Object PRECT; [void][PpWin32]::GetWindowRect($h, [ref]$r)
    $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $hh))
    # ⚠ Hash de TODOS los pixeles, no de una grilla rala: testsrc2 es casi todo barras de color
    # QUIETAS y lo que se mueve es chico (la diagonal, los cuadraditos, el reloj). Una grilla de
    # 40x24 puntos podia no tocar nada que se moviera en 600 ms (visto: 2 de 3 corridas "iguales"
    # con el video andando, comprobado con una captura de pantalla).
    $data = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $w, $hh),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $hh)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    $g.Dispose(); $bmp.Dispose()
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try { [Convert]::ToBase64String($md5.ComputeHash($bytes)) } finally { $md5.Dispose() }
}

Write-Host ''
Write-Host '=== 1. el sector pasa a reproducir el archivo IDA Y VUELTA ==='
$playGen = $null
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 20 -and -not $playGen) {
    Start-Sleep -Milliseconds 200
    Watch-Orphans
    $playGen = Get-Lines | Where-Object { $_ -like 'play *src=*\pingpong\*.mkv' } | Select-Object -First 1
}
$ready = Get-Lines | Where-Object { $_ -like 'pingpong ready *' } | Select-Object -First 1
if ($ready) { Ok "generado: $($ready.Substring(15))" }
if ($playGen) { Ok "arranco el generado ($([math]::Round($sw.Elapsed.TotalSeconds,1)) s desde el arranque)" }
else {
    Fallo 'el sector nunca reprodujo el archivo generado'
    Get-Lines | Where-Object { $_ -like 'pingpong*' -or $_ -like 'play *' } | ForEach-Object { Write-Host "    $_" }
}

Write-Host ''
Write-Host '=== 2. el playhead (tiempo ORIGINAL) va y vuelve dentro de la zona 2000-5000 ==='
# Se mira una ventana de ~7 s: con d = 3 s, una vuelta completa (ida + vuelta) son 6 s.
for ($i = 0; $i -lt 35; $i++) { Start-Sleep -Milliseconds 200; Watch-Orphans }
$pos = @(Get-Lines | Where-Object { $_ -like 'pppos *' } | ForEach-Object { [double]($_.Split(' ')[2]) })
Write-Host "  muestras: $($pos.Count)   min $(($pos | Measure-Object -Minimum).Minimum)   max $(($pos | Measure-Object -Maximum).Maximum)"
if ($pos.Count -lt 20) { Fallo "muy pocas muestras de posicion ($($pos.Count))" }
else {
    $fuera = @($pos | Where-Object { $_ -lt 1950 -or $_ -gt 5050 })
    if ($fuera.Count -gt 0) { Fallo "el playhead salio de la zona: $($fuera[0..([math]::Min(4,$fuera.Count-1))] -join ', ')" } else { Ok 'nunca sale de la zona [A, B]' }
    $sube = 0; $baja = 0
    for ($i = 1; $i -lt $pos.Count; $i++) { $dlt = $pos[$i] - $pos[$i - 1]; if ($dlt -gt 40) { $sube++ } elseif ($dlt -lt -40) { $baja++ } }
    Write-Host "  pasos hacia adelante: $sube   hacia atras: $baja"
    if ($sube -lt 8) { Fallo 'el playhead casi no avanza' } else { Ok 'avanza (ida)' }
    if ($baja -lt 8) { Fallo 'el playhead nunca retrocede: no hay vuelta' } else { Ok 'retrocede (vuelta)' }
    $max = ($pos | Measure-Object -Maximum).Maximum; $min = ($pos | Measure-Object -Minimum).Minimum
    if ($max -lt 4500 -or $min -gt 2500) { Fallo "no recorre la zona entera (min $min, max $max)" } else { Ok "recorre la zona (llega cerca de B y vuelve cerca de A)" }
}

Write-Host ''
Write-Host '=== 3. el video se mueve ADENTRO de la app ==='
# ⚠ Se captura la PANTALLA: si otra ventana tapa el board, se mide esa otra ventana (visto: un
# navegador encima -> dos capturas identicas -> rojo falso). Se trae al frente y, si igual hay
# algo encima, la prueba sale 2 ("no se pudo medir") en vez de mentir un rojo.
[void][PpWin32]::ShowWindow($h, 9); [void][PpWin32]::SetForegroundWindow($h); Start-Sleep -Milliseconds 400
$rr = New-Object PRECT; [void][PpWin32]::GetWindowRect($h, [ref]$rr)
$sinMedir = $false
if (-not [PpWin32]::OnTop($p.Id, $rr)) {
    Write-Host '  [ -- ] otra ventana tapa el board y Windows no deja traerlo al frente: SIN MEDIR (el resto sigue)'
    $sinMedir = $true
} else {
    try {
        $h1 = Get-Hash; Start-Sleep -Milliseconds 600; $h2 = Get-Hash
        if ($h1 -eq $h2) { Fallo 'dos capturas iguales: el generado no se ve avanzar adentro' } else { Ok 'dos capturas del board difieren' }
    } catch {
        Write-Host "  [ -- ] no se pudo capturar la pantalla ($($_.Exception.Message)): SIN MEDIR"
        $sinMedir = $true
    }
}

Write-Host ''
Write-Host '=== 4. apagar con el boton vuelve al ORIGINAL ==='
$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$AE = [System.Windows.Automation.AutomationElement]
$btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'PingPongButton')))
if ($null -eq $btn) { Fallo 'no encontre el boton PingPongButton' }
else {
    $antes = (Get-Lines).Count
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $volvio = $null
    for ($i = 0; $i -lt 40 -and -not $volvio; $i++) {
        Start-Sleep -Milliseconds 200; Watch-Orphans
        $nuevas = @(Get-Lines | Select-Object -Skip $antes)
        if ($nuevas -like 'pingpong switch original*') {
            $volvio = $nuevas | Where-Object { $_ -like "play *src=$clip" } | Select-Object -First 1
        }
    }
    if ($volvio) { Ok 'volvio a reproducir el clip original' } else { Fallo 'apagar no devolvio el clip original' }
    Start-Sleep -Milliseconds 800
    $n1 = @(Get-Lines | Where-Object { $_ -like 'pppos *' }).Count
    Start-Sleep -Milliseconds 1200
    $n2 = @(Get-Lines | Where-Object { $_ -like 'pppos *' }).Count
    if ($n2 -ne $n1) { Fallo 'siguen saliendo posiciones de ping-pong despues de apagarlo' } else { Ok 'el ping-pong quedo apagado' }
}

Write-Host ''
if ($sueltas -gt 0) { Fallo "aparecio una ventana de VLC sin duenio fuera de la app ($sueltas veces)" } else { Ok 'ninguna ventana de VLC suelta en toda la prueba' }

if (-not $KeepOpen) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
Write-Host ''
if ($fallas -gt 0) { Write-Host "RESULTADO: $fallas falla(s)"; exit 1 }
# Todo lo demas paso, pero la captura no se pudo hacer: ni verde ni rojo.
if ($sinMedir) { Write-Host 'RESULTADO: SIN MEDIR la captura (escritorio ocupado o bloqueado); el resto OK'; exit 2 }
Write-Host 'RESULTADO: OK'
exit 0
