using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AmpzMediaBoard.Persistence;

/// <summary>
/// Todo lo que hace que un board sea un ARCHIVO del usuario: la extensión propia, los filtros de
/// los diálogos y la asociación con el shell de Windows para que se abra con doble click.
///
/// Distinción importante que no hay que mezclar: el **estado de sesión** vive en `%APPDATA%` y su
/// única función es que no pierdas el trabajo al cerrar. Un **archivo `.mboard`** es un documento
/// tuyo, con nombre, en la carpeta que quieras. Son cosas distintas y conviven.
/// </summary>
public static class BoardFile
{
    public const string Extension = ".mboard";

    /// <summary>ProgId: la identidad del tipo de archivo en el registro. Lleva sufijo de versión por convención del shell.</summary>
    private const string ProgId = "AmpzMediaBoard.Board.1";

    private const string TypeDescription = "Board de Ampz MediaBoard";

    public const string DialogFilter =
        "Board de Ampz MediaBoard (*" + Extension + ")|*" + Extension + "|Todos los archivos|*.*";

    /// <summary>Le avisa al shell que las asociaciones cambiaron: sin esto el ícono nuevo tarda en aparecer (o no aparece hasta reiniciar Explorer).</summary>
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Agrega la extensión si el usuario tipeó un nombre pelado en el diálogo de guardar.</summary>
    public static string EnsureExtension(string path) =>
        Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase)
            ? path
            : path + Extension;

    /// <summary>¿La extensión ya está asociada a ALGO? (no necesariamente a este exe)</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Extension}");
            return key?.GetValue(null) as string == ProgId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Asocia los <c>.mboard</c> con el ejecutable ACTUAL, para que el doble click los abra acá.
    ///
    /// Se escribe en <c>HKEY_CURRENT_USER</c> y NO en HKEY_LOCAL_MACHINE: alcance de usuario, sin
    /// pedir permisos de administrador, y sin tocar la configuración de nadie más en la máquina.
    /// Devuelve false si no se pudo (registro bloqueado por política, etc.) — nunca tira.
    /// </summary>
    public static bool Register()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            using (var ext = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Extension}"))
                ext.SetValue(null, ProgId);

            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
                prog.SetValue(null, TypeDescription);

            // El ",0" es el índice del ícono dentro del exe: el ApplicationIcon del proyecto.
            // Así los .mboard se ven en Explorer con el ícono de la app.
            using (var icon = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon"))
                icon.SetValue(null, $"\"{exe}\",0");

            // El "%1" DEBE ir entre comillas: sin ellas, cualquier board en una carpeta con
            // espacios (que en Windows son casi todas) llega partido en varios argumentos.
            using (var cmd = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
                cmd.SetValue(null, $"\"{exe}\" \"%1\"");

            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>¿El argumento de línea de comandos es un board que existe? Lo usa el doble click.</summary>
    public static string? FromCommandLine(string[] args)
    {
        var path = args.FirstOrDefault(a =>
            !a.StartsWith('-') &&
            Path.GetExtension(a).Equals(Extension, StringComparison.OrdinalIgnoreCase));

        return path is not null && File.Exists(path) ? path : null;
    }
}
