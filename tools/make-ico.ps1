# Genera un .ico multi-resolucion REAL desde un PNG.
#   - tamanos <= 48  -> entradas BMP (BITMAPINFOHEADER + XOR BGRA + mascara AND)
#     Motivo: algunos contextos viejos del shell no rinden entradas PNG chicas.
#   - tamanos >= 64  -> entradas PNG (formato Vista+), mucho mas livianas.
Add-Type -AssemblyName System.Drawing

$srcPath = 'C:\Users\ampz\Desktop\Repos personales\ampz-mediaboard -dev\video-marketing.png'
$outPath = 'C:\Users\ampz\Desktop\Repos personales\ampz-mediaboard -dev\ampz-mediaboard.ico'

$src = [System.Drawing.Bitmap]::FromFile($srcPath)
$sizes = @(16, 24, 32, 48, 64, 128, 256)

function Get-Scaled([System.Drawing.Bitmap]$image, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($image, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
    $g.Dispose()
    return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

function Get-BmpBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    # BITMAPINFOHEADER. biHeight va DOBLE porque el bitmap del icono guarda
    # la imagen XOR y la mascara AND apiladas.
    $bw.Write([uint32]40)
    $bw.Write([int32]$w)
    $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)
    $bw.Write([uint32]($w * $h * 4))
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)

    # XOR: filas BGRA de ABAJO HACIA ARRIBA (los DIB son bottom-up).
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $buffer = New-Object 'byte[]' ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buffer, 0, $buffer.Length)
    $bmp.UnlockBits($data)

    for ($y = $h - 1; $y -ge 0; $y--) {
        $bw.Write($buffer, $y * $stride, $w * 4)
    }

    # Mascara AND: 1bpp, filas alineadas a 4 bytes. Todo en cero = opaco;
    # la transparencia real la aporta el canal alfa del XOR de 32 bits.
    $maskRow = [math]::Floor(($w + 31) / 32) * 4
    $zeros = New-Object 'byte[]' ($maskRow * $h)
    $bw.Write($zeros, 0, $zeros.Length)

    $bw.Flush()
    return $ms.ToArray()
}

$entries = @()
foreach ($size in $sizes) {
    $scaled = Get-Scaled $src $size
    $bytes = if ($size -ge 64) { Get-PngBytes $scaled } else { Get-BmpBytes $scaled }
    $entries += [pscustomobject]@{ Size = $size; Bytes = $bytes }
    $scaled.Dispose()
}
$src.Dispose()

$fs = [System.IO.File]::Create($outPath)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR
$bw.Write([uint16]0)                 # reservado
$bw.Write([uint16]1)                 # tipo 1 = icono
$bw.Write([uint16]$entries.Count)

# El primer byte de datos arranca despues del directorio completo.
$offset = 6 + (16 * $entries.Count)
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }   # 256 se codifica como 0
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)               # colores de la paleta (0 = sin paleta)
    $bw.Write([byte]0)               # reservado
    $bw.Write([uint16]1)             # planos
    $bw.Write([uint16]32)            # bits por pixel
    $bw.Write([uint32]$e.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $bw.Write($e.Bytes, 0, $e.Bytes.Length) }

$bw.Flush(); $fs.Close()

Write-Output ('ICO generado: ' + $outPath)
Write-Output ('Entradas: ' + (($entries | ForEach-Object { $_.Size }) -join ', '))
Write-Output ('Peso total: ' + [math]::Round((Get-Item $outPath).Length / 1KB, 1) + ' KB')

# Verificacion: que Windows lo pueda cargar de verdad como icono.
$test = New-Object System.Drawing.Icon($outPath)
Write-Output ('Windows lo carga OK -> ' + $test.Width + 'x' + $test.Height)
$test.Dispose()
