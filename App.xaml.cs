using System.Windows;
using System.Windows.Threading;
using AmpzMediaBoard.Media;
using AmpzMediaBoard.Persistence;

namespace AmpzMediaBoard;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // La app NUNCA se cae en silencio: cualquier excepción no manejada queda escrita junto
        // al exe antes de morir. Con interop nativo de por medio (libvlc), un crash mudo es
        // imposible de diagnosticar después.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.ExceptionObject as Exception, "AppDomain");

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception, "Dispatcher");
            MessageBox.Show(
                $"Se rompió algo:\n\n{args.Exception.Message}\n\nEl detalle quedó en:\n{AppPaths.CrashLog}",
                "Ampz MediaBoard", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true; // Un error en un sector no se lleva puesto el board entero.
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception, "Task");
            args.SetObserved();
        };

        base.OnStartup(e);

        // Doble click en un .mboard desde Explorer: el shell nos pasa el path como argumento.
        var startupFile = BoardFile.FromCommandLine(e.Args);

        // La extensión se registra sola la primera vez, para que el doble click funcione sin que
        // el usuario tenga que descubrir un botón. Solo se escribe si NO había asociación previa:
        // así, tener el build de Debug abierto un rato no le roba la asociación al de Release.
        // Para repuntarla a este exe existe el botón "Asociar .mboard" de la barra.
        if (!BoardFile.IsRegistered()) BoardFile.Register();

        new MainWindow(startupFile).Show();

        // DESPUÉS del Show, a propósito: el precalentamiento de VLC es trabajo de fondo y no
        // tiene que demorar la primera pintada de la ventana. Ver VlcEngine.Warmup para el
        // porqué (el primer archivo arrastrado se comía el escaneo de plugins en el hilo de UI).
        VlcEngine.Warmup();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // El runtime de VLC se libera al final de todo: para este punto MainWindow ya soltó
        // los MediaPlayer de cada sector. Liberarlo con players vivos deja hilos nativos
        // decodificando sobre memoria que ya no existe.
        VlcEngine.Shutdown();
        base.OnExit(e);
    }

    private static void LogCrash(Exception? ex, string origin)
    {
        try
        {
            File.AppendAllText(AppPaths.CrashLog,
                $"""

                ===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{origin}] =====
                {ex}

                """);
        }
        catch
        {
            // Si ni el log se puede escribir, no hay nada más que hacer: no vamos a tirar una
            // excepción DESDE el handler de excepciones.
        }
    }
}
