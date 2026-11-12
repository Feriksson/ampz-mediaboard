# Ampz MediaBoard

Board de media para Windows: la pantalla se **parte en sectores** (tipo tmux/OBS) y a cada uno se
le arrastra un video o imagen. Cada video tiene play/pausa y una **zona de loop** con markers A/B.
Uso previsto: estudio/referencia, **2 a 8 sectores** (no VJ de 20 celdas: un MediaPlayer por sector
y seek preciso se pagan en CPU). App hermana de `ampz desktop booster`, **sin código compartido**.

Regla de lectura: lo que aparece acá como ⚠ ya costó un bug. No lo "simplifiques" sin leer el porqué.

---

## Producto versionado

```
version_source:     AmpzMediaBoard.csproj → <Version>
production_branch:  main
```

- `<Version>` es la ÚNICA verdad; la UI la lee del assembly (`MainWindow.ShowVersion`). Un número
  tipeado en otro lado es drift: cablealo a la fuente.
- Bump por conventional commits acumulados (`!`/`BREAKING CHANGE` → major, `feat:` → minor, resto →
  patch), en commit `chore(release): vX.Y.Z` sobre `develop` ANTES del merge a `main`.
- ⚠ **Cadencia**: cada cambio → commit + `git push origin develop`, **sin tocar versión**. `main`,
  bump y tag **SOLO** con pedido explícito ("cerrá release", "sacá versión", "promocioná a main").
  Al cerrar: `git push origin main develop --follow-tags`. (Ya se cortaron cinco releases en una
  tarde por confundir commitear con releasear.)
- Push automático, no preguntar. Remoto: `git@github.com:Feriksson/ampz-mediaboard.git`.

---

## Stack & build

- **.NET 10** `net10.0-windows`, WPF. **x64 únicamente** (libvlc nativa solo `win-x64`; otro target →
  `DllNotFoundException` en `Core.Initialize()`). `app.manifest` PerMonitorV2.
- ⚠ Los ImplicitUsings de WPF no traen `System.IO`: el `.csproj` declara `<Using Include="System.IO" />`.
- ⚠ `tools/` está fuera del globbing (`<Compile Remove="tools\**" />`); sin eso los `Program.cs` de
  las sondas rompen el build ("más de un punto de entrada").
- Paquetes: `LibVLCSharp.WPF` 3.10.0, `VideoLAN.LibVLC.Windows` 3.0.23.1, `CommunityToolkit.Mvvm` 8.4.2.

### Ciclo build OBLIGATORIO (lo hace el agente)

Tras cada recompilada, dejar la app corriendo con el binario nuevo. El `.exe` abierto bloquea el
build (`MSB3027`/`MSB3021` = app abierta, no error de código):

1. `powershell.exe -Command "Stop-Process -Name AmpzMediaBoard -Force -ErrorAction SilentlyContinue"`
2. `dotnet build "AmpzMediaBoard.csproj"`
3. `powershell.exe -Command "Start-Process 'bin\Debug\net10.0-windows\AmpzMediaBoard.exe'"`
4. `powershell.exe -Command "Get-Process -Name AmpzMediaBoard -ErrorAction SilentlyContinue | Select-Object Id,MainWindowTitle"`

Nunca metas `$_` en esos strings (bash lo expande).

### Release / publish

```powershell
dotnet publish AmpzMediaBoard.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true
```

- Autocontenido (~411 MB, +134 MB de ffmpeg). Correr **`tools/fetch-ffmpeg.ps1` antes**: el
  `.csproj` copia `third_party/ffmpeg/` solo si existe; si no, el ⇄ sale deshabilitado.
- ⚠ **NO** `PublishTrimmed` (XAML y callbacks nativos por reflexión → fallas en runtime en la
  máquina del usuario). **NO** `PublishSingleFile` (`Core.Initialize()` no encuentra
  `libvlc\win-x64\`).
- Podar plugins de VLC achica, pero cada plugin borrado es un formato que deja de abrir en silencio.
- ⚠ **Si existe `publish/` y la asociación `.mboard` apunta ahí, REPUBLICÁ tras cada cambio**: el
  doble click abre el Release, no Debug. Debug muestra **`dev`** junto a la versión (`#if DEBUG` en
  `ShowVersion`): no saques esa marca.

