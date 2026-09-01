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

### ⚠ SI HAY UN RELEASE PUBLICADO, REPUBLICALO — o el usuario prueba código viejo

El doble click en un `.mboard` abre el exe de **`bin\Release\...\publish\`**, no el de Debug. Si
después de un cambio solo hacés `dotnet build`, el usuario que abre su board **sigue corriendo el
binario anterior** y va a reportar que la feature nueva "no funciona". Ya pasó, y costó una sesión
entera de debug persiguiendo un arrastre que sí existía… en el otro binario.

Regla: cuando exista una carpeta `publish/` y la asociación apunte ahí, **republicá también**:
```powershell
dotnet publish AmpzMediaBoard.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true
```

Agrava el problema que, con la cadencia de release actual, el trabajo en `develop` **no mueve la
versión**: los dos binarios muestran el MISMO número. Por eso el build de Debug pinta **`dev`** al
lado de la versión en la barra (`MainWindow.ShowVersion`, bajo `#if DEBUG`). Si en la barra ves
`dev`, estás en Debug; si no, en el Release publicado. No saques esa marca.

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

### ⚠ "Distribuir" NO es poner todos los splits en 0.5

`BoardViewModel.Distribute()` reparte el board en partes iguales, y la parte interesante es por
qué la solución obvia está mal. En un árbol binario el tamaño de una hoja es el **PRODUCTO** de
los ratios que hay desde la raíz hasta ella. Con `A | (B / C)` los tres splits al 0.5 dan
**A=50%, B=25%, C=25%**: igualar los ratios iguala HERMANOS, no sectores.

Lo que sí funciona es repartir según **cuántas hojas cuelgan de cada lado**
(`ratio = hojas(First) / hojas(total)`): los factores se telescopean por el camino y toda hoja
termina valiendo exactamente `1/N`, sin importar la forma del árbol ni cómo se mezclen las
orientaciones. En el ejemplo: la raíz queda en 1/3, el split interno en 0.5, los tres al 33%.

⚠ **NO dispara `LayoutChanged`, dispara `RatiosChanged`** — y la diferencia se ve y se escucha.
`LayoutChanged` reconstruye el árbol visual entero, lo que obliga a `Remount()` en cada sector:
VLC reabre TODOS los archivos para retomarlos donde iban. Pagar un re-decode del board completo
por cambiar tres números es absurdo. `RatiosChanged` lo atiende `BoardView.ApplyRatios`, que
reescribe las `GridLength` que ya existen usando el mapa `SplitNode → Grid` que se arma en
`BuildSplit`. Ningún VideoView se toca, ningún clip parpadea, nadie pierde la posición.

El botón vive en la barra superior y no en la cabecera de cada sector, a diferencia de partir.
No contradice la regla de "partir es solo por los botones del sector": esa regla existe porque
"partir el sector seleccionado" te obliga a mirar cuál está seleccionado. Distribuir actúa sobre
el board ENTERO — no hay ningún "¿sobre cuál?" que resolver.

### ⚠ Arrastrar un divisor CONGELA los sectores — no lo saques

Reportado como *"cuando hago resize con varios videos corriendo se pone muy buggy"*. La causa no
es misteriosa y es la deuda técnica que este mismo documento ya tenía asumida: cada sector con
video hostea **una ventana nativa** (el HWND del `VideoView`) **más la ventana de overlay** de
LibVLCSharp. Al arrastrar un `GridSplitter`, WPF re-layoutea en **CADA píxel** del movimiento →
esas ventanas se reposicionan y redimensionan decenas de veces por segundo, y el vout de VLC
tiene que reconfigurar su superficie de salida en cada una **mientras sigue decodificando**. Con
dos clips ya se nota; con seis el arrastre es un slideshow con rastros de imagen vieja pegados.

La cura ataca las DOS mitades, y hacen falta las dos:

| Mitad | Quién | Qué hace |
|---|---|---|
| Decodificación | `SectorNode.Freeze/Thaw` | Pausa el clip: el decoder deja de empujar frames a una ventana en movimiento. `Tick()` se saltea los congelados (ni posición ni loop). |
| Layout | `SectorView.SetFrozen` | **COLAPSA** el VideoView: la ventana nativa sale del layout, así ni siquiera se la reposiciona. |

