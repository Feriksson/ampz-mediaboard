using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AmpzMediaBoard.Media;

/// <summary>
/// Reglas PURAS del loop ida y vuelta (ping-pong). Nada de VLC, nada de ffmpeg, nada de disco:
/// todo lo que se puede equivocar en silencio (qué punto del clip original es cada instante del
/// archivo generado, cuándo una zona es demasiado larga, cuándo dos pedidos son "el mismo
/// archivo") vive acá para poder probarlo sin abrir un decodificador (tools/BoardProbe).
///
/// ¿Por qué hace falta un archivo generado? libvlc NO reproduce hacia atrás: no acepta una
/// velocidad negativa, y simularlo con seeks cuadro a cuadro sobre un H.264 es inviable (cada
/// seek decodifica desde el keyframe anterior). Así que ffmpeg pre-arma UN archivo con la zona
/// hacia adelante seguida de la zona al revés, y el sector loopea ESE archivo con la maquinaria
/// de loop de siempre (<c>SectorNode.EnforceLoop</c>).
///
/// Forma del archivo generado, con d = B − A (la zona en el tiempo ORIGINAL):
/// <code>
///   [0, d)        ida:     original A → B
///   [d, 2d)       vuelta:  original B → A
///   [2d, 2d+pad)  colchón: original A → A+pad   (NO se reproduce: el loop salta a 0 en 2d)
/// </code>
/// El colchón existe por el <c>TailGuardMs</c> de SectorNode: el loop corta 150 ms ANTES del
/// final real del archivo para que la vuelta sea un seek y no un relanzamiento en negro (ver
/// "El INTERVALO NEGRO" en el CLAUDE.md). Sin colchón, ese recorte se comería los últimos 150 ms
/// de la VUELTA y el playhead saltaría de A+150ms a A: un tirón visible justo en el giro. Con el
/// colchón, el punto de corte (2d) queda lejos del final del archivo y el guard no muerde nada.
/// Y como el colchón empieza en el MISMO cuadro que el principio del archivo (A), si un tick se
/// atrasa y el playhead se mete en él, lo que se ve sigue siendo la continuación correcta.
/// </summary>
public static class PingPongMath
{
    /// <summary>
    /// Tope de la zona, en ms (límite aceptado por el usuario). No es arbitrario: el filtro
    /// <c>reverse</c> de ffmpeg guarda TODOS los cuadros de la zona en RAM antes de emitir el
    /// primero invertido. A 720p son ~1,4 MB por cuadro: 30 s a 30 fps ≈ 1,2 GB de pico, a 60 fps
    /// el doble. Más largo que esto deja de ser una generación de fondo y pasa a ser un riesgo.
    /// </summary>
    public const double MaxZoneMs = 30_000;

    /// <summary>
    /// Zona mínima: la misma separación mínima entre markers que impone la timeline
    /// (<c>LoopTimeline.MinSpanMs</c>). Por debajo no hay ni tres cuadros que invertir.
    /// </summary>
    public const double MinZoneMs = 120;

    /// <summary>Colchón al final del archivo, en ms. Ver el resumen de la clase.</summary>
    public const double PadMs = 400;

    /// <summary>
    /// Versión del FORMATO del archivo generado. Entra en la clave del caché: si mañana cambia el
    /// comando de ffmpeg (otro códec, otro colchón), los archivos viejos dejan de coincidir solos
    /// en vez de reproducirse con una forma que el mapeo de tiempo ya no entiende.
    /// </summary>
    public const int FormatVersion = 1;

    public enum ZoneCheck { Ok, TooShort, TooLong }

    /// <summary>
    /// La zona EFECTIVA del loop, en tiempo original: la MISMA regla que <c>EnforceLoop</c> usa
    /// para el loop normal. Si divergieran, el ping-pong invertiría una zona distinta de la que
    /// loopea el modo normal (p. ej. con LoopEnd todavía en 0 recién cargado el clip).
    /// </summary>
    public static (double Start, double End) EffectiveZone(double loopStartMs, double loopEndMs, double durationMs)
    {
        var start = Math.Clamp(loopStartMs, 0, Math.Max(0, durationMs));
        var end = loopEndMs > start ? Math.Min(loopEndMs, durationMs) : durationMs;
        return (start, end);
    }