### Ícono

`ampz-mediaboard.ico` se genera desde `video-marketing.png` con `tools/make-ico.ps1` (multi-res
real; un PNG renombrado no sirve). Hacen falta `<ApplicationIcon>` (exe) **y** `<Resource>` +
`Icon=` en la Window (ventana).

---

## Por qué LibVLCSharp (no lo revoques sin datos nuevos)

- `MediaElement`: loop por polling, sin control. **FFME**: abandonado (última estable 2021, net5,
  clavado a FFmpeg 4.4). **LibVLCSharp 3.10**: target net10 explícito, mantenido, codecs propios.
- Render actual: `VideoView` (HWND hosteado) por velocidad de prototipo, **deuda asumida**
  (airspace, parpadeo, eventos tragados). Migración prevista a custom rendering
  (`SetVideoCallbacks` → `WriteableBitmap`), contenida a `Controls/SectorView.xaml.cs`. Esa
  migración elimina los seis bugs de HWND, el congelado de splitter y los re-montajes.

---

## Arquitectura

### Layout = árbol binario (`Layout/LayoutNode.cs`)

`SplitNode` (Orientation, Ratio 0..1, First, Second — siempre 2 hijos) y `SectorNode` (hoja: media +
loop; es a la vez nodo y ViewModel, a propósito). `Ratio` es proporción, nunca píxeles. Cerrar un
sector sube a su hermano; cerrar el ÚLTIMO lo vacía (un board sin sectores no tiene salida).

**Distribuir** (`BoardViewModel.Distribute`, `Ctrl+E`): `ratio = hojas(First)/hojas(total)`, no 0.5
(0.5 iguala hermanos, no sectores). ⚠ Dispara `RatiosChanged` (→ `BoardView.ApplyRatios` reescribe
las `GridLength`), **no** `LayoutChanged`: este reconstruye todo y re-monta cada clip.

### ⚠ Arrastrar un divisor CONGELA los sectores

Cada video es una ventana nativa; re-layout por píxel con VLC decodificando = slideshow. Dos mitades:
`SectorNode.Freeze/Thaw` (pausa, `Tick()` los saltea) + `SectorView.SetFrozen` (VideoView
**`Collapsed`**, no `Hidden`: Hidden sigue en el layout). Orquesta `BoardView` desde el
`DragStarted`/`DragCompleted` del splitter.
- Descongelar cuelga de `DragCompleted`, que también dispara con `Esc`.
- Solo se reanuda lo que estaba reproduciendo (`_resumeAfterThaw`).
- En `SyncRender` el congelado va en la MISMA expresión de `Video.Visibility` (evita carrera).
- Velo "Redimensionando…" en vez de negro.

### Un solo latido

UN `DispatcherTimer` ~33 ms por board activo itera todos los sectores. Ocho timers = jitter en el
corte del loop. Boards en segundo plano: timer **detenido**.

### ⚠ VLC se precalienta (`VlcEngine.Warmup`)

`new LibVLC()` escanea ~323 DLL de plugins (sin `plugins.dat`): 250 ms caliente, hasta **17 s en
frío**. `App.OnStartup` lo dispara tras `Show()` en un hilo **propio** `BelowNormal` (no del pool).
`_shuttingDown` va dentro del lock de `Shutdown()`. Descartado precargar DLL de plugins a mano (rutas
hardcodeadas que se rompen al podar). Regresión: `tools/test-warmup.ps1` (mide el hecho, no tiempo).

### Zona de loop (`SectorNode.EnforceLoop`)

Al conocerse la duración, la zona cubre el clip entero. TRES disparadores de salto a A:
1. el clip terminó (`Ended`) — ⚠ `player.Time` da **-1** al terminar; no colapsarlo a 0. Desde
   `Ended` no hay seek: `Stop()` + `Play()`;
2. el playhead llega a B;
3. el playhead quedó antes de A (arrastrar A hacia adelante).

