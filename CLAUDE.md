# Ampz MediaBoard

Board de media para Windows: una pantalla que se **parte en sectores** (tipo tmux/OBS), y a cada
sector le arrastrás un video o una imagen. Cada sector con video tiene play/pausa y una **zona de
loop** con markers arrastrables: el clip reproduce SOLO dentro de la zona marcada.

Uso previsto: **estudio / referencia**, 2 a 8 sectores simultáneos. NO es una herramienta de VJ de
20 celdas — varias decisiones (un MediaPlayer por sector, seek preciso) se pagan en CPU y están
tomadas asumiendo esa escala.

Es hermana de `ampz desktop booster -dev` (misma máquina, mismas convenciones), pero **no comparte
código**: son dos apps independientes.

---

## Este es un producto versionado

```
version_source:     AmpzMediaBoard.csproj → <Version>
production_branch:  main
```

**Una fuente, un número.** `<Version>` en el `.csproj` es la ÚNICA verdad. De ahí el SDK deriva
`AssemblyVersion` / `FileVersion` / `InformationalVersion`, y la UI la **lee del assembly en
runtime** (`MainWindow.ShowVersion`) — nunca la escribe a mano. Si alguna vez encontrás el número
tipeado en un segundo lugar, eso es **drift**: cableálo a la fuente en vez de bumpearlo dos veces.

El bump se deriva de los conventional commits que entran a `main` (`BREAKING CHANGE`/`!` → major,
`feat:` → minor, el resto → patch) y **viaja EN la promoción**: commit `chore(release): vX.Y.Z`
sobre `develop`, ANTES del merge a `main`. Nunca directo sobre `main`.

### ⚠ CADENCIA — un release NO es un commit (decisión del usuario, 2026-07-31)

| Rama | Cuándo | Versión |
|---|---|---|
| `develop` | **cada cambio**: se commitea y se pushea | **NO se toca** |
| `main` | **SOLO cuando el usuario lo pide explícitamente** | ahí se bumpea, agregando todo lo acumulado |

Disparadores para promocionar: *"cerrá release"*, *"sacá versión"*, *"promocioná a main"* o
equivalente. **Sin pedido explícito NO se toca `main`, NO se bumpea y NO se taggea.**

Esto se escribe porque ya se hizo mal: durante la primera sesión se promocionó a `main` en CADA
cambio y salieron **cinco releases en una tarde** (v1.0.0 → v1.3.1). El error fue confundir
"commitear" con "cortar un release": el protocolo dice *cuándo va el bump* (en la promoción),
nunca dijo que había que promocionar en cada cambio.

Por qué importa, más allá del ruido:
- **La versión deja de significar algo.** Debe marcar un estado al que querrías volver, no "el
  rato en que se tocó un archivo".
- **La regla del bump está DISEÑADA para acumular**: toma todos los commits entre `main` y
  `develop` y se queda con la señal más alta (un `feat:` entre veinte `fix:` → minor). Cortando
  de a un commit, esa lógica no sirve para nada porque siempre hay uno solo para mirar.

**Push automático**: no hay que preguntar si subir.
- En cada cambio → `git push origin develop`
- Al cerrar un release → `git push origin main develop --follow-tags`

Remoto: `git@github.com:Feriksson/ampz-mediaboard.git`.

---

## Stack & build

- **.NET 10** (`net10.0-windows`), **WPF**. Sin WinForms.
- **x64 únicamente** (`<Platforms>x64</Platforms>`): `libvlc.dll` es nativa y el paquete solo trae
  `win-x64`. Si el build no es x64, `Core.Initialize()` tira `DllNotFoundException`.
- `app.manifest` declara **PerMonitorV2** (el board se arrastra entre monitores de distinto DPI).
- ⚠ Los **ImplicitUsings de WPF NO incluyen `System.IO`** (a diferencia de una consola). Por eso el
  `.csproj` declara `<Using Include="System.IO" />`. Si lo sacás, `Path`/`File`/`Directory` dejan de
  resolver en media docena de archivos de un saque.

