using System.Globalization;

namespace AmpzMediaBoard.Board;

/// <summary>
/// Reglas PURAS del slideshow (▶ en la barra o Ctrl+P: recorre las pestañas en orden con un
/// temporizador). Sin timers ni WPF: el reloj y el cambio de pestaña los pone MainWindow (región
/// "Slideshow"); acá vive lo que se puede probar sin abrir una ventana (BoardProbe 24).
///
/// UN intervalo para TODO el documento (decisión aprobada: el más simple; por pestaña se puede
/// agregar después). Es dato del archivo; que el slideshow esté corriendo NO lo es.
/// </summary>
public static class Slideshow
{
    public const int DefaultSeconds = 10;

    /// <summary>
    /// Piso de 2 s: menos que eso no deja ni mirar la pestaña, y un Ken Burns (que dura el
    /// intervalo) de medio segundo es un salto, no un movimiento.
    /// </summary>
    public const int MinSeconds = 2;

    /// <summary>Techo de una hora: más es un número tipeado de más, no un slideshow.</summary>
    public const int MaxSeconds = 3600;

    public static int Clamp(int seconds) => Math.Clamp(seconds, MinSeconds, MaxSeconds);

    /// <summary>
    /// Lo que se tipeó en el campo de intervalo. Acepta "15" y "15s" (el campo muestra la unidad
    /// al lado y el usuario la copia). Fuera de rango se ACOTA, no se rechaza: "1" pide "lo más
    /// rápido", no un error. Basura → false, y el campo vuelve al valor que tenía.
    /// </summary>
    public static bool TryParse(string? text, out int seconds)
    {
        seconds = DefaultSeconds;
        var t = (text ?? string.Empty).Trim().TrimEnd('s', 'S').Trim();
        if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return false;
        seconds = (int)Math.Clamp(value, MinSeconds, MaxSeconds);
        return true;
    }

    /// <summary>
    /// La pestaña que sigue, dando la vuelta al final. Se calcula SIEMPRE desde la activa del
    /// momento (no desde un índice guardado): reordenar, agregar o quitar pestañas a mitad del
    /// slideshow no lo rompe — sigue desde donde estás mirando. Con una sola pestaña "la
    /// siguiente" es ella misma, y el cambio no hace nada.
    /// </summary>
    public static int NextIndex(int current, int count) =>
        count <= 0 ? 0 : current < 0 || current >= count ? 0 : (current + 1) % count;

    /// <summary>
    /// Cuenta regresiva junto al botón: "7s", y "2:05" desde el minuto. Redondea para ARRIBA:
    /// con 6,2 s restantes muestra "7s", y nunca "0s" (en cero ya cambió de pestaña).
    /// </summary>
    public static string CountdownText(long remainingMs)
    {
        var s = Math.Max(1, (long)Math.Ceiling(remainingMs / 1000.0));
        return s < 60 ? $"{s}s" : $"{s / 60}:{s % 60:00}";
    }

    /// <summary>
    /// El intervalo como se escribe en el `.mboard`: null (= ausente) cuando es el de por
    /// defecto. Misma regla que MasterVolume o PingPong: un 10 explícito y la ausencia describen
    /// el mismo documento, y tienen que normalizar igual para que un archivo viejo no se lea
    /// como modificado.
    /// </summary>
    public static int? SecondsOrNull(int? seconds) =>
        seconds is { } s && Clamp(s) != DefaultSeconds ? Clamp(s) : null;
}
