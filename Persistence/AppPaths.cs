namespace AmpzMediaBoard.Persistence;

/// <summary>
/// Rutas fijas de la app.
///
/// Ojo con lo que NO está acá: **no hay carpeta de datos en `%APPDATA%`**. La app no guarda
/// estado propio en ningún lado — el único lugar donde vive un board es su archivo `.mboard`,
/// donde el usuario lo puso. Si alguna vez volvés a necesitar `%APPDATA%`, leé antes la sección
/// "Arranque limpio" del CLAUDE.md: la ausencia de estado oculto es una decisión, no un olvido.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// El log de crashes va junto al **exe** y no a `%APPDATA%`: si el problema fuera justamente
    /// el acceso al perfil del usuario, ahí no podríamos escribir nada.
    /// </summary>
    public static string CrashLog { get; } = Path.Combine(AppContext.BaseDirectory, "ampz-crash.log");
}