Lo orquesta `BoardView` desde el `DragStarted`/`DragCompleted` del splitter.

⚠ **Se COLAPSA, no se pone en `Hidden`.** `Hidden` sigue participando del layout: la ventana se
mediría y arreglaría igual en cada movimiento, o sea que el costo que queremos evitar se pagaría
completo. `Collapsed` la saca del cálculo.

⚠ **El descongelado cuelga de `DragCompleted`, que dispara TAMBIÉN al cancelar con `Esc`.** Si
colgara de un final "exitoso", un Esc a mitad de arrastre te dejaría el board entero pausado y en
negro para siempre.

⚠ **Solo se reanuda lo que estaba REPRODUCIENDO de verdad** (`_resumeAfterThaw`). Un clip que
vos pausaste a mano, o uno terminado, no puede arrancar solo porque moviste un divisor: eso sería
la app revirtiendo una decisión tuya.

⚠ En `SyncRender` el congelado entra en la **misma expresión** que decide `Video.Visibility`, no
como una asignación aparte. `SyncRender` puede dispararse en medio de un arrastre y devolvería el
video a la pantalla justo cuando lo estamos escondiendo; con una sola condición esa carrera no
existe.

Queda tapado con un velo ("Redimensionando…") en vez de negro pelado: un sector que se apaga sin
explicación se lee como que la app se rompió.

**Esto se va con la migración a custom rendering** (`WriteableBitmap`), igual que los cinco bugs
del HWND: sin ventana nativa no hay nada que reposicionar y el resize es layout de WPF y nada más.

### Un solo latido para todo el board

`BoardViewModel` tiene **UN** `DispatcherTimer` a ~33ms que itera todos los sectores, **no uno por
celda**. Ocho timers compitiendo por la misma cola del Dispatcher se pisan, y el jitter arruina
justo lo que queremos preciso: el punto de corte del loop.

### ⚠ VLC se precalienta al arrancar (`VlcEngine.Warmup`) — no lo saques

`VlcEngine.Instance` es perezoso, así que **el primer archivo que arrastrás es el que paga el
arranque del motor**, y lo pagaba en el hilo de UI, adentro del handler del drop: la ventana
quedaba dura. Se reportó como *"el primer archivo tarda un montón y los siguientes no"*.

El costo no es cargar una DLL: `new LibVLC()` abre y consulta los **~323 DLL de plugins, uno por
uno**. El paquete NuGet no trae `plugins.dat` (el caché de plugins de VLC) ni el `vlc-cache-gen`
que lo genera, así que ese escaneo se hace **entero, en cada `new LibVLC()`**.

Medido con `tools/WarmupProbe`:

| Etapa | Costo |
|---|---|
| `Core.Initialize()` | ~30–120 ms |
| `new LibVLC()` | **~250 ms caliente … 17.700 ms EN FRÍO** |
| primer archivo | ~300–1.400 ms (demuxer + codec bajo demanda) |
| archivos siguientes | ~50–160 ms |

Ese `17.700` no es un typo: es el primer arranque después de bootear, con Defender mirando los 323
DLL. **Dos órdenes de magnitud de varianza** — por eso se sentía aleatorio y no correlacionaba con
el archivo, sino con qué tan caliente estaba el caché del filesystem.

`App.OnStartup` dispara `Warmup()` **después** del `Show()`, en un hilo dedicado `BelowNormal`. No
acelera nada: corre el mismo trabajo mientras el usuario todavía va al Explorer a buscar el clip.
Dos detalles que no son cosméticos:
- **Hilo propio, no del ThreadPool**: en frío bloquea ~17 s y ocuparle un hilo al pool arruina el
  arranque de todo lo demás.
- **`_shuttingDown` dentro del lock de `Shutdown()`**: sin eso, cerrar la app en el primer segundo
  deja al hilo de warmup creando un `LibVLC` que ya nadie va a liberar.

Regresión: `tools/test-warmup.ps1`. **NO mide tiempo** a propósito — entre 250 ms y 17 s no hay
umbral que distinga nada. Mide el HECHO: `libvlc.dll` cargado en el proceso sin haber arrastrado
nada. Visto fallar comentando `Warmup()`.

