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
            // El log de VLC a stderr es ruidosísimo y no lo leemos nunca.
            "--quiet");
    }

    /// <summary>Se llama en App.OnExit. Antes hay que haber liberado todos los MediaPlayer.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            // Se marca ANTES de soltar el lock: si el hilo de precalentamiento está esperando
            // acá atrás, tiene que ver el apagado y NO crear un runtime nuevo que nadie
            // liberaría. Sin esto, cerrar la app en el primer segundo deja libvlc colgado.
            _shuttingDown = true;
            _libVlc?.Dispose();
            _libVlc = null;
        }
    }
}
