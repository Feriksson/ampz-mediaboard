namespace AmpzMediaBoard.Media;

/// <summary>
/// Efecto de entrada de una FOTO (solo imágenes: un video ya se mueve solo). Se elige por sector
/// con el ✨ de la cabecera, se guarda en el `.mboard` (solo si ≠ None) y viaja con el clip en
/// <see cref="Layout.SectorNode.MediaSnapshot"/>. Se dispara cuando la pestaña del sector se
/// activa (incluido el slideshow), al cargar la imagen y al cambiar el efecto.
/// La animación en sí vive en <see cref="Controls.SectorView"/> (capa de render).
/// </summary>
public enum PhotoEffect
{
    None,

    /// <summary>Zoom lento 100%→112% con un paneo leve, ida y vuelta mientras se ve (un tramo = el intervalo).</summary>
    KenBurns,

    /// <summary>Entra de 108%→100% con fundido, ~600 ms: un "golpe" al aparecer.</summary>
    PunchIn,

    /// <summary>Fundido desde negro, 400 ms.</summary>
    Fade,
}

/// <summary>Reglas PURAS de los efectos (ciclo del ✨, nombres, persistencia, duraciones). BoardProbe 25.</summary>
public static class PhotoEffects
{
    /// <summary>Ken Burns sin slideshow corriendo: lo bastante lento para leerse como movimiento.</summary>
    public const int KenBurnsDefaultSeconds = 10;

    public const double KenBurnsEndScale = 1.12;
    public const double PunchInStartScale = 1.08;
    public const int PunchInMs = 600;
    public const int FadeMs = 400;

    /// <summary>
    /// El ✨ es un CICLO (None → Ken Burns → Punch-in → Fundido → None): cada click cambia el
    /// efecto Y lo reproduce en el acto, así que recorrerlo ES la vista previa. Un menú obligaría
    /// a abrir, elegir y cerrar para ver cada uno.
    /// </summary>
    public static PhotoEffect Next(PhotoEffect effect) => effect switch
    {
        PhotoEffect.None => PhotoEffect.KenBurns,
        PhotoEffect.KenBurns => PhotoEffect.PunchIn,
        PhotoEffect.PunchIn => PhotoEffect.Fade,
        _ => PhotoEffect.None,
    };

    public static string Label(PhotoEffect effect) => effect switch
    {
        PhotoEffect.KenBurns => "Ken Burns",
        PhotoEffect.PunchIn => "Punch-in",
        PhotoEffect.Fade => "Fundido",
        _ => "Sin efecto",
    };

    /// <summary>
    /// Como se escribe: el NOMBRE del enum (legible a ojo en el JSON y estable si se agregan
    /// efectos) o null (= ausente) para None. Mismo criterio que PingPong: un archivo anterior a
    /// la feature y un sector sin efecto describen lo mismo.
    /// </summary>
    public static string? ToFile(PhotoEffect effect) => effect == PhotoEffect.None ? null : effect.ToString();

    /// <summary>
    /// Lo leído del archivo. Desconocido (archivo de una versión futura, tocado a mano) → None, y
    /// normaliza a ausente: degradar en silencio como cualquier otro campo, nunca voltear la carga.
    /// Sin números: "1" no es un efecto, aunque Enum.TryParse lo aceptaría.
    /// </summary>
    public static PhotoEffect FromFile(string? text) =>
        text is { Length: > 0 } t && !char.IsDigit(t[0]) && !t.StartsWith('-') &&
        Enum.TryParse<PhotoEffect>(t, ignoreCase: true, out var effect) && Enum.IsDefined(effect)
            ? effect
            : PhotoEffect.None;

    /// <summary>
    /// Duración del Ken Burns: la del intervalo mientras corre el slideshow (el zoom termina
    /// justo cuando cambia la pestaña), o la de por defecto.
    /// </summary>
    public static TimeSpan KenBurnsDuration(int? slideshowSeconds) =>
        TimeSpan.FromSeconds(slideshowSeconds is { } s && s > 0 ? s : KenBurnsDefaultSeconds);

    /// <summary>
    /// Lado mayor (px) de la copia chica de la foto que hace de fondo desenfocado. Chica a
    /// propósito: estirada hasta llenar el sector, el propio reescalado ya la deja borrosa, y el
    /// BlurEffect de encima puede ser de radio moderado. Un radio grande sobre la foto a resolución
    /// completa, ×8 sectores en pantalla completa, es justo lo que no queremos pagar.
    /// </summary>
    public const int BackdropPixels = 96;

    /// <summary>
    /// El fondo desenfocado (la misma foto en "cubrir", detrás) aparece SOLO en una imagen con
    /// efecto. Sin efecto queda el letterbox negro de siempre: es una herramienta de estudio y la
    /// foto se ve tal cual es. Con efecto, el zoom/paneo mueve el borde de la foto y contra negro
    /// ese borde se lee como un error; contra la misma foto borrosa, como una composición.
    /// </summary>
    public static bool ShowsBackdrop(MediaKind kind, PhotoEffect effect) =>
        kind == MediaKind.Image && effect != PhotoEffect.None;

    /// <summary>
    /// Escala para bajar la foto a <see cref="BackdropPixels"/> de lado mayor. Nunca agranda (una
    /// foto ya chica se usa tal cual: agrandarla solo gasta memoria) y un tamaño inválido da 1.
    /// </summary>
    public static double BackdropScale(int pixelWidth, int pixelHeight)
    {
        var mayor = Math.Max(pixelWidth, pixelHeight);
        return mayor <= BackdropPixels ? 1.0 : (double)BackdropPixels / mayor;
    }
}
