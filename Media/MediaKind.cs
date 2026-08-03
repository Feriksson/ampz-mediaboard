namespace AmpzMediaBoard.Media;

public enum MediaKind
{
    None,
    /// <summary>Se reproduce con un MediaPlayer de VLC. Tiene transporte y zona de loop.</summary>
    Video,
    /// <summary>Se muestra fijo con un Image de WPF. No tiene transporte ni loop.</summary>
    Image,
}

public static class MediaKinds
{
    // Lo que VLC decodifica sin que le instales nada (trae sus propios codecs).
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts",
        // OJO, decisión deliberada: el GIF va por VLC, NO por el Image de WPF.
        // ¿Por qué? Porque el Image de WPF NO anima GIFs — te muestra el primer frame y listo.
        // Mandándolo por VLC se anima Y encima te queda la zona de loop funcionando gratis.
        ".gif",
    };

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff",
    };

    public static MediaKind FromPath(string path)
    {
        var ext = Path.GetExtension(path);
        if (VideoExt.Contains(ext)) return MediaKind.Video;
        if (ImageExt.Contains(ext)) return MediaKind.Image;
        return MediaKind.None;
    }

    /// <summary>¿Este archivo lo podemos aceptar en un sector? Lo usa el drag&amp;drop.</summary>
    public static bool IsSupported(string path) => FromPath(path) != MediaKind.None;

    /// <summary>
    /// Filtro para los diálogos de "elegir archivo". Se DERIVA de las mismas listas de arriba:
    /// si mañana se agrega un formato, el diálogo lo ofrece solo. Escribirlo a mano sería un
    /// segundo lugar donde declarar lo mismo, y el día que se desincronicen el usuario ve un
    /// archivo en el diálogo que la app después rechaza (o al revés, que es peor).
    /// </summary>
    public static string DialogFilter { get; } = BuildFilter();

    private static string BuildFilter()
    {
        static string Patterns(IEnumerable<string> ext) => string.Join(";", ext.Select(e => "*" + e));

        var todos = Patterns(VideoExt.Concat(ImageExt));
        return $"Media ({todos})|{todos}"
             + $"|Video|{Patterns(VideoExt)}"
             + $"|Imágenes|{Patterns(ImageExt)}"
             + "|Todos los archivos|*.*";
    }
}
