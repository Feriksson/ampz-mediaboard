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
            _libVlc?.Dispose();
            _libVlc = null;
        }
    }
}
