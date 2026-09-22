# Arma el instalador: republica en Release y compila installer\AmpzMediaBoard.iss.
#   -> installer\Output\AmpzMediaBoard-Setup-<version>.exe
#
# Publica SIEMPRE antes de compilar: el .iss empaqueta lo que haya en publish\, y un publish
# viejo daria un instalador con el numero nuevo y el binario anterior adentro.
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'AmpzMediaBoard.csproj'
$script = Join-Path $root 'installer\AmpzMediaBoard.iss'

# Inno Setup se instala por usuario (winget --scope user) o por maquina: se buscan los dos.
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Error 'No se encontro ISCC.exe. Instalalo con: winget install JRSoftware.InnoSetup --scope user'
}

# El exe abierto bloquea el publish (MSB3027): cerrar antes de pisarlo.
Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue

# publish NO limpia su carpeta: lo que dejo un publish anterior (una arquitectura de VLC que
# ya no se copia, un plugin podado) quedaria ahi y el .iss lo empaquetaria igual.
$publish = Join-Path $root 'bin\Release\net10.0-windows\win-x64\publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $iscc $script
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-ChildItem (Join-Path $root 'installer\Output') -Filter '*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
    ForEach-Object { 'Instalador: {0} ({1:N0} MB)' -f $_.FullName, ($_.Length / 1MB) }
