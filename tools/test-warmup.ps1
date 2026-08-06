# Prueba de regresion del PRECALENTAMIENTO de VLC.
#
# Cubre el bug reportado de "el primer archivo que arrastro tarda un monton y los siguientes
# no". Causa: VlcEngine.Instance es perezoso, asi que el primer drop pagaba el escaneo de los
# ~323 DLL de plugins (medido: hasta 17,7 s en frio) en el hilo de UI.
#
# La prueba NO mide tiempo -- medir tiempo en una maquina con Defender de por medio da
# resultados que van de 250 ms a 17 s y no distinguen nada. Mide el HECHO: con la app recien
# abierta y SIN haber tocado un solo archivo, el runtime de VLC ya tiene que estar cargado
# en el proceso.
#
# Verificado al reves: comentando VlcEngine.Warmup() en App.OnStartup, esta prueba FALLA
# (libvlc.dll no aparece en los modulos hasta que se arrastra algo).
#
#   powershell -File tools/test-warmup.ps1
#
# Sale 0 si pasa, 1 si fallo.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $repo 'bin\Debug\net10.0-windows\AmpzMediaBoard.exe'

if (-not (Test-Path $exe)) {
    Write-Host "FALLO: no existe $exe -- compila primero con dotnet build."
    exit 1
}

Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$proc = Start-Process $exe -PassThru
try {
    # Ventana generosa: en frio el escaneo de plugins puede tardar bastante, y lo que se
    # afirma es que arranca SOLO, no que sea instantaneo.
    $deadline = (Get-Date).AddSeconds(25)
    $ok = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $proc.Refresh()
        if ($proc.HasExited) { break }
        $mods = @($proc.Modules | Where-Object { $_.ModuleName -eq 'libvlc.dll' })
        if ($mods.Count -gt 0) { $ok = $true; break }
    }

    if ($ok) {
        Write-Host 'OK: libvlc.dll quedo cargado sin que nadie arrastrara un archivo.'
        exit 0
    }

    Write-Host 'FALLO: pasaron 25 s con la app abierta y libvlc.dll NO se cargo.'
    Write-Host '       El precalentamiento no corrio (VlcEngine.Warmup en App.OnStartup).'
    exit 1
}
finally {
    if (-not $proc.HasExited) { $proc | Stop-Process -Force }
}