La zona se sanea en cada vuelta. Constantes no arbitrarias: `LoopGuardMs = 40` (polling de 33 ms),
`ReseekCooldownMs = 150` (evita ráfagas), `RelaunchCooldownMs = 500`, `TailGuardMs = 150`,
`MaxExtrapolationMs = 600`. `LibVLC` con `--no-input-fast-seek`. El seek de VLC **no es
frame-exact**: el hipito de decenas de ms es límite del motor, no bug (loop sin costura = v2,
pre-decodificar a RAM).

**⚠ Intervalo negro entre repeticiones** (no es config de VLC): `SeekTo` (input abierto, hipito) vs
`RestartFrom` (reabre el archivo: cientos de ms en negro). Con B = final, siempre caía en
`RestartFrom`. Arreglo en dos mitades:
- `TailGuardMs`: la zona efectiva cierra 150 ms antes del final real (es un `Min`).
- **`player.Time` avanza a SALTOS** (~324 ms): `Tick()` extrapola con reloj de pared
  (`_lastRealTimeMs`), solo mientras reproduce, con techo `MaxExtrapolationMs`, y `Reanchor()` tras
  `SeekTo`/`RestartFrom`.
- `RestartFrom` queda como red. Regresión: `tools/test-restart.ps1` caso 3 — discrimina por el
  ESTADO de VLC (`Ended`/`Stopped`) leído antes de `Tick()`, no por la posición.

### Loop ida y vuelta / ping-pong (⇄ o `P`)

libvlc no reproduce al revés → **ffmpeg pre-genera UN archivo** (ida + vuelta + colchón) y el sector
loopea ese archivo. Sin audio, zona ≤ 30 s, salida ≤ 720p.

| Pieza | Dónde |
|---|---|
| Reglas puras (zona, tope, mapeo, clave, args) | `Media/PingPongMath.cs` |
| Proceso ffmpeg + caché (uno a la vez, cancelable matando, `.partial` + rename) | `Media/PingPongRenderer.cs` |
| Orquestación (debounce 600 ms, rechazo, cambio de fuente) | `SectorNode`, región "Ping-pong" |
| UI (⇄ + estado junto al reloj) | `SectorView.SyncPingPong` |

```
ffmpeg -ss A -t d -i <src> -an -sn -dn -filter_complex
  "[0:v]scale=-2:'min(720,ih)',format=yuvj420p,split=3[f][r][p];[r]reverse[rv];
   [p]trim=duration=PAD,setpts=PTS-STARTPTS[pp];[f][rv][pp]concat=n=3:v=1:a=0[out]"
  -map [out] -c:v mjpeg -q:v 3 <out>.mkv
```

- ffmpeg: build **LGPL v3 estático fijado** (BtbN `n8.1.3-9-g29e619e767`, SHA256) vía
  `tools/fetch-ffmpeg.ps1` → `third_party/ffmpeg/` (gitignored, con LICENSE y NOTICE). **Nunca se
  commitea ni se usa el del PATH.** Sin él la feature se deshabilita sola.
- MJPEG intra-only (LGPL no trae x264; todo cuadro es keyframe → seek exacto). `-ss/-t` de
  **entrada** (seek exacto; `-t`, no `-to`). Escalar **antes** del `split` (`reverse` guarda la zona
  en RAM: ~1,7 GB para 30 s → una generación a la vez y tope de 30 s).
- Forma, d = B − A: `[0,d)` ida, `[d,2d)` vuelta, `[2d,2d+pad)` colchón (pad = min(400 ms, d)) para
  que `TailGuardMs` no muerda la vuelta. El loop vuelve a 0 en 2d.
- ⚠ **`MediaPath` es SIEMPRE el original**; VLC abre `_playbackPath`. Dos relojes: `_playMs` (archivo
  que suena) y `PositionMs` (original); se traducen solo en `SetPlayPosition` con la zona **con que se
  generó** (`_ppStartMs/_ppEndMs`). `DurationMs` es siempre la del original.
- Click en timeline → punto sobre la ida, sin salir del modo. A/B fijan sobre el playhead original y
  regeneran. Ping-pong es modo del loop (prenderlo prende LOOP; apagar LOOP lo apaga).