    public static ZoneCheck Check(double startMs, double endMs)
    {
        var d = endMs - startMs;
        if (!(d >= MinZoneMs)) return ZoneCheck.TooShort;   // !(>=) atrapa también NaN
        return d > MaxZoneMs ? ZoneCheck.TooLong : ZoneCheck.Ok;
    }

    /// <summary>Colchón real para una zona: nunca más largo que la zona misma.</summary>
    public static double PadFor(double zoneMs) => Math.Min(PadMs, Math.Max(0, zoneMs));

    /// <summary>
    /// Punto del archivo generado donde el loop vuelve a 0: el final de la vuelta (2d). Todo lo
    /// que viene después es colchón.
    /// </summary>
    public static double LoopEndMs(double startMs, double endMs) => 2 * Math.Max(0, endMs - startMs);

    /// <summary>
    /// Tiempo del archivo GENERADO → tiempo del clip ORIGINAL. Es lo que ve el usuario en la
    /// timeline y en el reloj: el archivo generado es un detalle de implementación, los markers y
    /// el playhead siguen hablando del clip que él soltó.
    /// </summary>
    public static double ToOriginal(double generatedMs, double startMs, double endMs)
    {
        var d = Math.Max(0, endMs - startMs);
        var t = Math.Max(0, generatedMs);
        if (t < d) return startMs + t;                          // ida
        if (t < 2 * d) return endMs - (t - d);                  // vuelta
        return Math.Min(endMs, startMs + (t - 2 * d));          // colchón (= otra vez la ida)
    }

    /// <summary>
    /// Tiempo ORIGINAL → tiempo del archivo generado, sobre la IDA. Lo usa el click en la
    /// timeline durante el ping-pong: cada punto de la zona aparece dos veces en el archivo
    /// (ida y vuelta) y se elige la ida porque es la lectura obvia de "llevame a este cuadro":
    /// de ahí en adelante el clip avanza, como en el modo normal. Un click fuera de la zona se
    /// acota a la zona — el ping-pong no reproduce nada fuera de ella.
    /// </summary>
    public static double FromOriginal(double originalMs, double startMs, double endMs) =>
        Math.Clamp(originalMs, startMs, Math.Max(startMs, endMs)) - startMs;

