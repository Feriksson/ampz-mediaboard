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

    /// <summary>Zoom lento 100%→112% con un paneo leve, a lo largo de todo el intervalo.</summary>
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
}
