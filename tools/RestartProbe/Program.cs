// ⚠ System.IO explícito: los ImplicitUsings de WPF NO lo incluyen (a diferencia de una consola).
// Es la misma razón por la que el .csproj de la app lo declara a mano.
using System.IO;
using AmpzMediaBoard.Layout;

// Prueba de REGRESION: cuando un clip que fue MOVIDO DE CELDA (o re-montado al partir un sector)
// llega al final, tiene que volver al marker A — no a la posicion en la que estaba cuando lo
// arrastraste.
//
// El bug original: mover el clip lo recarga con la opcion `:start-time=<posicion>` para retomarlo
// donde iba. Esa opcion vive en el objeto Media, no en el Play(). Como EnforceLoop reiniciaba con
// `Stop(); Play();` (sin media nuevo), VLC volvia a aplicar el :start-time y el clip reiniciaba en
// la posicion vieja, ignorando el marker. Con el marker A en 0 no habia ni seek correctivo, asi
// que quedaba loopeando desde un punto arbitrario para siempre.
//
// Esta prueba NO simula nada: usa SectorNode de verdad, VLC de verdad y un clip de verdad.

internal static class Program
{
    private static int _fallas;

    [STAThread]
    private static int Main(string[] args)
    {
        var clip = args.Length > 0 ? args[0] : string.Empty;
        if (clip.Length == 0 || !File.Exists(clip))
        {
            Console.WriteLine("FALTA EL CLIP. Uso: dotnet run --project tools/RestartProbe -- <ruta al video>");
            Console.WriteLine("Generalo con: ffmpeg -f lavfi -i testsrc=size=320x240:rate=25:duration=4 -pix_fmt yuv420p loop-clip.mp4");
            return 1;
        }

        Console.WriteLine($"clip: {clip}");
        Console.WriteLine();

        // Caso 1: el que reportó el usuario. Marker A en 0 (la zona por defecto), clip que llega
        // al sector "desde otra celda" arrancando por la mitad.
        Escenario("A en 0 (zona por defecto), llega desde otra celda arrancando en 2,0s",
            clip, llegaEnMs: 2000, loopStartMs: 0);

        // Caso 2: con marker A adentro del clip. Verifica que el reinicio respeta el marker y no
        // se queda en el :start-time (que acá es MAYOR que A, o sea el error se vería igual).
        Escenario("A en 1,0s, llega desde otra celda arrancando en 2,5s",
            clip, llegaEnMs: 2500, loopStartMs: 1000);

        Console.WriteLine();
        if (_fallas == 0) Console.WriteLine("=== TODO OK ===");
        else Console.WriteLine($"=== {_fallas} FALLA(S) ===");
        return _fallas == 0 ? 0 : 1;
    }

    private static void Escenario(string titulo, string clip, double llegaEnMs, double loopStartMs)
    {
        Console.WriteLine($"=== {titulo} ===");

        using var nodo = new SectorNode();

        // Esto es EXACTAMENTE lo que hace SectorNode.Restore al recibir un media arrastrado desde
        // otro sector: recarga el archivo desde la posición en la que iba.
        nodo.Load(clip, llegaEnMs);
        nodo.LoopStartMs = loopStartMs;
        nodo.LoopEnabled = true;

        // En la app esto lo dispara la vista cuando la superficie de video está lista. Acá no hay
        // vista: VLC abre su propia ventanita y no molesta a nadie.
        nodo.StartPending();

        // Se batea el latido del board a mano, al mismo ritmo (~33ms).
        var duracion = Bombear(nodo, hasta: () => nodo.DurationMs > 0, limiteMs: 5000);
        if (nodo.DurationMs <= 0)
        {
            Fallo("VLC nunca reportó la duración del clip");
            return;
        }
        Console.WriteLine($"  duración detectada: {nodo.DurationMs:0} ms  (abrió en {duracion} ms)");

        // Se deja correr hasta que el clip termina y el loop lo relanza. El reinicio se detecta
        // como una CAÍDA de la posición: si el playhead retrocede, es que dio la vuelta.
        var anterior = nodo.PositionMs;
        var maxima = anterior;
        double? reinicioEn = null;

        Bombear(nodo, hasta: () =>
        {
            var pos = nodo.PositionMs;
            maxima = Math.Max(maxima, pos);
            if (pos < anterior - 500) reinicioEn ??= pos;
            anterior = pos;
            return reinicioEn is not null;
        }, limiteMs: 15000);

        Console.WriteLine($"  posición máxima alcanzada: {maxima:0} ms");

        if (reinicioEn is null)
        {
            Fallo("el clip NUNCA volvió a empezar: el loop no disparó");
            return;
        }

        // ⚠ ACÁ NO SE MIDE LA POSICIÓN DEL INSTANTE DEL REINICIO, y esa fue la primera versión
        // EQUIVOCADA de esta prueba: entre el Stop() y que VLC termine de reabrir el archivo,
        // `Time` devuelve 0. Leer ahí da 0 SIEMPRE — con bug y sin bug — y la prueba pasaba
        // aunque el clip estuviera reiniciando en el lugar equivocado.
        //
        // Se deja correr una VENTANA fija y se mide después: si arrancó en el marker A, a los
        // ~900ms el playhead está cerca de A+900; si arrancó en el :start-time viejo, está cerca
        // de start-time+900. Esos dos valores están a más de un segundo — imposible confundirlos.
        const int VentanaMs = 900;
        var arranqueVentana = Environment.TickCount64;
        Bombear(nodo, hasta: () => false, limiteMs: VentanaMs);
        var transcurrido = Environment.TickCount64 - arranqueVentana;

        var estimado = nodo.PositionMs - transcurrido;
        Console.WriteLine($"  {transcurrido} ms después del reinicio el playhead está en {nodo.PositionMs:0} ms");
        Console.WriteLine($"  -> reinició cerca de {estimado:0} ms   (marker A = {loopStartMs:0} ms, :start-time era {llegaEnMs:0} ms)");

        if (nodo.PositionMs <= 0)
        {
            Fallo("después del reinicio el playhead no avanza: el clip quedó trabado");
            return;
        }

        // Se compara por CERCANÍA en vez de por tolerancia absoluta: el reinicio se come unos
        // cientos de ms reabriendo el archivo, así que el estimado siempre queda un poco por
        // debajo del real. Lo que importa no es el número exacto, es a cuál de los dos se parece.
        var distanciaAlMarker = Math.Abs(estimado - loopStartMs);
        var distanciaAlStartTime = Math.Abs(estimado - llegaEnMs);

        if (distanciaAlMarker < distanciaAlStartTime)
            Console.WriteLine("  [OK ] volvió al marker A");
        else
            Fallo($"VOLVIÓ AL :start-time ({llegaEnMs:0} ms) EN VEZ DEL MARKER A ({loopStartMs:0} ms) — es el bug original");

        Console.WriteLine();
    }

    /// <summary>Late el nodo cada ~33ms (el ritmo real del board) hasta que se cumpla la condición.</summary>
    private static long Bombear(SectorNode nodo, Func<bool> hasta, int limiteMs)
    {
        var arranque = Environment.TickCount64;
        while (Environment.TickCount64 - arranque < limiteMs)
        {
            nodo.Tick();
            if (hasta()) break;
            Thread.Sleep(33);
        }
        return Environment.TickCount64 - arranque;
    }

    private static void Fallo(string mensaje)
    {
        Console.WriteLine($"  [FALLA] {mensaje}");
        _fallas++;
    }
}