⚠ Se midió y se **descartó** precargar los DLL de plugins a mano (`NativeLibrary.TryLoad` sobre
`libavcodec_plugin.dll` y compañía). Aporta ~150 ms y obliga a hardcodear rutas de plugins, que
se rompen **en silencio** el día que se poda la carpeta para achicar el publish — algo que este
mismo documento contempla. No lo agregues.

⚠ Sigue siendo posible esperar: si soltás un archivo ANTES de que termine el precalentamiento, el
drop se bloquea en el `lock`. Nunca queda peor que sin warmup (el trabajo es el mismo y ya
empezó), pero matarlo del todo exige volver asincrónico el camino del drop. No está hecho.

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

### ⚠ El INTERVALO NEGRO entre repeticiones — y por qué no era una config de VLC

Reportado como *"con varios videos, entre repetición y repetición aparece un intervalo negro"*.
Se buscó un flag de VLC para ajustar; **no existe**, porque la causa está en cuál de los dos
caminos de vuelta al marker A tomaba el loop:

| Camino | Cuándo | Qué hace VLC | Costo |
|---|---|---|---|
| `SeekTo` | el playhead cruza B a mitad de clip | mueve el input, que sigue ABIERTO | hipito de decenas de ms, la imagen NO se va |
| `RestartFrom` | el clip llegó a `Ended` | `Stop()` + Media NUEVO + `Play()`: reabre el archivo, re-inicializa demuxer y codec, vuelve a llenar el caché | **cientos de ms EN NEGRO** |

Con la zona por defecto (B = duración) caía SIEMPRE en la fila de abajo: el playhead nunca
alcanzaba a cruzar B porque VLC cortaba el stream primero. O sea que el caso MÁS COMÚN de la app
—soltar un clip y que loopee entero— era justo el que pagaba el reinicio caro. Con varios
sectores empeora: si los clips duran parecido se sincronizan y reabren todos a la vez.

El arreglo tiene DOS mitades, y la segunda es la que de verdad importa:

**1. `TailGuardMs = 150`** — la zona efectiva se cierra 150ms antes del final real, así el
playhead cruza B ANTES de que VLC llegue a `Ended` y el loop pasa a ser un seek. Es un `Min`, así
que a un marker B puesto a mitad de clip NO lo toca: solo muerde cuando B está pegado al final.
El precio es perder los últimos ~150ms; imperceptible al lado de medio segundo en negro.

**2. ⚠ `player.Time` NO AVANZA CONTINUO — se actualiza A SALTOS.** Esto es lo que hacía que la
mitad 1 sola fuera **una lotería**, y no se descubrió razonando sino MIDIENDO con `RestartProbe`:
sobre un clip de 4s a 25fps el valor se queda quieto ~10 ticks y después salta **~324ms de una**.
La resolución real de la posición es un orden de magnitud PEOR que el latido de 33ms. Si el
último salto caía por debajo del umbral, el clip llegaba al final igual — con el guard puesto,
el test fallaba 2 de cada 3 corridas.
→ Fix: entre salto y salto `Tick()` estima la posición con el **reloj de pared** (`_lastRealTimeMs`
+ transcurrido), que es lo que hace la barra de progreso de cualquier reproductor. Bonus real: el
playhead de la timeline dejó de moverse a los tirones.
- Solo se estima **mientras reproduce**: en pausa el playhead se iría solo con el video quieto.
- Techo `MaxExtrapolationMs = 600`: si VLC se cuelga (buffering, disco lento) la estimación no
  puede correr sola para siempre.
- `Reanchor()` en `SeekTo`/`RestartFrom`: tras un salto deliberado VLC devuelve la posición VIEJA
  por un par de vueltas, y sin reanclar la estimación te arrastra de vuelta al punto del que
  saltaste.

`RestartFrom` **NO se eliminó**: sigue siendo la red por si un tick se pierde del todo. Es el
camino excepcional ahora, no el normal.

