using LibVLCSharp.Shared;

namespace AmpzMediaBoard.Media;

/// <summary>
/// Dueño ÚNICO de la instancia de LibVLC. Es una sola para toda la app, compartida por todos
/// los sectores: <c>LibVLC</c> es el runtime (codecs, módulos, config), no el reproductor.
/// Los que van uno por sector son los <c>MediaPlayer</c>.
///
/// Crear un LibVLC por celda funcionaría, pero cada uno recarga el set completo de módulos de
/// VLC → arranque lento y RAM multiplicada por 8 sin ganar absolutamente nada.
/// </summary>
public static class VlcEngine
{
    private static LibVLC? _libVlc;
    private static bool _shuttingDown;
    private static readonly Lock Gate = new();

    public static LibVLC Instance
    {
        get
        {
            if (_libVlc is not null) return _libVlc;
            lock (Gate)
            {
                return _libVlc ??= Create();
            }
        }
    }

    /// <summary>
    /// Paga el arranque de VLC en un hilo aparte, ni bien abre la app.
    ///
    /// El problema que resuelve fue REPORTADO: el PRIMER archivo que arrastrás tarda un
    /// montón y los siguientes son instantáneos. No es el archivo — es que
    /// <see cref="Instance"/> es perezoso, así que el primer drop es el que se come
    /// <c>new LibVLC()</c>, que abre y consulta los ~323 DLL de plugins UNO POR UNO. Y lo
    /// hace en el hilo de UI, adentro del handler del drop: la ventana queda dura.
    ///
    /// Medido con <c>tools/WarmupProbe</c> en esta máquina:
    ///   - <c>Core.Initialize()</c> ....... ~30-120 ms
    ///   - <c>new LibVLC()</c> ............ ~250-950 ms con el caché de archivos caliente,
    ///                                      y **17.700 ms** en frío (primer arranque después
    ///                                      de bootear, con Defender mirando los 323 DLL).
    ///   - primer archivo ................. ~300-1400 ms (demuxer + codec bajo demanda)
    ///   - archivos siguientes ............ ~50-160 ms
    /// Esa varianza de dos órdenes de magnitud es exactamente por qué se sentía aleatorio.
    ///
    /// Precalentar NO hace más rápido el escaneo: lo corre mientras el usuario todavía está
    /// yendo a buscar el archivo al Explorer. El costo sigue existiendo, pero deja de estar
    /// en el camino crítico.
    ///
    /// ⚠ No es una promesa: si el usuario suelta un archivo ANTES de que termine, el drop se
    /// queda esperando en el <c>lock</c>. Nunca queda PEOR que hoy (el trabajo es el mismo y
    /// ya está empezado), pero en frío puede seguir habiendo espera. Volver el camino del
    /// drop asincrónico es otra pelea, mucho más grande.
    /// </summary>
    public static void Warmup()
    {
        // Hilo dedicado y no del pool: en frío esto bloquea ~17s, y ocupar un hilo del
        // ThreadPool tanto tiempo le arruina el arranque a cualquier otra cosa.
        var thread = new Thread(() =>
        {
            try
            {
                lock (Gate)
                {
                    // La app puede cerrarse antes de que lleguemos acá. Crear el runtime
                    // nativo durante el apagado deja hilos de VLC vivos sobre un proceso
                    // que se está muriendo, y nadie los va a liberar.
                    if (_shuttingDown || _libVlc is not null) return;
                    _libVlc = Create();
                }
            }
            catch
            {
                // Un precalentamiento fallido NO es un error: si acá se rompe algo, el primer
                // drop lo vuelve a intentar por el camino de siempre y ahí sí se reporta.
            }
        })
        {
            IsBackground = true,
            Name = "vlc-warmup",
            // Por debajo de lo normal: el objetivo es que NO le compita al render de la
            // ventana ni a la carga del board que se abrió por doble click.
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    /// <summary>
    /// Crea un Media para <paramref name="path"/>. TODA apertura de archivo de la app pasa por
    /// acá (carga, re-montaje, relanzamiento del loop), y eso es lo que la hace MEDIBLE: cada
    /// una deja una línea en el <see cref="DiagLog"/> (apagado salvo en las pruebas).
    /// </summary>
    public static LibVLCSharp.Shared.Media NewMedia(string path)
    {
        DiagLog.Write($"open {path}");
        return new LibVLCSharp.Shared.Media(Instance, new Uri(path));
    }

    private static LibVLC Create()
    {
        // Core.Initialize() localiza libvlc.dll + el directorio de plugins. El paquete
        // VideoLAN.LibVLC.Windows los deja en <output>\libvlc\win-x64\, que es donde busca por
        // defecto. Si esto tira DllNotFoundException, el problema es que el build NO es x64.
        Core.Initialize();

        return new LibVLC(
            // Nada de overlays ni OSD de VLC encima del video: la UI la dibujamos NOSOTROS.
            "--no-video-title-show",
            "--no-osd",
            "--no-snapshot-preview",
            // El loop lo maneja LoopController, no VLC. Que VLC no intente nada por su cuenta.
            "--no-loop",
            "--no-repeat",
            // Sin subtítulos automáticos: en un board de referencia son ruido visual.
            "--no-sub-autodetect-file",
            // Seek PRECISO (no por keyframe). Es lo que hace que los markers de loop caigan
            // donde los pusiste y no ~1s antes. Cuesta un poco más de CPU al saltar: vale la pena.
            "--no-input-fast-seek",
            // ⚠ Salida de audio DirectSound, NO la de defecto (mmdevice/WASAPI). No es gusto: con
            // WASAPI el volumen y el mute que le pedís a UN MediaPlayer se aplican a la SESIÓN de
            // audio del proceso, que es UNA sola para toda la app. Todos los players comparten
            // el mismo control y gana el último que escribe: medido por readback (DiagLog), un
            // sector a 80 leía 10 y uno sin mute leía Mute=True. Eso dejaba muertos el volumen
            // por sector, el SOLO y el volumen general del board. Con DirectSound cada player
            // tiene su buffer propio y su volumen propio. Regresión: tools/test-audio.ps1.
            "--aout=directsound",
            // El log de VLC a stderr es ruidosísimo y no lo leemos nunca.
            "--quiet");
    }

    /// <summary>
    /// Liberaciones de reproductores EN CURSO (Stop + Dispose corriendo en otro hilo). Ver
    /// <see cref="Release"/>. Tiene su propio lock y NO usa <see cref="Gate"/> a propósito:
    /// Gate lo puede tener tomado el precalentamiento hasta ~17s en frío, y liberar un player
    /// (que pasa en el hilo de UI) no puede quedar esperando detrás de eso.
    /// </summary>
    private static readonly List<Task> Releases = [];

    private static readonly Lock ReleasesGate = new();

    /// <summary>
    /// Cuánto espera <see cref="Shutdown"/> a que terminen las liberaciones antes de rendirse.
    /// Un Stop() normal tarda 100-500ms; si después de esto sigue colgado, VLC está trabado y
    /// esperar más no lo destraba — solo deja el proceso zombi con la ventana ya cerrada.
    /// </summary>
    public static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Detiene y libera un reproductor FUERA del hilo de UI, en paralelo con cualquier otro.
    ///
    /// Existe por un bug reportado: cerrar un board con varios videos tardaba una eternidad y
    /// se veía cerrar "video por video". <c>MediaPlayer.Stop()</c> es BLOQUEANTE en libvlc —
    /// espera a que terminen el decoder, la salida de video y la de audio (WASAPI), ~100-500ms
    /// por player — y se hacía uno atrás del otro en el hilo de UI. Con N sectores eso es N
    /// veces el costo, con la ventana congelada a la vista. En paralelo el total es el del
    /// player MÁS LENTO, y encima no lo paga la UI.
    ///
    /// ⚠ Precondición del que llama: el player YA tiene que estar desenganchado de su VideoView
    /// (<c>SectorNode.Unload</c> pone <c>Player = null</c> antes de llamar acá). El hilo de
    /// fondo toca SOLO el objeto que recibe, nunca el nodo: el nodo puede volver a cargarse
    /// otro clip en el hilo de UI mientras el player viejo todavía se está deteniendo.
    ///
    /// Hilo dedicado (LongRunning) y no del pool: son llamadas nativas que bloquean, y con 8
    /// sectores ocuparían el pool entero justo cuando el board nuevo quiere arrancar.
    /// </summary>
    public static void Release(MediaPlayer player)
    {
        var task = Task.Factory.StartNew(() =>
        {
            // Stop() antes de Dispose(): soltar el player mientras decodifica deja el hilo de
            // VLC trabajando sobre memoria liberada. Cada paso con su try: si el Stop falla,
            // el Dispose se intenta igual; y nada de esto puede tirar abajo la app.
            try { player.Stop(); } catch { /* ver arriba */ }
            try { player.Dispose(); } catch { /* ver arriba */ }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        lock (ReleasesGate)
        {
            // Se poda al agregar: la lista solo tiene que recordar lo que TODAVÍA corre.
            Releases.RemoveAll(t => t.IsCompleted);
            Releases.Add(task);
        }
    }

    /// <summary>Se completa cuando terminaron todas las liberaciones pedidas hasta ahora.</summary>
    public static Task WhenReleased()
    {
        lock (ReleasesGate)
        {
            Releases.RemoveAll(t => t.IsCompleted);
            return Releases.Count == 0 ? Task.CompletedTask : Task.WhenAll(Releases.ToArray());
        }
    }

    /// <summary>
    /// Se llama en App.OnExit. Espera (con techo) a que terminen las liberaciones en curso y
    /// recién ahí suelta el runtime.
    ///
    /// ⚠ El orden NO es negociable: <c>LibVLC.Dispose()</c> con players todavía deteniéndose en
    /// otro hilo es liberar el runtime debajo de hilos nativos que lo están usando. Si la espera
    /// vence, NO se libera — el proceso se está muriendo igual y el SO recupera todo; un crash
    /// al salir o un proceso colgado son los dos peores que un runtime sin liberar.
    /// </summary>
    public static void Shutdown()
    {
        bool drained;
        try { drained = WhenReleased().Wait(ReleaseTimeout); }
        catch { drained = true; } // Las tareas tragan sus errores; esto es solo por las dudas.

        lock (Gate)
        {
            // Se marca ANTES de soltar el lock: si el hilo de precalentamiento está esperando
            // acá atrás, tiene que ver el apagado y NO crear un runtime nuevo que nadie
            // liberaría. Sin esto, cerrar la app en el primer segundo deja libvlc colgado.
            _shuttingDown = true;
            if (drained) _libVlc?.Dispose();
            _libVlc = null;
        }
    }
}