- Zona > 30 s se **rechaza** (no se recorta), validado al prender y tras el debounce.
- Sin ffmpeg / error de ffmpeg **no apagan** `PingPong` (regla del archivo ausente: no inventar
  cambios sin guardar). Errores a `ampz-crash.log` y DiagLog.
- Cambio original ↔ generado = re-montaje seguro (`SectorNode.SwitchPlayback`: player y Media nuevos,
  `Play()` desde la vista tras `WhenReleased()`). Mientras genera, sigue sonando lo anterior y el
  estado va junto al reloj (no sobre el video: airspace).
- Audio: el generado es mudo; volumen/mute del usuario no se tocan, solo se atenúan.
- Caché descartable: `%TEMP%\AmpzMediaBoard\pingpong\<pid>\<hash>.mkv` (carpeta por proceso por la
  multi-instancia; se barren las de procesos muertos al arrancar y la propia al cerrar, después de
  `VlcEngine.Shutdown`). Clave SHA256 de (ruta en mayúsculas, mtime, A y B al ms, versión de formato).
  El `.mboard` guarda `"PingPong": true` solo si está prendido.
- Regresión: `BoardProbe` 18–21 (puro), `tools/PingPongProbe` (ffmpeg real), `tools/test-pingpong.ps1`
  (app real; detecta con `WindowFromPoint` si otra ventana tapa el board y deja ese paso sin medir).

### ⚠ Nunca asignes valor local a una DP con binding OneWay

Destruye el binding para siempre (bug: playhead clavado tras el primer click). `LoopTimeline` no es
dueña del tiempo: el click solo dispara `SeekRequested`. `PositionMsProperty` **sin**
`BindsTwoWayByDefault` (`LoopStartMs`/`LoopEndMs` sí). Regresión: `tools/LoopProbe`.

### Capa de render

**Todo lo que toca `VideoView` vive en `Controls/SectorView.xaml.cs` y en ningún otro lado.**
⚠ El player se desengancha (`Player = null` → `SyncRender`) ANTES de liberarse. `BoardView.Rebuild()`
llama `SectorView.Detach()` en cada vista vieja; el `MediaPlayer` vive en el `SectorNode` y sobrevive.

### ⚠ Cierre rápido

`MediaPlayer.Stop()` bloquea 100–500 ms por player. Se resuelve con:
- `VlcEngine.Release(player)`: Stop + Dispose en paralelo, hilos `LongRunning` (no pool).
- `MainWindow.BeginFastClose`: la ventana se **esconde** ya y se **cierra** recién cuando terminaron
  los Stop (cerrar antes destruye HWND con vouts vivos). Postergado una vuelta del Dispatcher (no se
  puede esconder desde `Closing`); termina en `finally`.
- `VlcEngine.WhenReleased()` con techo `ReleaseTimeout` (3 s). `Shutdown` no libera `LibVLC` si vence.
- ⚠ `Remount` **no** re-monta un clip pendiente (`_pending != null`): cargaba cada archivo dos veces
  al abrir. Regresión: `RestartProbe` caso 4; cierre: `tools/test-close.ps1` (8 sectores, umbral 400 ms).

### Pestañas (`Board/BoardTab.cs`)

`BoardTab` = `BoardViewModel` + `BoardView` + nombre. La vista se crea UNA vez.
- ⚠ **Se cambia por `Visibility`** (inactiva `Collapsed`), todas apiladas en `BoardHost`. **No es un
  `TabControl`** (descarga el contenido = destruye los `VideoView`). Cambio medido en 4–12 ms, 0 reaperturas.
- Pestaña de fondo **suspendida**: `Freeze` (mira también `IsPlaying` propio: un clip en Opening da
  `player.IsPlaying == false`), latido detenido, Play espera superficie visible
  (`StartWhenSurfaceReady` en `IsVisibleChanged`), y `Thaw` → `Reanchor` (si no, el TickCount viejo
  inventa 600 ms y dispara loops). Un `DragCompleted` con board suspendido no descongela.
- Abrir un archivo de varias pestañas: solo la activa decodifica; las demás quedan pendientes.
- Límites conocidos: primera vista de una pestaña de fondo 0,7–3,5 s; pestaña pausada ~90–230 ms/s de
  CPU; F11 escondiendo la tira no está verificado.

