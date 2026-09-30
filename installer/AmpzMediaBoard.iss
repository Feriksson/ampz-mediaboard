; Instalador de Ampz MediaBoard (Inno Setup 6).
; No se compila a mano: lo arma tools/build-installer.ps1, que primero republica.
;
; Decisiones que NO son arbitrarias:
;   - Instalacion POR USUARIO (%LOCALAPPDATA%\Programs), nunca Program Files: la app escribe
;     ampz-crash.log JUNTO AL EXE (AppPaths.CrashLog). En Program Files esa carpeta no es
;     escribible sin admin y el log de crash fallaria justo cuando hace falta.
;   - La asociacion .mboard la escribe EL INSTALADOR, apuntando al exe instalado. La app solo se
;     autoregistra si no habia asociacion previa: si alguna vez corriste el build de Debug, la
;     asociacion seguiria apuntando a bin\Debug y el doble click abriria el binario equivocado.

; La version se LEE del exe publicado: <Version> del .csproj es la unica fuente.
; Escribirla aca seria un segundo lugar donde declarar lo mismo (drift).
#define PublishDir "..\bin\Release\net10.0-windows\win-x64\publish"
#define AppExe "AmpzMediaBoard.exe"
; Se arma con 3 componentes: el FileVersion del exe trae un cuarto (.0) que el .csproj no tiene.
#define VerMajor
#define VerMinor
#define VerRev
#define VerBuild
#expr GetVersionComponents(PublishDir + "\" + AppExe, VerMajor, VerMinor, VerRev, VerBuild)
#define AppVersion Str(VerMajor) + "." + Str(VerMinor) + "." + Str(VerRev)

[Setup]
; El AppId es la identidad del producto para Windows: NUNCA lo cambies. Cambiarlo hace que
; una version nueva se instale AL LADO de la vieja en vez de actualizarla.
AppId={{671914E4-D93F-4A14-AC30-22D520D80FC8}
AppName=Ampz MediaBoard
AppVersion={#AppVersion}
AppVerName=Ampz MediaBoard {#AppVersion}
AppPublisher=Ampz
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\AmpzMediaBoard
DefaultGroupName=Ampz MediaBoard
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/ultra64
SolidCompression=yes
SetupIconFile=..\ampz-mediaboard.ico
UninstallDisplayIcon={app}\{#AppExe}
ChangesAssociations=yes
; Multi-instancia es intencional: puede haber varios boards abiertos. El Restart Manager los
; detecta y ofrece cerrarlos antes de pisar el exe.
CloseApplications=yes
OutputDir=Output
OutputBaseFilename=AmpzMediaBoard-Setup-{#AppVersion}
WizardStyle=modern

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Al actualizar, la carpeta de VLC se reemplaza ENTERA: un plugin que la version nueva ya no
; trae (o que se podo para achicar el publish) no puede quedar huerfano de la version vieja.
Type: filesandordirs; Name: "{app}\libvlc"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Ampz MediaBoard"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\Ampz MediaBoard"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Mismo esquema que Persistence/BoardFile.cs. El %1 va ENTRE COMILLAS: sin ellas un board en
; una carpeta con espacios llega partido en varios argumentos y no abre.
Root: HKCU; Subkey: "Software\Classes\.mboard"; ValueType: string; ValueName: ""; ValueData: "AmpzMediaBoard.Board.1"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Classes\AmpzMediaBoard.Board.1"; ValueType: string; ValueName: ""; ValueData: "Board de Ampz MediaBoard"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\AmpzMediaBoard.Board.1\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"
Root: HKCU; Subkey: "Software\Classes\AmpzMediaBoard.Board.1\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

[UninstallDelete]
; Los logs los crea la app despues de instalar: el desinstalador no los conoce y dejaria la
; carpeta viva por culpa de ellos.
Type: files; Name: "{app}\*.log"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,Ampz MediaBoard}"; Flags: nowait postinstall skipifsilent
