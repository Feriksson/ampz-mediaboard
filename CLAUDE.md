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

Flujo de trabajo: se desarrolla en `develop`, se promociona a `main`.

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

## Persistencia

Todo en **`%APPDATA%\AmpzMediaBoard\`** (`Persistence/AppPaths.cs`). Nunca junto al exe.

| Archivo | Contenido |
|---|---|
| `board.json` | El árbol de layout + el path del archivo de cada sector + los markers de loop y el flag `LoopEnabled`. |
| `ampz-crash.log` | Junto al **exe** (si %APPDATA% es lo que falla, ahí no escribiríamos). |

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
| `Ctrl+S` | Guardar el board |

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
