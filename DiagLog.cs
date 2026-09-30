namespace AmpzMediaBoard;

/// <summary>
/// Log de diagnóstico para las pruebas end-to-end. APAGADO salvo que la variable de entorno
/// <c>AMPZ_DIAG_LOG</c> apunte a un archivo: el usuario nunca ve un log que no pidió.
///
/// Existe porque hay hechos que desde afuera no se pueden medir con confianza y desde adentro
/// son triviales (ver <c>tools/test-tabs.ps1</c>):
///  · "volver a una pestaña NO reabre archivos": un negro de 200ms se le escapa a cualquier
///    muestreo de pantalla, pero contar creaciones de Media es exacto (línea <c>open</c>);
///  · "cambiar de pestaña es instantáneo": medirlo por UI Automation mezcla el costo del
///    cambio con el del marshaling entre procesos (medido: hasta 250ms de la llamada Invoke
///    sola). Adentro se mide el cambio en sí (línea <c>switch</c>).
/// </summary>
public static class DiagLog
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("AMPZ_DIAG_LOG");

    public static bool Enabled => Path is { Length: > 0 };

    public static void Write(string line)
    {
        if (!Enabled) return;
        try { File.AppendAllText(Path!, line + Environment.NewLine); }
        catch { /* Un log de diagnóstico jamás puede voltear la app. */ }
    }
}