Regresión: `tools/test-restart.ps1`, caso 3. ⚠ **El discriminador es el ESTADO de VLC, no la
posición** — y esto costó una vuelta: mirar "hasta dónde llegó el playhead" da OK **también contra
el código roto**, porque `RestartFrom` pisa `PositionMs` con el marker A dentro de la MISMA vuelta
del Tick y el pico nunca se muestrea. Hay que preguntar si el player pasó por `Ended`/`Stopped`, y
**antes** de llamar a `Tick()` (que es quien lo atiende y devuelve el estado a `Playing` en el
acto). Verificado en rojo por las dos mitades: sin extrapolación falla 2 de 3 corridas; con
`TailGuardMs = 0` el margen cae a ~45ms y falla el piso de 100ms.

⚠ Esto NO vuelve el loop invisible: el seek de VLC sigue sin ser frame-exact y queda el hipito de
decenas de ms que este documento ya declara aceptable. Lo que se fue es el NEGRO. Un loop
verdaderamente sin costura sigue exigiendo pre-decodificar la zona a RAM (anotado como v2).

Dos constantes que NO son arbitrarias:
- `LoopGuardMs = 40` — el polling es de 33ms; si esperáramos a superar B exacto nos pasaríamos de
  largo. Se dispara un poco antes.
- `ReseekCooldownMs = 150` — sin enfriamiento, una zona más corta que la latencia del seek de VLC
  entra en ráfaga de saltos y el video queda congelado tartamudeando.
- `TailGuardMs = 150` y `MaxExtrapolationMs = 600` — ver la sección del intervalo negro, arriba.

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

Cinco trampas que ya costaron una ronda de debug. La 1, la 2, la 3 y la 5 se reportaron desde la
UI; la cuarta se anticipó antes de que se viera.

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

**5. Las opciones de un `Media` SOBREVIVEN a `Stop()` + `Play()`.**
Reportado como *"moví el video de celda y al terminar vuelve a arrancar en posiciones extrañas, no
respeta el marker"*. Y es hijo directo del bug 4: mover un clip entre sectores lo recarga con
`:start-time=<posición>` para retomarlo donde iba, y **esa opción vive en el objeto `Media`, no en
la llamada a `Play()`**. Como el reinicio del loop era `Stop(); Play();` sobre el MISMO media, VLC
se la volvía a aplicar y el clip reiniciaba en la posición vieja en vez del marker A.
⚠ Se manifestaba **SOLO con el marker A en 0** (la zona por defecto), porque con A > 0 el
`if (start > 0) SeekTo(start)` lo corregía. Y A en 0 es justo el estado de un clip recién
arrastrado a otra celda — por eso se sentía errático y difícil de reproducir.
→ Fix: `SectorNode.RestartFrom(ms)` construye un `Media` NUEVO apuntando al marker. Bonus: el
punto de arranque lo resuelve VLC al ABRIR el archivo, así que ya no depende de que un seek
recién relanzado prenda.
Enfriamiento propio para el relanzamiento (`RelaunchCooldownMs = 500`, contra los 150 del seek):
mientras VLC reabre el archivo `Time` devuelve 0, y con el margen corto el caso 3 leía ese 0 como
que el salto falló y relanzaba de nuevo — el clip nunca terminaba de abrir.
Regresión: `tools/test-restart.ps1`. ⚠ Si alguna vez la tocás, ojo con la trampa que ya se pisó
UNA VEZ al escribirla: **medir la posición en el instante del reinicio no distingue nada**, porque
entre el `Stop()` y que VLC reabra el archivo `Time` devuelve 0 con bug y sin bug. La primera
versión de la sonda pasaba contra el código roto. Hay que dejar correr una ventana fija (~900ms) y
comparar por CERCANÍA contra {marker, `:start-time`}.

Las cinco **desaparecen** al migrar a custom rendering (`WriteableBitmap`): sin HWND no hay
ventana propia de VLC, ni ventana de overlay, ni re-montaje al re-parentar (que es lo que obliga a
usar `:start-time`, o sea que la 5 se va con la 4), ni airspace tragándose los eventos de drop.

### Mover media entre sectores = INTERCAMBIAR (`BoardViewModel.SwapMedia`)

Arrastrando la **cabecera** de un sector se mueve su media a otro. Y es un **swap**, no un
"mover y vaciar el origen", por una razón concreta: soltar sobre un sector OCUPADO tiene que
**reacomodar, no destruir**. Con "mover" a secas, el clip del destino se borraría en silencio — y
el que está reordenando su board no pidió borrar nada. Si el destino está vacío, el swap ES un
movimiento simple.

