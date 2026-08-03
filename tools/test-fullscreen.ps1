# Prueba end-to-end de PANTALLA COMPLETA (F11).
#
# Por que existe: cambiar WindowStyle en caliente es exactamente el tipo de cosa que puede
# recrear el HWND de la ventana, y esta app hostea un VideoView (una ventana nativa hija con
# VLC dibujando adentro). Si el handle se recreara, el video quedaria en NEGRO sin ningun error
# — el mismo modo de falla del bug #4 del CLAUDE.md. Mirar la ventana y decir "se ve bien" no
# alcanza: aca se mide.
#
# Que verifica:
#   1. F11 hace que la ventana cubra la pantalla ENTERA (no el area de trabajo: la barra de
#      tareas tiene que quedar tapada).
#   2. El video SIGUE DECODIFICANDO en pantalla completa — se comparan dos capturas separadas
#      en el tiempo y tienen que DIFERIR. Una imagen congelada o negra no pasa.
#   3. Esc devuelve la ventana al tamano que tenia antes.
#
#   powershell -File tools/test-fullscreen.ps1
#
# Sale 0 si pasa, 1 si fallo.

param([string]$Clip = '')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'bin\Debug\net10.0-windows\AmpzMediaBoard.exe'
$fallas = 0

function Fallo([string]$m) { Write-Host "  [FALLA] $m"; $script:fallas++ }
function Ok([string]$m)    { Write-Host "  [OK ] $m" }

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public static class Win32 {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
'@

if (-not $Clip) {
    $Clip = Join-Path $env:TEMP 'ampz-loop-clip.mp4'
    if (-not (Test-Path $Clip)) {
        if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
            Write-Host 'No hay ffmpeg para generar el clip. Pasa uno con -Clip <ruta>.'
            exit 1
        }
        ffmpeg -v error -y -f lavfi -i 'testsrc=size=320x240:rate=25:duration=4' -pix_fmt yuv420p $Clip
    }
}

# Un board de un solo sector con el clip adentro, abierto por linea de comandos (el mismo camino
# que el doble click en Explorer).
$board = Join-Path $env:TEMP 'ampz-fullscreen.mboard'
$json = ([ordered]@{ Type='sector'; Path=$Clip; LoopStart=0; LoopEnd=0; LoopEnabled=$true; Volume=0; Muted=$true } | ConvertTo-Json -Compress)
[System.IO.File]::WriteAllText($board, $json, (New-Object System.Text.UTF8Encoding($false)))

Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

$proc = Start-Process $exe -ArgumentList $board -PassThru

# ⚠ Se ESPERA al handle de la ventana en un bucle, no con un Start-Sleep fijo. MainWindowHandle
# viene en cero durante el arranque (WPF todavia no creo la ventana) y el objeto de Start-Process
# lo cachea, asi que un unico chequeo reporta "no levanto" con la app perfectamente viva.
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 250
    $vivo = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if (-not $vivo) { Write-Host 'FALLO: el proceso murio al arrancar'; exit 1 }
    if ($vivo.MainWindowHandle -ne [IntPtr]::Zero) { $h = $vivo.MainWindowHandle; break }
}
if ($h -eq [IntPtr]::Zero) { Write-Host 'FALLO: la ventana nunca aparecio'; exit 1 }

# Un par de segundos mas para que VLC abra el clip y empiece a dibujar.
Start-Sleep -Seconds 3

function Get-Rect { $r = New-Object RECT; [void][Win32]::GetWindowRect($h, [ref]$r); $r }

# Firma de la imagen de la ventana: se usa para detectar si el video sigue vivo. Se muestrea una
# grilla en vez de todos los pixeles — alcanza para distinguir "cambia" de "congelado/negro" y no
# tarda un siglo.
function Get-Firma {
    $r = Get-Rect
    $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
    if ($w -le 0 -or $hh -le 0) { return $null }
    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $hh))
    $suma = 0; $distintos = @{}
    for ($y = [int]($hh * 0.25); $y -lt $hh - 4; $y += [math]::Max(1, [int]($hh / 40))) {
        for ($x = 4; $x -lt $w - 4; $x += [math]::Max(1, [int]($w / 40))) {
            $p = $bmp.GetPixel($x, $y).ToArgb()
            $suma = ($suma * 31 + $p) % 2147483647
            $distintos[$p] = $true
        }
    }
    $g.Dispose(); $bmp.Dispose()
    [pscustomobject]@{ Hash = $suma; Colores = $distintos.Count }
}

[void][Win32]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 500
$wsh = New-Object -ComObject WScript.Shell

Add-Type -AssemblyName System.Windows.Forms
$antes = Get-Rect
$pantalla = [System.Windows.Forms.Screen]::FromHandle($h)
Write-Host ''
Write-Host '=== 1. ANTES DE F11 ==='
Write-Host ("  ventana : {0}x{1}" -f ($antes.Right - $antes.Left), ($antes.Bottom - $antes.Top))
Write-Host ("  pantalla: {0}x{1}   area de trabajo: {2}x{3}" -f $pantalla.Bounds.Width, $pantalla.Bounds.Height, $pantalla.WorkingArea.Width, $pantalla.WorkingArea.Height)