**Un archivo = TODAS las pestañas** (decisión del usuario). Ruta, foto guardada y "¿cambios?" viven en
`MainWindow` (región "Documento"). Agregar (`Ctrl+T`, nombre "Board N" con menor N libre vía
`BoardTab.NextDefaultName`), quitar (`Ctrl+W`, sin preguntar), reordenar y renombrar son cambios sin
guardar. **La pestaña activa se persiste (`ActiveTab`) pero NO es cambio sin guardar.**
- Renombre: click afuera confirma vía `OnWindowPreviewMouseDown` y `Deactivated` (`LostKeyboardFocus`
  no alcanza). Mientras hay `TextBox` enfocado, los atajos se saltean.

**Reordenar** (`Board/TabOrder.cs`, puro; única operación `MainWindow.MoveTab`, que mueve la colección
`_tabs` y nada más — ninguna `BoardView` se re-parenta). Captura de mouse, no `DoDragDrop`.
- ⚠ Captura la **lista** (`TabList`), no la pestaña (salir del árbol visual pierde la captura).
- Reordena en vivo al cruzar la mitad de la vecina; orden decidido con posición sin recortar.
- ⚠ `Active = true` ANTES de `CaptureMouse` (MouseMove sintético reentrante).
- `Esc` o perder la captura cancela; solo cuenta el `LostMouseCapture` de la lista.
- Regresión: `tools/test-tab-reorder.ps1` (necesita escritorio quieto; sale 2 si no). El re-parentado
  solo se detecta por `BoardView.OnVisualParentChanged` → `viewreparent` en DiagLog.

**`DiagLog`** (`DiagLog.cs`): apagado salvo `AMPZ_DIAG_LOG=<archivo>`. Líneas: `switch`, `open`,
`audio`, `audiostate` (readback sin escribir, ~2 s), `move`, `tabdrag`, `viewreparent`, `freeze`,
`play`, `pppos`. Regresión pestañas: `tools/test-tabs.ps1` (el "•" se compara como `[char]0x2022`:
`.ps1` sin BOM).

### Panel fijado (`Board/PinnedDock.cs`)

📌 manda un sector al panel a la **izquierda**; en el panel, 📌 lo desfija.
- ⚠ **`DockHost` es HERMANO de `BoardHost`, nunca hijo**: un cambio de pestaña jamás pausa, congela ni
  reconstruye el panel.
- El panel es un `BoardViewModel` con `IsDock = true` (latido propio, mismos `SectorView`). Siempre
  pila vertical sin partir, alturas iguales. Sin contenido: columna y divisor en ancho 0.
- Fijar/desfijar mueven `MediaSnapshot` (un re-montaje). Desfijar vuelve a la pestaña activa: llena el
  sector seleccionado (o el último recordado) si está vacío, si no lo parte. El intercambio por
  arrastre funciona entre panel y pestaña.
- Divisor pestañas ↔ panel congela ambos lados (`Begin/EndExternalResize`).
- Una sola selección en la app (`OnBoardSelectionChanged`); ⚠ `ClearSelection` recuerda el último.
- Solo = pestaña activa + panel. El panel ignora el volumen general (suena a master 100).
- Persistencia: `"Dock": { "Width", "Sectors": [...] }`, ancho como proporción 0.1–0.8 (4 decimales).
  ⚠ Panel vacío se escribe **AUSENTE** (si no, todo v2 viejo se leería modificado).
- Límites: fijar/desfijar re-monta los otros clips del board que cambia de forma; vaciar con ⏏ el
  último lo esconde en ≤ 0,5 s.
- Regresión: `BoardProbe` 12–17, `tools/test-dock.ps1`.

### ⚠ Bugs ya cazados — no los revivas

1. **`Play()` sin HWND → VLC abre su propia ventana.** `SectorNode.Load()` no reproduce: deja `_pending`;
   la vista dispara `StartPending()` con la superficie lista (`StartWhenSurfaceReady`, prioridad
   Background). Síntoma: ventana con `owner=0x0`.
2. **Leer `sector.Parent` después de crear el `SplitNode`** (el constructor lo reasigna → árbol
   apuntándose a sí mismo). Guardar `previousParent` ANTES.