Paquetes:
| Paquete | Por qué |
|---|---|
| `LibVLCSharp.WPF` 3.10.0 | El motor. Arrastra `LibVLCSharp`. |
| `VideoLAN.LibVLC.Windows` 3.0.23.1 | Los binarios nativos de VLC → `<output>\libvlc\win-x64\`. |
| `CommunityToolkit.Mvvm` 8.4.2 | `[ObservableProperty]` por source generator. Cero `INotifyPropertyChanged` a mano. |

Build / run:
```powershell
dotnet build AmpzMediaBoard.csproj
dotnet run --project AmpzMediaBoard.csproj
```

### Release / distribución

```powershell
dotnet publish AmpzMediaBoard.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true
# → bin\Release\net10.0-windows\win-x64\publish\
```

Autocontenido: **no necesita .NET instalado en la máquina destino**. Pesa ~411 MB (≈279 MB son los
plugins de VLC, ≈132 MB el runtime de .NET). Arranca en ~0,9 s.

⚠ Lo que el `.csproj` NO activa, y es a propósito — no lo "optimices" agregándolo:
- **`PublishTrimmed` → NO.** WPF resuelve tipos por reflexión desde XAML y LibVLCSharp usa
  callbacks nativos. El trimmer se come lo que nadie referencia estáticamente y el error aparece
  **en runtime, en la máquina del usuario**, no en el build.
- **`PublishSingleFile` → NO.** `Core.Initialize()` busca libvlc en
  `<BaseDirectory>\libvlc\win-x64\`; con single-file el directorio base es el de extracción y no
  encuentra los plugins.

Para achicar: se pueden borrar plugins de `libvlc\win-x64\plugins\` que la app no usa (streaming,
chromecast, visualizaciones, accesos de red). Baja a ~60 MB, pero es manual y **cada plugin que
sacás es un formato que la app deja de abrir para siempre** — sin aviso, sin error claro.

### Ícono

`ampz-mediaboard.ico` se genera desde `video-marketing.png` (512×512) con
`tools/make-ico.ps1`: **multi-resolución real** (16/24/32/48 como BMP para que el shell viejo
los rinda siempre, 64/128/256 como PNG). Un PNG renombrado a `.ico` NO sirve: queda borroso o
directamente en blanco según el contexto.

Va por dos vías, y hacen falta las dos: `<ApplicationIcon>` se lo pone al **.exe** (Explorer,
taskbar) y `<Resource Include>` + `Icon="ampz-mediaboard.ico"` en la Window se lo pone a la
**ventana**. ApplicationIcon solo no alcanza para la ventana.

### Ciclo build OBLIGATORIO (responsabilidad del AGENTE)

Después de CADA recompilada, el agente deja la app corriendo con el binario nuevo. No se le pide al
usuario que la cierre. El `.exe` abierto BLOQUEA el build (`MSB3027`/`MSB3021`) — ese error no es de
código, es la app abierta:

1. `powershell.exe -Command "Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue"`
2. `dotnet build "AmpzMediaBoard.csproj"`
3. `powershell.exe -Command "Start-Process 'bin\Debug\net10.0-windows\AmpzMediaBoard.exe'"`
4. `powershell.exe -Command "Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue | Select-Object Id,MainWindowTitle"`

El `Bash` tool corre por bash → toda invocación de PowerShell va envuelta en
`powershell.exe -Command "..."`. Nunca metas `$_` en ese string (bash lo expande antes que PowerShell).

---

## ⚠ Por qué LibVLCSharp y NO FFME (no lo vuelvas atrás sin datos nuevos)

Se evaluaron los tres motores reales y se **verificó contra la API de NuGet**, no de memoria:

- **`MediaElement` de WPF**: descartado. El loop se implementaría polleando `Position`, sin control
  real, y no da nada que VLC no dé mejor.
- **FFME (`FFME.Windows`)**: **descartado por abandono**. Última estable `4.4.350`, de **2021**,
  apuntando a `net5.0-windows` y clavada a **FFmpeg 4.4** (binarios que hay que conseguir y
  bundlear a mano, de esa versión exacta). La `7.0.361-beta.1` nunca salió de beta.
- **LibVLCSharp 3.10.0**: **elegido**. Su nuspec declara `targetFramework="net10.0"` explícito y lo
  mantiene VideoLAN. Trae sus propios codecs (no dependés de lo que el usuario tenga instalado).

**El airspace de WPF NO es motivo para descartar VLC.** Hay dos caminos, y la doc oficial los
documenta a ambos:
1. `VideoView` (**el que usamos hoy**): hostea un HWND y renderiza el contenido hijo en una ventana
   transparente encima.
2. **Custom rendering**: `SetVideoFormatCallbacks` + `SetVideoCallbacks` → VLC escribe los frames a
   memoria → los volcás a un `WriteableBitmap`. **Cero HWND, cero airspace.**

Se eligió (1) a pedido del usuario, para tener el prototipo andando rápido, **con la deuda técnica
asumida**: dos HWND por sector, posible parpadeo al arrastrar los splitters, y riesgo de que el
HWND se coma los eventos de drop. La migración a (2) está contenida a **un solo archivo**
(`Controls/SectorView.xaml.cs`) — ver "Capa de render" abajo.

---

## Arquitectura

### El layout es un ÁRBOL BINARIO, no una grilla NxM

Es LA decisión estructural de la app (`Layout/LayoutNode.cs`). "Partir un sector" tipo tmux es
recursivo por naturaleza: una grilla de filas y columnas NO puede representar "A ocupa toda la
mitad izquierda, y la derecha está partida en tres pedazos de distinto alto". El árbol lo hace
gratis y encima serializa a JSON sin gimnasia.

```
LayoutNode
├── SplitNode  (Orientation, Ratio 0..1, First, Second)   ← nodo interno, SIEMPRE 2 hijos
└── SectorNode (el media + su estado de loop)             ← hoja
```

- `Ratio` se guarda como **proporción, nunca en píxeles**: el board se reabre con otra resolución o
  con la ventana de otro tamaño.
- `SectorNode` es **a la vez** nodo de layout y ViewModel del media. Deliberado: partirlo en dos
  objetos espejados obligaría a sincronizarlos a mano por cero beneficio.
- Al **cerrar** un sector, su HERMANO sube a ocupar el lugar del split (`BoardViewModel.Close`), así
  el árbol nunca queda con un split de un solo hijo. Cerrar el ÚLTIMO sector no lo elimina: lo
  VACÍA — un board sin sectores no se puede volver a partir y dejaría la app sin salida.

### Un solo latido para todo el board

`BoardViewModel` tiene **UN** `DispatcherTimer` a ~33ms que itera todos los sectores, **no uno por
celda**. Ocho timers compitiendo por la misma cola del Dispatcher se pisan, y el jitter arruina
justo lo que queremos preciso: el punto de corte del loop.

### Cómo funciona la zona de loop (`SectorNode.EnforceLoop`)

Al conocerse la duración por primera vez, la zona arranca cubriendo el **clip entero**: un clip
recién soltado tiene que loopear de punta a punta sin tocar un solo marker.

**TRES** casos disparan el salto al marker A. Los tres son necesarios:
1. **el clip TERMINÓ** (`Ended`) — es el caso de la zona por defecto, donde el playhead nunca
   llega a "cruzar" B porque VLC corta el stream primero;
2. el playhead llegó (o está por llegar) a B — el loop normal, a mitad de clip;
3. el playhead quedó **antes** de A — pasa al arrastrar A hacia adelante mientras reproduce. Sin
   este caso, el video seguiría corriendo fuera de la zona marcada.

⚠ **El caso 1 faltaba y era un bug reportado** ("no loopea hasta que muevo los markers"). Causa:
`player.Time` devuelve **-1** cuando el clip terminó, y el código lo colapsaba a 0. Con la posición
en cero, el caso 2 da falso Y el caso 3 también (A suele estar en 0) → ninguna condición se cumplía
y el clip quedaba muerto al final. Se manifestaba SOLO con la zona por defecto y desaparecía al
mover B hacia adentro, porque ahí el playhead cruza B antes de que VLC llegue a `Ended`.
→ Fix: no colapsar `Time` a 0 (si terminó, la posición ES la duración) y tratar `Ended` como
disparador propio. Desde `Ended` VLC **no acepta un seek** (el input está cerrado): hay que
`Stop()` + `Play()`, y `Stop()` antes de `Play()` es obligatorio.
Verificado con `tools/test-loop.ps1`: la app decodificando 7×–20× por encima del piso de CPU
(medido con un board VACÍO como control) mucho después de la primera pasada del clip.

La zona se **sanea en cada vuelta** en vez de confiar en los campos crudos: si `LoopEnd` todavía no
se inicializó o quedó fuera de rango por un board editado a mano, el loop igual corre sobre el clip
entero en vez de no hacer nada.

Dos constantes que NO son arbitrarias:
- `LoopGuardMs = 40` — el polling es de 33ms; si esperáramos a superar B exacto nos pasaríamos de
  largo. Se dispara un poco antes.
- `ReseekCooldownMs = 150` — sin enfriamiento, una zona más corta que la latencia del seek de VLC
  entra en ráfaga de saltos y el video queda congelado tartamudeando.

⚠ **El seek de VLC NO es frame-exact.** Hay un hipito de decenas de ms en el punto del loop. Es
aceptable para referencia de estudio y es **una limitación del motor, no un bug a cazar**. Un loop
verdaderamente sin costura exige otra cosa: pre-decodificar la zona a RAM y reproducir del buffer.
Anotado como v2; NO está implementado.

`LibVLC` se crea con `--no-input-fast-seek` (seek preciso, no por keyframe) para que los markers
caigan donde los pusiste y no ~1s antes.

### ⚠ NUNCA le asignes un valor local a una DP con binding OneWay

Bug real, ya pasó: **la marca del playhead quedaba clavada** desde el primer click en el riel. El
video seguía reproduciendo, pero la marca no se movía nunca más.

`OnTrackClick` hacía `PositionMs = ms;` "para mover la marca al instante". En WPF, un binding se
guarda **como el valor local** de la propiedad. Asignarle un valor local a una DP con binding
**OneWay** REEMPLAZA la expresión de binding y la destruye para siempre — no se recupera sola.
(Con `TwoWay` no pasa: ahí la asignación se propaga a la fuente y el binding sobrevive; por eso
arrastrar los markers A/B, que sí están bindeados TwoWay, funcionaba bien.)

Regla del control: **`LoopTimeline` no es dueña del tiempo, el reproductor lo es.** El click solo
dispara `SeekRequested`; el dueño del sector hace el seek, el nodo actualiza su `PositionMs` y el
valor vuelve por el binding en la misma vuelta del Dispatcher — no hace falta ningún atajo visual.
Por eso `PositionMsProperty` **NO** lleva `BindsTwoWayByDefault` (a diferencia de
`LoopStartMs`/`LoopEndMs`, que el control SÍ modifica al arrastrar).

Test de regresión: `dotnet run` sobre `tools/LoopProbe` (referencia el proyecto real e invoca
`OnTrackClick` por reflexión). Sale 0 si el binding sobrevive, 1 si volvió el bug.

⚠ `tools/` está excluido del globbing del proyecto principal (`<Compile Remove="tools\**" />`).
Sin eso, el `Program.cs` del probe entra al build de la app y falla con "el programa tiene más de
un punto de entrada".

### Capa de render — el punto de migración

**Todo lo que toca al `VideoView` vive en `Controls/SectorView.xaml.cs` y en ningún otro lado.**
Ese aislamiento es a propósito: migrar a custom rendering (`WriteableBitmap`) se hace tocando ESE
archivo. Si el video empieza a aparecer en otros archivos, se perdió la propiedad que hace barata
la migración.

⚠ **Orden crítico**: el `MediaPlayer` se desengancha del `VideoView` ANTES de liberarse.
`SectorNode.Unload()` pone `Player = null` (lo que dispara `SyncRender`) y RECIÉN DESPUÉS llama a
`Dispose()`. Si el VideoView siguiera apuntando al player mientras se destruye, el hilo de render
de VLC escribiría sobre una ventana que ya no existe. No reordenes eso.

`BoardView.Rebuild()` llama a `SectorView.Detach()` en cada vista vieja antes de tirar el árbol
visual: cada `VideoView` hostea una ventana nativa que hay que desenganchar a mano. El
`MediaPlayer` vive en el `SectorNode` y **sobrevive** a la reconstrucción — por eso el clip sigue
reproduciendo después de partir un sector.

### ⚠ Bugs ya cazados — no los revivas

Cuatro trampas que ya costaron una ronda de debug. Las tres primeras se reportaron desde la UI; la
cuarta se anticipó antes de que se viera.

**1. `Play()` sin HWND → VLC abre SU PROPIA VENTANA.**
Si le pedís Play a libvlc sin haberle asignado una ventana de salida, **no falla**: se abre una
ventana propia y el video queda flotando FUERA de la app. Pasaba al restaurar un board guardado,
porque ahí el `Load()` corre antes de que exista el `VideoView`.
→ Fix: `SectorNode.Load()` **NO reproduce**, deja el media en `_pending`. El Play lo dispara la
VISTA vía `StartPending()`, ya con la superficie enganchada
(`SectorView.StartWhenSurfaceReady`, diferido a `DispatcherPriority.Background` para que el
VideoView ya esté posicionado por el layout).
Verificación: el proceso carga `libvlc.dll` + `libavcodec_plugin` + `libdirect3d11_plugin`, y la
única ventana extra es la de `LibVLCSharp.WPF` — **owned por la MainWindow y con su rect contenido
adentro** de ella. Si alguna vez aparece una ventana con `owner=0x0`, volvió este bug.

**2. Leer `sector.Parent` DESPUÉS de crear el `SplitNode`.**
El constructor de `SplitNode` reasigna `Parent` de sus hijos. Si en `BoardViewModel.Split` leés
`sector.Parent` después de construirlo, el "padre previo" **es el split recién creado** →
`split.First = split` → el árbol se apunta a sí mismo, la raíz nunca cambia y el board se queda
con **una sola celda para siempre**, por más clicks que hagas.
→ Fix: `var previousParent = sector.Parent;` **antes** de construir el split.

**3. `DragLeave` BURBUJEA desde los hijos → parpadeo del velo de drop.**
Al mover el cursor de un elemento interno a otro (del hint al fondo, del fondo a la cabecera) se
dispara un `DragLeave` que sube al sector, **aunque el cursor nunca salió**. Apagar el velo a
ciegas en ese evento produce el "va y viene".
→ Fix doble: `IsHitTestVisible="False"` en el velo (si participa del hit-test, al aparecer se
vuelve él mismo el elemento bajo el cursor y se auto-dispara el ciclo) **y** verificación de
geometría en `OnDragLeave` (apagar solo si el punto está fuera de los límites).

**4. Un `MediaPlayer` NO puede cambiar de ventana de salida en caliente.**
libvlc fija su vout en el `Play()`. Al reconstruir el layout (partir/cerrar un sector) los
`VideoView` se destruyen y se crean de nuevo; un reproductor que sobrevive queda dibujando en un
HWND muerto.
→ Fix: `BoardView.Rebuild()` llama a `SectorNode.Remount()` en cada sector — relanza el clip desde
donde iba usando la opción `:start-time` del media (**no** un seek post-Play, que se ignora la
mitad de las veces porque el input todavía no abrió).
⚠ `:start-time` se formatea con `CultureInfo.InvariantCulture`: en un Windows en español el
separador decimal es la coma, y `:start-time=12,4` para VLC es basura — perderías la posición sin
ningún error visible.

Las cuatro **desaparecen** al migrar a custom rendering (`WriteableBitmap`): sin HWND no hay
ventana propia de VLC, ni ventana de overlay, ni re-montaje al re-parentar, ni airspace tragándose
los eventos de drop.

### Drag & drop y el HWND

La **cabecera de cada sector es un drop target garantizado** (WPF puro). Existe a propósito como
red: el área de video es un HWND hosteado y puede tragarse los eventos de drop. Si el drop sobre el
video falla, la cabecera sigue funcionando. No la saques "para ganar espacio".

Se toma **un solo archivo** del arrastre (el primero soportado): un sector muestra un clip; soltar
una carpeta con 40 videos y que la app elija sería adivinar.

### GIF va por VLC, no por `Image`

Decisión deliberada (`Media/MediaKind.cs`): el `Image` de WPF **no anima GIFs** — muestra el primer
frame y listo. Mandándolo por VLC se anima Y queda la zona de loop funcionando gratis.

---

## Persistencia — ⚠ ARRANQUE LIMPIO, un solo destino

**La app NO guarda ningún estado propio. No escribe en `%APPDATA%`. No restaura nada al arrancar.**
Abrir el ejecutable te da SIEMPRE un board vacío; la única forma de recuperar trabajo es abrir su
archivo `.mboard`.

Esto es una decisión explícita del usuario y **no un pendiente**. Hubo estado de sesión hasta
v1.2.0 y se sacó a propósito. Antes de reintroducir cualquier cosa parecida (autoguardado, "último
board", "recuperar sesión", lista de recientes que se abra sola), leé los tres motivos:

1. **Es la misma regla que el usuario ya había fijado en su otra app.** El `CLAUDE.md` de
   `ampz desktop booster` dice, textual: *"la sesión NUNCA se rellena del INI al arrancar. Ver
   proyectos de ayer sin confirmar sería confuso."* Mismo criterio, mismo dueño.
2. **La sesión era la CAUSA de la única regla rara del diseño.** Existía porque sí un
   `_openedFromFile`, una regla de "de quién es la sesión" y un conflicto entre instancias.
   Al sacarla, las tres desaparecieron solas. Cuando remover una feature borra tres reglas
   especiales, esa feature estaba peleada con el diseño.
3. **Los archivos `.mboard` ya resuelven el problema** que la sesión venía a resolver, y lo hacen
   de forma explícita y predecible.

⚠ **Lo que SÍ hay que conservar si alguna vez se toca esto**: sin sesión, cerrar / "Nuevo" /
"Abrir" sobre un board con trabajo lo perdería en silencio. Por eso `ConfirmDiscardChanges` guarda
las tres puertas. **Sacar la sesión sin ese aviso cambiaría "restaura cosas que no pediste" por
"pierde cosas sin avisar", que es estrictamente peor.**

**Limitación conocida y aceptada**: si la app se cuelga o la matan, el trabajo no guardado se
pierde — no hay red de recuperación. Es el precio del arranque limpio y está asumido.

| Archivo | Dónde | Contenido |
|---|---|---|
| `*.mboard` | donde el usuario quiera | El único lugar donde vive un board: árbol de layout + archivo por sector + markers. |
| `ampz-crash.log` | junto al **exe** | Si el problema fuera el acceso al perfil del usuario, en `%APPDATA%` no podríamos escribir. |

### La extensión `.mboard` y el doble click (`Persistence/BoardFile.cs`)

Asociación en **`HKEY_CURRENT_USER\Software\Classes`**, nunca en HKLM: alcance de usuario, sin
pedir permisos de administrador y sin tocar la configuración de nadie más en la máquina.

```
.mboard                                  → (default) = AmpzMediaBoard.Board.1
AmpzMediaBoard.Board.1                   → (default) = Board de Ampz MediaBoard
AmpzMediaBoard.Board.1\DefaultIcon       → "<exe>",0
AmpzMediaBoard.Board.1\shell\open\command→ "<exe>" "%1"
```

⚠ **El `%1` va entre comillas.** Sin ellas, cualquier board en una carpeta con espacios (o sea,
casi todas en Windows) llega partido en varios argumentos y no abre nada.

Se registra **sola en el primer arranque**, y SOLO si no había asociación previa — así tener el
build de Debug abierto un rato no le roba la asociación al de Release. Para repuntarla al exe
actual está el botón **"Asociar .mboard"** de la barra, que aparece únicamente cuando la extensión
no está registrada.

Después de escribir el registro se llama a `SHChangeNotify(SHCNE_ASSOCCHANGED)`: sin eso el ícono
nuevo tarda en aparecer en Explorer, o no aparece hasta reiniciarlo.

El doble click llega como **argumento de línea de comandos** (`App.OnStartup` → `e.Args` →
`BoardFile.FromCommandLine`). Si hay un `.mboard` válido ahí se abre ese; si no, board vacío.

### ⚠ MULTI-INSTANCIA ES INTENCIONAL — no le pongas un mutex

La app **no** es de instancia única, y eso es un requisito del usuario, no un descuido: correr
**dos o más boards a la vez** (cada uno en su ventana, con sus clips reproduciendo) es un caso de
uso buscado. Doble click en un `.mboard` con la app abierta levanta otra instancia, y así tiene
que ser.

**NO agregues un mutex global de instancia única** (la app hermana `ampz desktop booster` sí lo
tiene, pero ahí el motivo es que dos hooks de teclado se pelearían — acá no aplica nada de eso).

Las instancias son **totalmente independientes**: sin estado de sesión compartido, no hay nada que
puedan pisarse entre ellas. (Hasta v1.2.0 sí lo había y hubo que arbitrar de quién era la sesión;
ese problema murió con la sesión.)

### Cambios sin guardar (`MainWindow.ConfirmDiscardChanges`)

Es lo que hace SEGURO no tener sesión. Guarda **tres puertas** — cerrar la ventana, "Nuevo" y
"Abrir" — y cubre **dos casos**:

| Estado del board | Qué hace |
|---|---|
| Con archivo `.mboard` | Compara contra el archivo; si difiere, avisa. |
| Sin archivo, con contenido | Avisa que nunca se guardó. |
| Sin archivo y vacío | No pregunta: no hay nada que perder. |

Diálogo Guardar / No guardar / Cancelar; Cancelar hace `e.Cancel = true` y la ventana se queda.
Por eso `Save`, `SaveAs` y `Write` devuelven `bool`: si el guardado falla o el usuario cancela el
diálogo de archivo, **no se puede dejar cerrar**.

El último caso de la tabla no es un detalle: un aviso que salta cuando no hace falta es un aviso
que el usuario aprende a ignorar, y ahí perdiste la advertencia para cuando importa de verdad.

La detección **NO usa un flag "dirty"** (`BoardStore.MatchesFile`): serializa el board y lo compara
contra el archivo. Un flag obligaría a observar cada mutación posible —cargar un clip, mover un
marker, partir un sector, arrastrar un splitter— y alcanza con que se escape UNA para que el aviso
mienta. Comparar el resultado no se puede equivocar.

⚠ El contenido del archivo se **normaliza** antes de comparar (deserializar a DTO y re-serializar
con las mismas opciones). Comparar texto crudo sería sensible al FORMATO: un `.mboard` escrito
compacto se leería como "modificado" sin que nadie lo tocó.

`BoardStore` serializa con **DTOs propios**, no con el árbol de dominio: `SectorNode` arrastra un
MediaPlayer nativo, un BitmapImage y un puntero al padre (un ciclo) — nada de eso puede ni debe
serializarse.

- El board se **autoguarda al cerrar la ventana**. El botón "Guardar board" (y Ctrl+S) queda para
  asegurar sin cerrar.
### ⚠ Archivo ausente: la referencia NO se descarta (esto evita pérdida de datos)

Si el archivo de un sector no existe al abrir (movido, borrado, disco externo desconectado), el
sector **conserva el path y los markers** y se marca como ausente (`SectorNode.MissingPath` /
`IsMissing`). Se muestra un estado ámbar con el nombre, la carpeta donde vivía, y un botón
"Buscar el archivo…".

**Por qué importa y no es cosmético**: el board se **autoguarda al cerrar**. Si el sector quedara
vacío, bastaría abrir la app UNA vez con el archivo ausente para borrar para siempre la referencia
Y la zona de loop que te costó ajustar. El board se degradaría solo, en silencio. Con esto, la
referencia sobrevive ciclos indefinidos de abrir/cerrar.

Re-vincular (`SectorNode.Relink`) **conserva los markers**; soltar un archivo sobre un sector que
NO está ausente es reemplazar y sí los resetea (son de otro clip: mantenerlos marcaría una zona
que no tiene nada que ver).

Verificado end-to-end con `tools/test-missing.ps1`: board con un path fantasma + markers
1500/4200 → abrir → cerrar limpio (WM_CLOSE, que dispara el autoguardado) → path, ambos markers y
la estructura del split siguen en el JSON.
- **`Load()`/`Save()` JAMÁS voltean la app**: try/catch → degradar a board vacío o a memoria.

---

## Atajos

| Tecla | Acción |
|---|---|
| `Espacio` | Play / pausa del sector seleccionado |
| `A` | Fijar el marker de INICIO del loop en el playhead |
| `B` | Fijar el marker de FIN del loop en el playhead |
| `L` | Prender/apagar la zona de loop |
| `Ctrl+N` | Board nuevo (un solo sector vacío) |
| `Ctrl+O` | Abrir un `.mboard` |
| `Ctrl+S` | Guardar en el archivo actual (si no hay, pregunta dónde) |
| `Ctrl+Shift+S` | Guardar como… |

**Partir sectores es SOLO por los botones de la cabecera de cada sector.** La barra superior tenía
botones de partir y se sacaron: actuaban sobre "el sector seleccionado", lo que obligaba a mirar
cuál estaba seleccionado antes de apretar. El botón que vive EN el sector no tiene esa ambigüedad
— partís el que estás mirando.

`A`/`B` fijan los markers **donde está el playhead** porque ese es el flujo real de marcar una
zona: mirás el clip y en el momento exacto apretás la tecla. Buscar el frame arrastrando el marker
a ojo es más lento y menos preciso.

Se enganchan en `PreviewKeyDown` y no en `KeyDown`: si el foco quedó en un botón, el `KeyDown` de
Espacio lo consume el botón (lo lee como "apretame") y el atajo nunca llega.

---

## Estructura de carpetas

| Carpeta | Qué vive ahí |
|---|---|
| `Layout/` | El árbol: `LayoutNode`, `SplitNode`, `SectorNode` (nodo + estado del media + loop). |
| `Board/` | `BoardViewModel` (árbol + latido + split/close) y `BoardView` (materializa el árbol a controles). |
| `Media/` | `VlcEngine` (la instancia única de LibVLC) y `MediaKind` (qué extensión es qué). |
| `Controls/` | `SectorView` (**la capa de render**) y `LoopTimeline` (markers + playhead). |
| `Persistence/` | `AppPaths` y `BoardStore`. |
| `tools/` | Scripts de mantenimiento: `make-ico.ps1` (regenerar el ícono desde el PNG) y `test-missing.ps1` (prueba end-to-end del archivo ausente). |
| raíz | `App`, `MainWindow`, `video-marketing.png` (fuente del ícono), `ampz-mediaboard.ico`. |

---

## Convenciones del repo

- **Comentarios en español**, densos y orientados al *por qué*, no al *qué*. Mantené el estilo:
  cuando agregues lógica no trivial, explicá la razón.
- `Load()`/`Save()` con try/catch silencioso. Un JSON corrupto o un disco lleno **nunca** tumban la app.
- Toda data de usuario a `AppPaths.DataDir`, jamás junto al exe.
- La capa de render no se desparrama: el video se toca en `SectorView` y en ningún otro lado.
