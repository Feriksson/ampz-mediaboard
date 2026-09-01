# Prueba end-to-end del DOBLE CLICK EN EL SECTOR = copiar el path al portapapeles.
#
# No simula nada: levanta la app de verdad con un board de verdad, manda un doble click REAL
# con SendInput y despues LEE EL PORTAPAPELES. Si lo que quedo ahi no es el path del clip, fallo.
#
# Ademas MIDE la limitacion del HWND: el mismo doble click sobre el area de video (que es una
# ventana nativa de VLC, no WPF) se reporta aparte. Ese resultado no hace fallar la prueba, la
# DOCUMENTA: si algun dia se migra a custom rendering tiene que empezar a copiar tambien ahi.
#
#   powershell -File tools/test-doubleclick.ps1
#
# ⚠ NECESITA UNA SESION INTERACTIVA Y DESBLOQUEADA. Manda clicks reales y usa el portapapeles;
# las dos cosas viven en el escritorio interactivo. Con la pantalla bloqueada, en un servicio o
# en una sesion sin escritorio, el portapapeles devuelve "acceso denegado" y no hay nada que
# medir. Ese caso NO es una falla del producto y por eso tiene codigo propio.
#
# Sale 0 si pasa, 1 si fallo, 2 si NO SE PUDO MEDIR (sin acceso al escritorio).

param([string]$Clip = '')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'bin\Debug\net10.0-windows\AmpzMediaBoard.exe'

if (-not (Test-Path $exe)) { Write-Host "No esta compilado: $exe"; exit 1 }

if (-not $Clip) {
    # ⚠ Clip PROPIO y no el 'ampz-loop-clip.mp4' que comparten las otras pruebas, y el motivo es
    # el corazon de esta: hay que comprobar que abajo del cursor hay video CORRIENDO mirando si el
    # pixel cambia. El patron testsrc tiene el centro ESTATICO -> el pixel no cambiaba nunca y la
    # prueba se acusaba a si misma de no tener video. mandelbrot se mueve en TODA la imagen.
    $Clip = Join-Path $env:TEMP 'ampz-anim-clip.mp4'
    if (-not (Test-Path $Clip)) {
        if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
            Write-Host 'No hay ffmpeg para generar el clip. Pasa uno con -Clip <ruta>.'; exit 1
        }
        ffmpeg -v error -y -f lavfi -i 'mandelbrot=size=320x240:rate=25' -t 4 -pix_fmt yuv420p $Clip
    }
}