3. **`DragLeave` burbujea desde hijos** → velo de drop parpadea. Velo con `IsHitTestVisible="False"` +
   chequeo de geometría en `OnDragLeave`.
4. **Un `MediaPlayer` no cambia de ventana de salida en caliente.** `Rebuild()` → `Remount()` con
   `:start-time` en el Media (no seek post-Play), formateado con `InvariantCulture` (coma decimal = basura).
5. **Las opciones del `Media` sobreviven a `Stop()`+`Play()`** (el `:start-time` viejo reaparecía en el
   loop). `RestartFrom(ms)` construye un `Media` NUEVO. Regresión `tools/test-restart.ps1`: medir con
   ventana fija ~900 ms y comparar por cercanía, no en el instante del reinicio.
6. **HWND ocupado también saca el video afuera** (`drawable: HWND … is busy`: el Stop async del player
   viejo no soltó el vout). `StartWhenSurfaceReady` espera `VlcEngine.WhenReleased()`; `TogglePlay` no
   adelanta pendientes con liberaciones en curso. No volver al Stop sincrónico.
   Regresión: `tools/test-replace.ps1`.

### Mover media entre sectores = INTERCAMBIAR (`BoardViewModel.SwapMedia`)

Soltar sobre ocupado reacomoda, no destruye. Viaja `SectorNode.MediaSnapshot` (path, posición,
markers, volumen, mute, ping-pong), **nunca el `MediaPlayer`** (atado a su HWND). ⚠ Tomar las DOS fotos
antes de restaurar ninguna. Asa = cabecera (con umbral de arrastre, botones excluidos). ⚠ El origen
viaja en un campo estático (`SectorView._dragSource`), no en el `DataObject` (COM); se limpia en `finally`.

### Audio por sector

- Volumen 0..100 y mute por sector, persistidos y viajan con el clip. Mute ≠ volumen 0 (`MediaPlayer.Mute`).
- ⚠ VLC no acepta volumen hasta que existe la salida: `ApplyAudio` se llama al cambiar la propiedad, en
  `StartPending` y en el tick donde aparece la duración.
- **Solo** (Shift+click en mute, `BoardViewModel.Solo`): solo toca mutes, saltea sin audio
  (`HasAudio`). ⚠ Interceptado en `PreviewMouseLeftButtonDown` con `Handled` (si no, el ToggleButton
  pelea).
- **Volumen general**: ESCALA, nunca pisa: `round(sector × master / 100)` (`EffectiveVolume`, pura).
  Por board, **empujado** a cada sector (`BoardMasterVolume`) en cada punto de entrada (`Split`,
  `ReplaceRoot`). Persistido por pestaña **solo si ≠ 100**. No viaja en el snapshot.
- ⚠ **`--aout=directsound`** (`VlcEngine.Create`): con WASAPI el volumen/mute es de la sesión del
  PROCESO y gana el último. Regresión: `tools/test-audio.ps1` con `audiostate` (la línea `audio` lee
  justo tras escribir y no puede ponerse roja).

### Drag & drop y formatos

- La **cabecera** es drop target garantizado (el HWND puede tragarse el drop). No la saques.
- Se toma un solo archivo (el primero soportado).
- GIF va por VLC (`Media/MediaKind.cs`): el `Image` de WPF no anima.

---

## Persistencia — ⚠ ARRANQUE LIMPIO

**Sin estado propio**: nada en `%APPDATA%`, nada se restaura; arrancar = board vacío. Decisión del
usuario (misma regla que su otra app; la sesión hasta v1.2.0 generaba tres reglas especiales). No
reintroducir autoguardado, "último board", recientes ni recuperación.

⚠ **No hay autoguardado, tampoco al cerrar.** Solo escribe Guardar / `Ctrl+S` / `Ctrl+Shift+S` / el
"Guardar" del aviso. Una prueba que "cierra y compara el `.mboard`" no prueba nada: guardar
explícitamente o usar `BoardStore` desde `tools/BoardProbe`.

| Archivo | Dónde | Contenido |
|---|---|---|
| `*.mboard` | donde quiera el usuario | Todas las pestañas, panel fijado y pestaña activa. |
| `ampz-crash.log` | junto al exe | Por si el problema es el perfil del usuario. |