Lo que viaja es la **descripción** del media (`SectorNode.MediaSnapshot`: path, posición, markers,
volumen, mute), NO el `MediaPlayer`. Un reproductor de VLC está atado a la ventana de salida donde
arrancó: pasarlo de un sector a otro lo dejaría dibujando en el HWND equivocado — el mismo motivo
por el que existe `Remount`. Se recarga el clip en destino **desde la posición en la que iba**
(vía `:start-time`), que cuesta un re-decode y es una acción deliberada del usuario.

⚠ Las DOS fotos se toman ANTES de restaurar ninguna. Restaurar sobre un nodo lo modifica; sacar la
segunda foto después leería el contenido recién puesto y terminarías con el mismo clip duplicado
en los dos sectores.

**El asa de arrastre es la cabecera**, no el área de video: esa área es un HWND hosteado y
arrastrar desde ahí es poco confiable. Además hay umbral de arrastre (`SystemParameters.Minimum*
DragDistance`) y los botones de la cabecera están excluidos — sin eso, el menor temblor del mouse
convertiría "cerrar sector" en "mover el clip".

⚠ El nodo de origen viaja en un **campo estático** (`SectorView._dragSource`), NO adentro del
`DataObject`. El DataObject de WPF está pensado para cruzar procesos y envuelve lo que le metas en
COM; con objetos vivos y no serializables (y `SectorNode` arrastra un MediaPlayer nativo) eso es
pedir problemas. El arrastre nunca sale de esta ventana, así que un campo estático es más simple y
no puede fallar. Se limpia en un `finally` porque `DoDragDrop` es bloqueante y un origen colgado
haría que el próximo arrastre mueva el clip equivocado.

### Audio por sector

Volumen (`0..100`) y silencio son **por sector**, no globales: en un board con varios clips
corriendo lo normal es querer escuchar UNO y tener el resto de fondo o mudo. Ambos se persisten en
el `.mboard` y viajan con el clip al intercambiarlo.

**El mute va aparte del volumen, y no es "volumen a 0"**: así silenciar y volver no te hace perder
el nivel que habías ajustado. Se usa `MediaPlayer.Mute`, que es justo para eso.

⚠ **VLC no acepta el volumen hasta que existe la salida de audio**, y esa salida se crea recién
cuando el input abrió. Por eso `SectorNode.ApplyAudio` se llama en **TRES** momentos: al cambiar la
propiedad, en `StartPending` (después del `Play`), y en el tick donde la duración se conoce por
primera vez — que es la señal de que el media abrió de verdad. Llamarlo una sola vez al cargar deja
el nivel sin aplicar y el sector suena siempre a 100.

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

### ⚠ NO hay autoguardado. En ningún momento. Tampoco al cerrar.

Un board se escribe **solo cuando el usuario lo pide**: "Guardar board", `Ctrl+S`, `Ctrl+Shift+S`,
o el "Guardar" del diálogo de cambios sin guardar. `MainWindow.OnClosing` lo dice textual:
*"No se escribe NINGÚN estado al cerrar"*. Lo único que hace al cerrar es **preguntar**
(`ConfirmDiscardChanges`) y liberar el board.

Esto estuvo **documentado al revés acá mismo** hasta v1.4.1: tres pasajes afirmaban que el board
"se autoguarda al cerrar la ventana". Nunca fue cierto en el código. Y no era un detalle de
redacción — **envenenó una prueba**: `tools/test-missing.ps1` abría un board, lo cerraba y
comparaba el archivo contra sí mismo esperando que el cierre lo hubiera reescrito. Como nadie lo
reescribe nunca, la comparación daba igual SIEMPRE, incluso con la conservación de la referencia
rota a propósito. Un verde que no puede ponerse rojo.

Regla que se desprende: **una prueba que "cierra y compara el `.mboard`" no prueba nada.** Si
querés verificar qué se ESCRIBE, tenés que guardar explícitamente — y si es desde un `.ps1`,
ojo que `SendKeys`/`AppActivate` no llegan a la ventana de forma confiable. El camino bueno es
una sonda que llame a `BoardStore` directo (ver `tools/BoardProbe`).
### ⚠ Archivo ausente: la referencia NO se descarta (esto evita pérdida de datos)