# ⚠ El .mboard se genera con ConvertTo-Json y NUNCA a mano: un path de Windows tipeado dentro de
# un JSON lleva los backslashes sin escapar -> JSON invalido -> la app lo rechaza como corrupto y
# la prueba termina midiendo el rechazo. Ya paso al escribir test-missing.ps1.
$board = Join-Path $env:TEMP 'ampz-dblclick.mboard'
@{ Type = 'sector'; Path = $Clip; LoopStart = 0; LoopEnd = 0; LoopEnabled = $true; Volume = 0; Muted = $true } |
    ConvertTo-Json | Set-Content -Path $board -Encoding UTF8

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public static class Raton {
    // Color de UN pixel de la pantalla. Es como se comprueba que abajo del cursor hay video
    // DE VERDAD y no un rectangulo negro: testsrc esta animado, asi que dos muestras separadas
    // en el tiempo tienen que DIFERIR.
    public static string Pixel(int x, int y) {
        using (var bmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(bmp)) {
            g.CopyFromScreen(x, y, 0, 0, new Size(1, 1));
            var c = bmp.GetPixel(0, 0);
            return c.R + "," + c.G + "," + c.B;
        }
    }
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public static void DobleClick(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
'@

# ⚠ Se comprueba el ACCESO al portapapeles ANTES de levantar la app. Sin esto, la prueba abre
# la ventana, manda los clicks y recien ahi revienta — y el error que salia mandaba a buscar una
# app que estuviera "reteniendo" el portapapeles, que es una pista falsa: GetOpenClipboardWindow
# devuelve 0 (nadie lo tiene). Lo que falta es el escritorio interactivo, no un turno.
try { Set-Clipboard -Value 'ampz-sonda' }
catch {
    Write-Host 'NO SE PUDO MEDIR: no hay acceso al portapapeles.'
    Write-Host 'Esta prueba necesita una sesion interactiva y DESBLOQUEADA (manda clicks reales).'
    Write-Host 'No es una falla del producto: es que aca no hay escritorio contra el cual medir.'
    exit 2
}

Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$proc = Start-Process $exe -ArgumentList "`"$board`"" -PassThru
$salida = 1   # ⚠ pesimista por defecto: si el script REVIENTA a mitad, no puede reportar exito
try {
    # Se espera a la ventana. ⚠ Si el titulo es 'Ampz MediaBoard' PELADO, eso es el caption de un
    # MessageBox y no la ventana: la app esta mostrando un error que nadie leyo.
    $limite = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $limite) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne 0 -and $proc.MainWindowTitle) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($proc.MainWindowTitle -eq 'Ampz MediaBoard') { Write-Host 'FALLA: hay un MessageBox arriba, no la ventana'; exit 1 }
    Write-Host "ventana: '$($proc.MainWindowTitle)'"

    $h = $proc.MainWindowHandle
    [void][Raton]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 1200   # que termine de abrir el clip y de acomodar el layout

    $r = New-Object Raton+RECT
    [void][Raton]::GetClientRect($h, [ref]$r)
    $o = New-Object Raton+POINT
    [void][Raton]::ClientToScreen($h, [ref]$o)
    Write-Host "cliente: $($r.R)x$($r.B)  origen en pantalla: $($o.X),$($o.Y)"

    $fallas = 0

    # ⚠ El portapapeles de Windows es un recurso EXCLUSIVO y de un solo dueno: si cualquier otra
    # app lo tiene abierto en ese instante, la llamada FALLA con CLIPBRD_E_CANT_OPEN. Es
    # transitorio y no es culpa de nadie — la propia app lo documenta y se lo traga en silencio.
    # Sin reintento, esta prueba se cae por algo que no tiene NADA que ver con lo que mide, y con
    # $ErrorActionPreference='Stop' se cae ademas en el peor momento: a mitad de la medicion.
    function ConPortapapeles([scriptblock]$accion) {
        for ($i = 0; $i -lt 12; $i++) {
            try { return & $accion } catch { Start-Sleep -Milliseconds 150 }
        }
        # ⚠ NO se dice "lo tiene otra app": eso seria adivinar, y ya paso que el mensaje
        # mandara a buscar una contencion que no existia (GetOpenClipboardWindow devolvia 0,
        # o sea que NADIE lo tenia abierto — era falta de ACCESO al escritorio).
        throw 'SIN-PORTAPAPELES'
    }

    function Probar([string]$que, [int]$cx, [int]$cy, [bool]$obligatorio) {
        ConPortapapeles { Set-Clipboard -Value 'NADA' }
        Start-Sleep -Milliseconds 150
        [Raton]::DobleClick($script:o.X + $cx, $script:o.Y + $cy)
        Start-Sleep -Milliseconds 600
        $pegado = ConPortapapeles { Get-Clipboard -Raw }
        if ($pegado) { $pegado = $pegado.Trim() }
        $ok = ($pegado -eq $script:Clip)
        if ($ok)            { Write-Host "  [OK ] $que -> copio el path" }
        elseif (-not $obligatorio) { Write-Host "  [--- ] $que -> NO copio (limitacion conocida del HWND)" }
        else                { Write-Host "  [FALLA] $que -> el portapapeles quedo en '$pegado'"; $script:fallas++ }
        return $ok
    }

    # La cabecera del sector: WPF puro, tiene que funcionar SIEMPRE. x a la izquierda para caer
    # sobre el titulo y no sobre los seis botones de la derecha.
    Write-Host ''
    Write-Host '=== 1. DOBLE CLICK EN LA CABECERA (WPF puro: obligatorio) ==='
    [void](Probar 'cabecera' 120 60 $true)

    # El area de video: HWND de VLC. Se MIDE y se informa, no hace fallar.
    # ⚠ ANTES de medir el click sobre el video hay que probar que ABAJO DEL CURSOR HAY VIDEO.
    # Sin esto la prueba es un fraude: si VLC no renderizara nada, el punto seria WPF pelado, el
    # click funcionaria igual y estariamos concluyendo algo sobre un HWND que no esta ahi.
    # testsrc esta animado -> dos muestras separadas en el tiempo TIENEN que diferir.
    Write-Host ''
    Write-Host '=== 2. DOBLE CLICK EN EL AREA DE VIDEO ==='
    $vx = $o.X + [int]($r.R / 2)
    $vy = $o.Y + [int]($r.B / 2)
    $p1 = [Raton]::Pixel($vx, $vy)
    Start-Sleep -Milliseconds 500
    $p2 = [Raton]::Pixel($vx, $vy)
    Write-Host "  pixel bajo el cursor: $p1 -> $p2"
    if ($p1 -eq $p2) {
        Write-Host '  [FALLA] el pixel NO cambio: abajo del cursor no hay video corriendo, la medicion no vale'
        $fallas++
    } else {
        Write-Host '  [OK ] hay video renderizando en ese punto (el pixel cambia)'
        [void](Probar 'area de video' ([int]($r.R / 2)) ([int]($r.B / 2)) $true)
    }
    $video = $true


    Write-Host ''
    if ($fallas -eq 0) { Write-Host '=== TODO OK ==='; $salida = 0 }
    else { Write-Host "=== $fallas FALLA(S) ===" }
}
finally {
    Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item $board -ErrorAction SilentlyContinue
}
exit $salida