### Cambios sin guardar (`MainWindow.ConfirmDiscardChanges`)

Es lo que hace seguro no tener sesión. Guarda cerrar / Nuevo / Abrir con UN aviso por el archivo
(Guardar / No guardar / Cancelar; `Save`/`SaveAs`/`Write` devuelven `bool`). Sin archivo y en blanco no
pregunta (un aviso innecesario se aprende a ignorar).
- **No hay flag dirty** (`BoardStore.MatchesFile`): serializa y compara contra el archivo
  **normalizado** (`ReadSnapshot`), sin `ActiveTab`.
- ⚠ Un archivo VIEJO recién abierto no puede leerse modificado: se normaliza a v2 con la MISMA
  `ParseFile` de la carga. Regresión: `BoardProbe` 8–10.
- Título con "•" si hay cambios, refrescado cada 500 ms.

### Formato `.mboard` v2 (`BoardStore`, con DTOs propios)

```
{ "Version": 2, "ActiveTab": 0, "Tabs": [ { "Name": "Board 1", "MasterVolume": 40, "Root": {…} } ], "Dock": {…} }
```

- Se escribe siempre v2. El formato viejo (árbol en la raíz, master en el nodo raíz) se lee como UNA
  pestaña con el nombre del archivo.
- ⚠ El formato se detecta mirando el JSON (`Tabs`/`Version` en la raíz), no con try/catch: un v2 roto
  leído como viejo cargaría vacío y pisaría el bueno. v2 sin pestañas = corrupto.
- Campos opcionales (`MasterVolume` ≠ 100, `PingPong: true`, `Dock`) se escriben solo si difieren del
  default, para que archivos viejos comparen igual.

### ⚠ Archivo ausente: la referencia NO se descarta

Sector con archivo inexistente conserva path y markers (`MissingPath`/`IsMissing`), estado ámbar y
"Buscar el archivo…". Si se vaciara, el "¿guardar?" al cerrar confirmaría la pérdida. `Relink` conserva
markers; reemplazar los resetea. Todo pasa por `SectorNode.Adopt`.
Regresión: `BoardProbe` caso 3 y `tools/test-missing.ps1`. ⚠ Los `.mboard` de prueba se generan con
`ConvertTo-Json` (backslashes sin escapar = "board corrupto").

### Extensión `.mboard` (`Persistence/BoardFile.cs`)

Asociación en **HKCU** `Software\Classes` (`AmpzMediaBoard.Board.1`, comando `"<exe>" "%1"` — ⚠ `%1`
entre comillas). Se registra sola solo si no había asociación; botón "Asociar .mboard" para repuntar.
`SHChangeNotify(SHCNE_ASSOCCHANGED)` tras escribir. El doble click llega por `e.Args`; se toma UN archivo.

### ⚠ Multi-instancia es intencional — sin mutex

Varios boards en varias ventanas es un caso de uso buscado. Instancias independientes.

### Título: board primero, marca al final (`MainWindow.UpdateTitle`)

`<archivo> • — AMB`. La taskbar trunca por la derecha. ⚠ `Ampz MediaBoard` a secas es el caption de los
`MessageBox`: si una prueba lo lee como título, hay un diálogo modal abierto.

---

## Atajos

| Tecla | Acción |
|---|---|
| `Espacio` | Play/pausa del sector seleccionado |
| `A` / `B` | Marker de inicio / fin en el playhead |
| `L` | Prender/apagar loop |
| `P` | Loop ida y vuelta (también ⇄) |
| `Ctrl+T` / `Ctrl+W` | Agregar / quitar pestaña (sin preguntar). También `+`, ×, click del medio |
| doble click en pestaña | Renombrar (Enter / click afuera confirma, Esc cancela) |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Pestaña siguiente / anterior |
| `Ctrl+Shift+RePág/AvPág` | Mover pestaña (también arrastrándola) |
| `Ctrl+1`…`Ctrl+8`, `Ctrl+9` | Ir a pestaña N; `Ctrl+9` = última |
| Shift+click en mute | Solo |
| `Ctrl+N` / `Ctrl+O` | Nuevo / Abrir (reemplazan el documento, con aviso) |
| `Ctrl+S` / `Ctrl+Shift+S` | Guardar / Guardar como |
| `Ctrl+V` | Cargar el archivo del portapapeles en el sector seleccionado |
| doble click en sector | Copiar el path |
| `Ctrl+E` | Distribuir |
| `F11` / `Esc` | Pantalla completa / salir |

