# Prueba de regresion del REINICIO DEL LOOP al terminar el clip.
#
# Cubre el bug de "movi el video de celda y al terminar vuelve a arrancar en cualquier lado,
# sin respetar el marker A". Se manifestaba SOLO con la zona de loop por defecto (marker A en 0),
# que es justo el caso normal al arrastrar un clip a otra celda.
#
# Necesita un clip corto. Si hay ffmpeg lo genera solo; si no, pasale uno por parametro.
#
#   powershell -File tools/test-restart.ps1
#   powershell -File tools/test-restart.ps1 -Clip 'C:\ruta\a\un\video.mp4'
#
# Sale 0 si pasa, 1 si fallo.

param([string]$Clip = '')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not $Clip) {
    $Clip = Join-Path $env:TEMP 'ampz-loop-clip.mp4'
    if (-not (Test-Path $Clip)) {
        if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
            Write-Host 'No hay ffmpeg para generar el clip. Pasa uno con -Clip <ruta>.'
            exit 1
        }
        # 4 segundos alcanzan: la prueba necesita que el clip TERMINE, no que sea largo.
        ffmpeg -v error -y -f lavfi -i 'testsrc=size=320x240:rate=25:duration=4' -pix_fmt yuv420p $Clip
        Write-Host "clip generado: $Clip"
    }
}

& dotnet run --project (Join-Path $repo 'tools\RestartProbe') -- $Clip
exit $LASTEXITCODE