Si el archivo de un sector no existe al abrir (movido, borrado, disco externo desconectado), el
sector **conserva el path y los markers** y se marca como ausente (`SectorNode.MissingPath` /
`IsMissing`). Se muestra un estado ámbar con el nombre, la carpeta donde vivía, y un botón
"Buscar el archivo…".

**Por qué importa y no es cosmético**: si el sector quedara vacío al no encontrar el archivo, la
referencia y la zona de loop que te costó ajustar se perderían **en el primer guardado**. Y ese
guardado es el que MENOS sospechás: al cerrar, `ConfirmDiscardChanges` compara el board en memoria
contra el archivo; un board degradado por la carga NO coincide, así que la app pregunta
*"¿guardar?"* — y la respuesta natural es que sí. Le decís que sí, y confirmás la pérdida con tu
propia mano. Con la referencia conservada, la comparación da igual, no hay pregunta, y el board
sobrevive ciclos indefinidos de abrir/cerrar.

Re-vincular (`SectorNode.Relink`) **conserva los markers**; soltar un archivo sobre un sector que
NO está ausente es reemplazar y sí los resetea (son de otro clip: mantenerlos marcaría una zona
que no tiene nada que ver).

Verificado en DOS niveles, y hacen falta los dos:

- **El round-trip** (lo que de verdad evita la pérdida): `tools/BoardProbe`, caso 3. Guarda un
  board con sectores ausentes y lo relee. **Visto fallar**: rompiendo el `sector.MarkMissing(path)`
  de `BoardStore.FromDto`, cae en "los markers siguen ahi".
- **End-to-end**: `tools/test-missing.ps1`. Board con un path fantasma + markers 1500/4200 → abrir
  → la app no se cae, el board CARGA (no lo rechaza como corrupto) y cerrar no degrada el archivo.

⚠ Ese `.ps1` estuvo **muerto** hasta v1.4.1 y nadie lo notó: apuntaba a
`%APPDATA%\AmpzMediaBoard\board.json`, el estado de sesión eliminado en v1.3.0. Como la app dejó
de escribir ahí, fallaba en TODAS las versiones — y una prueba que no puede pasar nunca no avisa
nada, se aprende a ignorarla.

⚠ Al reescribirlo apareció otra trampa: **el `.mboard` de prueba se genera con `ConvertTo-Json`,
nunca a mano.** Un path de Windows tipeado dentro de un JSON (`"D:\clips\x.mp4"`) lleva los
backslashes SIN ESCAPAR → es JSON inválido → la app lo rechaza con "board corrupto" y la prueba
termina midiendo el rechazo en vez del board. El síntoma delator es el título de la ventana:
si dice `Ampz MediaBoard` a secas, eso es el caption de un MessageBox, no la ventana.
- **`Load()`/`Save()` JAMÁS voltean la app**: try/catch → degradar a board vacío o a memoria.

---

### ⚠ El título va "board primero, marca al final" (`MainWindow.UpdateTitle`)

`The board — AMB`, no `Ampz MediaBoard — The board`. **No es preferencia estética.**

El botón de la barra de tareas de Windows trunca por la **derecha** y da lugar a un puñado de
caracteres. Con `Ampz MediaBoard — ` adelante había **18 caracteres antes de que el título dijera
algo**: el nombre del board se cortaba SIEMPRE y todas las ventanas se veían idénticas. Justo
cuando más importa distinguirlas — con varios boards abiertos a la vez, que es un caso de uso
buscado (ver "MULTI-INSTANCIA ES INTENCIONAL").

Lo que IDENTIFICA va primero; la marca va al final, donde puede perderse sin costo. El `Title` del
XAML se mantiene alineado (`Board sin guardar — AMB`): es lo que se ve en el diseñador y en el
instante previo a que corra `UpdateTitle`.

