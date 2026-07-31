namespace AmpzMediaBoard.Persistence;

/// <summary>
/// Toda la data del usuario vive en %APPDATA%\AmpzMediaBoard\, NUNCA junto al exe. El exe tiene
/// que poder vivir en Program Files (donde no se escribe) o copiarse entre máquinas sin arrastrar
/// el estado de nadie.
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AmpzMediaBoard");

    public static string BoardFile => Path.Combine(DataDir, "board.json");

    /// <summary>El log de crashes SÍ va junto al exe: si %APPDATA% es el que falla, ahí no escribiríamos.</summary>
    public static string CrashLog { get; } = Path.Combine(AppContext.BaseDirectory, "ampz-crash.log");

    public static void EnsureDataDir() => Directory.CreateDirectory(DataDir);
}