    /// <summary>
    /// Clave del archivo generado: hash de (ruta, fecha de modificación, A, B, formato).
    ///
    /// - La ruta va NORMALIZADA (completa y en mayúsculas): en Windows <c>D:\Clips\x.mp4</c> y
    ///   <c>d:\clips\X.MP4</c> son el mismo archivo y no pueden generar dos veces lo mismo.
    /// - La fecha de modificación hace que re-exportar el clip con el mismo nombre invalide el
    ///   caché: sin ella, el ping-pong mostraría la versión VIEJA del video.
    /// - A y B se redondean al ms: un marker que "no se movió" pero quedó con ruido de punto
    ///   flotante (un Shift+arrastre que vuelve a su lugar) no dispara 2 s de ffmpeg al pedo.
    /// </summary>
    public static string CacheKey(string sourcePath, DateTime lastWriteUtc, double startMs, double endMs)
    {
        string full;
        try { full = Path.GetFullPath(sourcePath); }
        catch { full = sourcePath; }

        var text = string.Join('|',
            FormatVersion.ToString(CultureInfo.InvariantCulture),
            full.ToUpperInvariant(),
            lastWriteUtc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            ((long)Math.Round(startMs, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture),
            ((long)Math.Round(endMs, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    /// <summary>
    /// Segundos para la línea de comandos de ffmpeg. ⚠ InvariantCulture NO es opcional: en un
    /// Windows en español el separador decimal es la coma y "-ss 12,4" es un error de ffmpeg.
    /// Mismo gotcha que el <c>:start-time</c> de VLC (ver bug 4 en el CLAUDE.md).
    /// </summary>
    private static string Seconds(double ms) =>
        (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Argumentos de ffmpeg para generar el archivo ida y vuelta. Pura: se prueba sin ffmpeg.
    ///
    /// - <c>-ss A -t d</c> como opciones de ENTRADA: ffmpeg busca el keyframe anterior a A y
    ///   decodifica descartando hasta A exacto (accurate_seek, el default al transcodificar), así
    ///   que el corte es al cuadro aunque la fuente sea H.264 con GOP largo. Verificado por
    ///   tools/PingPongProbe comparando cuadros del generado contra la fuente en A+t. Se usa
    ///   <c>-t</c> (duración) y no <c>-to</c>: la semántica de <c>-to</c> junto con <c>-ss</c> de
    ///   entrada cambió entre versiones de ffmpeg; una duración no es ambigua.
    /// - <c>-an -sn -dn</c>: sin audio (límite aceptado: no hay "audio al revés" que valga la
    ///   pena), sin subtítulos ni datos.
    /// - Escala a 720p de alto como MÁXIMO, ANTES del split: el <c>reverse</c> guarda todos los
    ///   cuadros en RAM, y guardarlos ya achicados es la diferencia entre ~1,2 GB y ~2,7 GB con
    ///   una fuente 1080p. <c>-2</c> mantiene el aspecto con un ancho par (MJPEG 4:2:0 lo exige).
    /// - MJPEG intra-only (<c>-q:v 3</c>): TODO cuadro es keyframe. El build LGPL no trae libx264,
    ///   y aunque lo trajera, un códec con GOP haría del salto de vuelta a 0 un seek que
    ///   decodifica desde un keyframe. Con intra-only cada seek es exacto al cuadro y barato,
    ///   que es justo lo que el loop hace dos veces por vuelta menos que antes. Precio: archivos
    ///   grandes (~9 MB por segundo de zona a 720p), aceptable para un temporal descartable.
    /// - <c>yuvj420p</c>: el rango de color que el encoder MJPEG espera; sin él ffmpeg se queja o
    ///   elige otro formato según la versión.
    /// - Contenedor MKV: VLC lo abre sin problemas, informa la duración (para el mapeo) y busca
    ///   por índice (Cues) en vez de escanear.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string sourcePath, double startMs, double endMs, string outputPath)
    {
        var d = Math.Max(0, endMs - startMs);
        var pad = PadFor(d);
        var filter =
            "[0:v]scale=-2:'min(720,ih)',format=yuvj420p,split=3[f][r][p];" +
            "[r]reverse[rv];" +
            $"[p]trim=duration={Seconds(pad)},setpts=PTS-STARTPTS[pp];" +
            "[f][rv][pp]concat=n=3:v=1:a=0[out]";

        return
        [
            "-hide_banner", "-nostdin", "-nostats", "-v", "error", "-y",
            "-ss", Seconds(startMs), "-t", Seconds(d), "-i", sourcePath,
            "-an", "-sn", "-dn",
            "-filter_complex", filter,
            "-map", "[out]",
            "-c:v", "mjpeg", "-q:v", "3",
            outputPath,
        ];
    }
}

/// <summary>
/// En qué anda el ping-pong de un sector. Lo pinta la cabecera del transporte (SectorView):
/// cada estado que NO es "Active" explica por qué el sector está loopeando el original.
/// </summary>
public enum PingPongStatus
{
    /// <summary>Apagado.</summary>
    Off,
    /// <summary>Pedido; esperando la duración del clip o ffmpeg generando.</summary>
    Preparing,
    /// <summary>Reproduciendo el archivo ida y vuelta.</summary>
    Active,
    /// <summary>Esta instalación no trae ffmpeg. El pedido se CONSERVA (y se guarda).</summary>
    Unavailable,
    /// <summary>Rechazado: la zona supera <see cref="PingPongMath.MaxZoneMs"/>. El toggle queda apagado.</summary>
    TooLong,
    /// <summary>Rechazado: la zona es más corta que <see cref="PingPongMath.MinZoneMs"/>.</summary>
    TooShort,
    /// <summary>ffmpeg falló. El pedido se conserva; el motivo va al log. Ver PingPongRenderer.LogFailure.</summary>
    Error,
}