⚠ `Ampz MediaBoard` a secas ya NO es un título de ventana válido — es el **caption de todos los
`MessageBox`** de la app. Si una prueba lee `MainWindowTitle` y ve exactamente eso, lo que hay
arriba es un diálogo modal, no la ventana. Es el síntoma más rápido para detectar que la app te
está mostrando un error que no leíste.

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
| `Ctrl+V` | Cargar en el sector seleccionado el archivo del portapapeles |
| doble click | Copiar al portapapeles el path del archivo del sector |
| `Ctrl+E` | Distribuir: reparte el espacio en partes iguales entre todos los sectores |
| `F11` | Pantalla completa (toggle) |
| `Esc` | Salir de pantalla completa |

### Pantalla completa (`MainWindow`, región "Pantalla completa")

Saca bordes, esconde la barra superior y maximiza a la pantalla ENTERA. Se guarda el trío
`WindowStyle`/`WindowState`/`ResizeMode` y se restaura al salir: un toggle que te deja la ventana
de otro tamaño deja de ser un toggle.

⚠ **`WindowState = Normal` ANTES de `Maximized` no es redundante.** Si la ventana YA estaba
maximizada, cambiarle el `WindowStyle` **no la re-maximiza**: Windows le deja el rect que tenía,
que es el ÁREA DE TRABAJO → la barra de tareas queda ENCIMA del board. Medido: 2062×1214 en vez de
2048×1280. Es de esos errores que uno le echa al monitor y nunca al código.

⚠ **`F11` y `Esc` se enganchan ANTES del guard de "hay un sector seleccionado"**. La pantalla
completa no depende de qué celda esté activa, y un board recién abierto puede no tener ninguna:
colgarla del guard la haría funcionar día por medio. `Esc` solo se marca como `Handled` si estás
en pantalla completa — si no, le estarías robando el `Esc` a cualquier otra cosa.

Verificado que cambiar `WindowStyle` en caliente **NO recrea el HWND** de la ventana, o sea que el
`VideoView` sobrevive (era el riesgo real, ver bug 4). `tools/test-fullscreen.ps1` lo MIDE en vez
de mirarlo: el rect contra los bounds del monitor, y dos capturas de pantalla separadas en el
tiempo que tienen que DIFERIR — si el video quedara negro o congelado, falla.

### Los markers se mueven de a saltos grandes: NO es un bug, es el riel

El riel de `LoopTimeline` mapea el clip ENTERO sobre el ancho del sector, así que el paso mínimo
de un arrastre es `duración / ancho`. Un clip de 10 min en un sector de 400px da **1,5 s por
píxel**. Se reportó como "los markers no tienen sensibilidad"; no hay ninguna cuantización que
sacar, es la resolución del control.

Por eso existen las dos vías precisas, y ninguna es "arrastrar mejor":
- **`A` / `B`** fijan el marker EXACTO donde está el playhead — el flujo real de marcar una zona;
- **Shift + arrastrar** cambia el delta de proporcional a absoluto (`FineMsPerPixel = 10`).
  El fino se topea contra `normal/4`: en un clip corto sobre un sector ancho el arrastre normal ya
  puede ser más preciso que 10ms/px, y un "fino" más grueso que el normal se lee como un bug.

Va con tooltip en los thumbs: un modificador que nadie sabe que existe, no existe.

### Doble click en el sector = copiar el path

Mismo efecto que el botón `⧉` de la cabecera, pero sobre una superficie grande en vez de un
botón de 22px. Reusa `CopyPathToClipboard()`, así que hereda el visto de confirmación y el
`SetDataObject(..., copy: true)` que hace que el path sobreviva al cierre de la app.

⚠ **Funciona TAMBIÉN sobre el área de video, y eso no era lo esperado.** Esa área es un HWND
hosteado, y el airspace es justo el motivo por el que el asa de arrastre es la cabecera y no el
video — así que la suposición razonable era que el doble click ahí no llegaría. Se midió y
llega: la ventana nativa de VLC no se queda con los mensajes de mouse.

Está verificado por `tools/test-doubleclick.ps1`, que manda un doble click REAL con `SendInput`
y **lee el portapapeles**. ⚠ Y antes de creerse el resultado sobre el video, comprueba que abajo
del cursor haya video CORRIENDO: muestrea el pixel dos veces separadas en el tiempo y exige que
CAMBIE. Sin esa guarda la prueba sería un fraude — si VLC no renderizara nada, ese punto sería
WPF pelado, el click funcionaría igual y estaríamos concluyendo algo sobre un HWND que no está.