- Atajos en `PreviewKeyDown` (en `KeyDown` un botón enfocado se come el Espacio).
- **Pantalla completa**: guarda y restaura `WindowStyle`/`WindowState`/`ResizeMode`. ⚠ `Normal` antes de
  `Maximized` (si no, la taskbar queda encima). ⚠ `F11`/`Esc` antes del guard de "sector seleccionado";
  `Esc` solo `Handled` en pantalla completa. Cambiar `WindowStyle` no recrea el HWND.
- **Markers a saltos grandes no es bug**: paso = duración/ancho. Precisión vía `A`/`B` o Shift+arrastre
  (`FineMsPerPixel = 10`, topeado a `normal/4`).
- **Doble click = copiar path** (`CopyPathToClipboard`, `SetDataObject(..., copy: true)`). Llega también
  sobre el video (medido). Excluye controles interactivos y sectores vacíos. Regresión:
  `tools/test-doubleclick.ps1` (clip `mandelbrot` que se mueve en todo el cuadro; sale 2 sin escritorio).
- **Tres formas de cargar un clip**, todas por `SectorNode.Adopt`: arrastrar, botón carpeta (acepta pegar
  un path) y `Ctrl+V` (lista de archivos **o** texto con comillas). Filtro del diálogo derivado de
  `MediaKinds`.
- **Partir es solo por los botones de la cabecera del sector** (sin ambigüedad de "¿cuál?"). Distribuir
  sí vive en la barra: actúa sobre todo el board.

---

## Estructura

| Carpeta | Contenido |
|---|---|
| `Layout/` | `LayoutNode`, `SplitNode`, `SectorNode` |
| `Board/` | `BoardViewModel`, `BoardView`, `BoardTab`, `TabOrder`, `PinnedDock` |
| `Media/` | `VlcEngine`, `MediaKind`, `PingPongMath`, `PingPongRenderer` |
| `Controls/` | `SectorView` (capa de render), `LoopTimeline` |
| `Persistence/` | `AppPaths`, `BoardStore`, `BoardFile` |
| raíz | `App`, `MainWindow`, `DiagLog`, íconos |
| `tools/` | Scripts y pruebas (abajo) |

**Pruebas** (`tools/`):
- Sondas `dotnet run`, salen 0/1: `LoopProbe` (binding del playhead), `BoardProbe` (swap, persistencia,
  ausente, distribuir, solo, master, pestañas, reorden, panel, ping-pong puro), `RestartProbe` (reinicio
  del loop, toca VLC), `PingPongProbe` (ffmpeg real). `WarmupProbe` **mide**, no testea.
- `test-*.ps1` contra la app corriendo: missing, loop, restart, close, tabs, tab-reorder, dock, audio,
  replace, pingpong, warmup, fullscreen, doubleclick, multi, boardfile. Código **2** = no se pudo medir
  (escritorio ocupado/bloqueado), no es verde ni rojo.
- ⚠ `test-boardfile.ps1` repunta la asociación `.mboard` a **Debug**: después, "Asociar .mboard" desde el
  Release.
- ⚠ `test-fullscreen.ps1` falla también en la base (el F11 por keystroke no llega).
- Otros: `make-ico.ps1`, `fetch-ffmpeg.ps1`, `build-installer.ps1`.

---

## Convenciones

- Comentarios en español, densos, orientados al *por qué*.
- **Todo test de regresión se ve FALLAR antes de creerle** (revertir el fix, confirmar rojo, restaurar).
  Ya pasó dos veces que una sonda pasaba contra el código roto.
- `Load()`/`Save()` con try/catch: un JSON corrupto o disco lleno nunca tumban la app.
- La capa de render no se desparrama: el video se toca solo en `SectorView`.