Write-Host ''
Write-Host '=== 2. F11 -> PANTALLA COMPLETA ==='
$wsh.SendKeys('{F11}')
Start-Sleep -Milliseconds 1200
$full = Get-Rect
$anchoFull = $full.Right - $full.Left; $altoFull = $full.Bottom - $full.Top
Write-Host ("  ventana : {0}x{1}  en ({2},{3})" -f $anchoFull, $altoFull, $full.Left, $full.Top)

if ($anchoFull -ge $pantalla.Bounds.Width -and $altoFull -ge $pantalla.Bounds.Height) {
    Ok 'cubre la pantalla ENTERA (barra de tareas tapada)'
} elseif ($altoFull -ge $pantalla.WorkingArea.Height) {
    Fallo 'solo cubre el AREA DE TRABAJO: la barra de tareas quedo encima del board'
} else {
    Fallo "la ventana no se maximizo ($anchoFull x $altoFull)"
}

Write-Host ''
Write-Host '=== 3. EL VIDEO SOBREVIVE AL CAMBIO DE WindowStyle ==='
$f1 = Get-Firma
Start-Sleep -Milliseconds 900
$f2 = Get-Firma
Write-Host ("  captura 1: {0} colores distintos" -f $f1.Colores)
Write-Host ("  captura 2: {0} colores distintos" -f $f2.Colores)

if ($f1.Colores -le 2) {
    Fallo 'la ventana esta en un color plano: el video quedo en NEGRO (se recreo el HWND)'
} elseif ($f1.Hash -eq $f2.Hash) {
    Fallo 'la imagen no cambio entre dos capturas: el video quedo CONGELADO'
} else {
    Ok 'el video sigue decodificando en pantalla completa'
}

Write-Host ''
Write-Host '=== 4. Esc -> VUELVE COMO ESTABA ==='
$wsh.SendKeys('{ESC}')
Start-Sleep -Milliseconds 1200
$vuelta = Get-Rect
$anchoV = $vuelta.Right - $vuelta.Left; $altoV = $vuelta.Bottom - $vuelta.Top
Write-Host ("  ventana : {0}x{1}" -f $anchoV, $altoV)

if ($anchoV -eq ($antes.Right - $antes.Left) -and $altoV -eq ($antes.Bottom - $antes.Top)) {
    Ok 'volvio EXACTAMENTE al tamano que tenia'
} else {
    Fallo 'no volvio al tamano original'
}

$f3 = Get-Firma
if ($f3.Colores -le 2) { Fallo 'al salir de pantalla completa el video quedo en negro' }
else { Ok 'el video sigue vivo al salir' }

# ── El caso sutil ──────────────────────────────────────────────────────────────────────────
# Entrar en pantalla completa desde una ventana YA MAXIMIZADA. Sin el paso por WindowState.Normal
# que hace EnterFullScreen, Windows le deja el rect que ya tenia (el AREA DE TRABAJO) y la barra
# de tareas queda encima del board. Se ve como "casi pantalla completa" y es de esos errores que
# uno le echa al monitor. Este bloque es el que lo caza.
Write-Host ''
Write-Host '=== 5. F11 DESDE UNA VENTANA YA MAXIMIZADA ==='
[void][Win32]::ShowWindow($h, 3)   # SW_MAXIMIZE
Start-Sleep -Milliseconds 1000
$max = Get-Rect
Write-Host ("  maximizada: {0}x{1}" -f ($max.Right - $max.Left), ($max.Bottom - $max.Top))

[void][Win32]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 300
$wsh.SendKeys('{F11}')
Start-Sleep -Milliseconds 1200
$full2 = Get-Rect
$alto2 = $full2.Bottom - $full2.Top
Write-Host ("  con F11   : {0}x{1}" -f ($full2.Right - $full2.Left), $alto2)

if ($alto2 -ge $pantalla.Bounds.Height) {
    Ok 'cubre la pantalla entera tambien viniendo de maximizada'
} else {
    Fallo "quedo en el area de trabajo ($alto2 px de alto, la pantalla mide $($pantalla.Bounds.Height)): falta el paso por WindowState.Normal"
}

$wsh.SendKeys('{ESC}')
Start-Sleep -Milliseconds 1000
$vuelta2 = Get-Rect
if (($vuelta2.Bottom - $vuelta2.Top) -eq ($max.Bottom - $max.Top)) {
    Ok 'al salir vuelve a MAXIMIZADA, que es como estaba'
} else {
    Fallo 'al salir no volvio al estado maximizado'
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Remove-Item $board -ErrorAction SilentlyContinue

Write-Host ''
if ($fallas -eq 0) { Write-Host '=== TODO OK ==='; exit 0 }
Write-Host "=== $fallas FALLA(S) ==="
exit 1