⚠ Por eso ese test usa un clip PROPIO (`mandelbrot`) y no el `ampz-loop-clip.mp4` compartido: el
patrón `testsrc` tiene el **centro estático**, el pixel no cambiaba nunca y la prueba se acusaba
a sí misma de no tener video. El clip de una prueba de movimiento tiene que MOVERSE en todos lados.

Quedan afuera los controles interactivos (botones, el volumen, el riel de la timeline): un doble
click ahí es el usuario operando ESE control, no pidiendo un path. Y un sector vacío no hace
nada — que se abra un explorador porque hiciste dos clicks de más es de lo que nadie pidió.

### Las TRES formas de poner un clip en un sector

Las tres terminan en `SectorNode.Adopt`, que centraliza la única regla que hay que respetar
siempre: si el sector estaba **huérfano** se RE-VINCULA conservando los markers; si tenía otro
clip se REEMPLAZA y los markers se resetean (son de otro video). Antes esa decisión estaba
repetida en cada punto de entrada — así es como se termina con un camino que se olvidó de
conservar los markers.

1. **Arrastrar** un archivo desde el Explorer.
2. **Botón de la cabecera** (ícono de carpeta) → diálogo de archivo. En su campo "Nombre" se
   puede **pegar un path completo** y dar Enter, que es lo que sirve para rutas largas o de red.
3. **`Ctrl+V`** sobre el sector seleccionado.

⚠ El pegado acepta las **dos** formas en que un path llega al portapapeles, porque el usuario no
piensa en cuál es: copiar el archivo en el Explorer (`Ctrl+C`, deja una **lista de archivos**) o
copiar la ruta como **texto** ("Copiar como ruta de acceso" de Windows, que además la envuelve en
comillas — se limpian). Soportar solo una haría que la feature funcione día por medio.

El filtro del diálogo se **deriva** de las listas de extensiones de `MediaKinds`: si mañana se
agrega un formato, el diálogo lo ofrece solo. Escribirlo a mano sería un segundo lugar donde
declarar lo mismo, y el día que se desincronicen el usuario ve en el diálogo un archivo que la app
después rechaza.

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
| `tools/` | Mantenimiento y pruebas. `make-ico.ps1` regenera el ícono desde el PNG. Los `test-*.ps1` son pruebas end-to-end de la app corriendo (archivo ausente, loop, arranque limpio, `.mboard`, multi-instancia, pantalla completa, precalentado de VLC, doble click para copiar el path). `LoopProbe/`, `BoardProbe/` y `RestartProbe/` son proyectos que referencian el código real: el primero es el test de regresión del playhead, el segundo cubre el intercambio de media entre sectores, la persistencia del audio **y el round-trip del archivo ausente**, el reparto en partes iguales de `Distribute`, el tercero el reinicio del loop al terminar el clip (bug 5 — este SÍ toca VLC y un archivo de verdad). Salen con código 0/1. `WarmupProbe/` es la excepción: **NO es un test, es una MEDICIÓN** del arranque en frío de VLC etapa por etapa — no falla, informa. |
| raíz | `App`, `MainWindow`, `video-marketing.png` (fuente del ícono), `ampz-mediaboard.ico`. |

---

## Convenciones del repo

- **Comentarios en español**, densos y orientados al *por qué*, no al *qué*. Mantené el estilo:
  cuando agregues lógica no trivial, explicá la razón.
- **Todo test de regresión se verifica AL REVÉS antes de creerle**: revertí el fix, confirmá que
  el test FALLA, y recién ahí restauralo. Un test que nunca viste fallar no es un test, es
  decoración. No es teoría: en esta app ya pasó DOS veces que una sonda pasaba contra el código
  roto (la del reinicio del loop midiendo el `Time` transitorio, ver bug 5). Si vas a escribir un
  `test-*.ps1` o un `*Probe/`, este paso no se saltea.
- `Load()`/`Save()` con try/catch silencioso. Un JSON corrupto o un disco lleno **nunca** tumban la app.
- Toda data de usuario a `AppPaths.DataDir`, jamás junto al exe.
- La capa de render no se desparrama: el video se toca en `SectorView` y en ningún otro lado.
