// ⚠ System.IO explícito: los ImplicitUsings de WPF NO lo incluyen (a diferencia de una consola).
// Es la misma razón por la que el .csproj de la app lo declara a mano.
using System.IO;
using AmpzMediaBoard.Layout;
using LibVLCSharp.Shared;

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

        // Caso 3: el intervalo NEGRO entre repeticiones. Ver TailGuardMs en SectorNode.
        LoopeaSinReabrirElArchivo(clip);

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

    /// <summary>
    /// El clip con la ZONA POR DEFECTO tiene que dar la vuelta SIN reabrir el archivo.
    ///
    /// Reportado como "entre repetición y repetición aparece un intervalo negro". La causa no
    /// era la config de VLC: con B pegado al final, el playhead nunca alcanzaba a cruzarlo
    /// porque VLC cortaba el stream primero → el loop caía siempre en RestartFrom, que hace
    /// Stop() + Media nuevo + Play(), o sea reabrir el archivo entero. Eso son cientos de ms de
    /// pantalla negra, en el caso MÁS COMÚN de la app.
    ///
    /// ⚠ EL DISCRIMINADOR ES EL ESTADO DE VLC, no la posición, y esto se aprendió escribiendo
    /// esta misma prueba. Mirar "hasta dónde llegó el playhead" NO sirve para detectar el bug:
    /// cuando el clip termina, Tick() atiende el Ended y RestartFrom pone PositionMs en el
    /// marker A dentro de la MISMA vuelta, así que el pico nunca se llega a muestrear. Contra el
    /// código roto, "dio la vuelta antes del final" daba OK igual. Otro verde que no se podía
    /// poner en rojo.
    ///
    /// Lo que sí distingue es preguntarle a VLC si pasó por Ended/Stopped, y hay que mirarlo
    /// ANTES de cada Tick: Tick() es justamente quien lo atiende (relanza y el player vuelve a
    /// Playing en el acto), así que después ya llegaste tarde.
    ///
    /// El margen se verifica igual, con un piso de 100ms, y ahí sí es informativo: cubre a
    /// TailGuardMs. Sin ese margen el loop corta ~49ms antes del final — funciona en una máquina
    /// ociosa, pero es un tick y medio de aire, y con seis clips decodificando la cola del
    /// Dispatcher se atrasa más que eso y el clip termina igual.
    ///
    /// Verificado AL REVÉS, las dos mitades:
    ///   - anulando la extrapolación de Tick() → falla 2 de cada 3 corridas (la lotería original);
    ///   - con TailGuardMs = 0 → el margen cae a ~49ms y falla el piso de 100ms.
    /// </summary>
    private static void LoopeaSinReabrirElArchivo(string clip)
    {
        Console.WriteLine("=== 3. LA ZONA POR DEFECTO LOOPEA SIN REABRIR EL ARCHIVO (sin intervalo negro) ===");

        using var nodo = new SectorNode();

        // Un clip recién soltado: desde 0 y sin tocar un solo marker. La zona por defecto la fija
        // el propio Tick en cuanto VLC reporta la duración.
        nodo.Load(clip, 0);
        nodo.LoopEnabled = true;
        nodo.StartPending();

        Bombear(nodo, hasta: () => nodo.DurationMs > 0, limiteMs: 5000);
        if (nodo.DurationMs <= 0)
        {
            Fallo("VLC nunca reportó la duración del clip");
            return;
        }
        Console.WriteLine($"  duración: {nodo.DurationMs:0} ms   (zona por defecto: 0 → {nodo.LoopEndMs:0} ms)");

        var maxima = nodo.PositionMs;
        var anterior = nodo.PositionMs;
        var tocoEnded = false;
        var dioLaVuelta = false;

        var arranque = Environment.TickCount64;
        while (Environment.TickCount64 - arranque < 15000 && !dioLaVuelta)
        {
            if (nodo.Player is { } p && p.State is VLCState.Ended or VLCState.Stopped) tocoEnded = true;

            nodo.Tick();

            var pos = nodo.PositionMs;
            maxima = Math.Max(maxima, pos);
            if (pos < anterior - 500) dioLaVuelta = true;
            anterior = pos;

            Thread.Sleep(33);
        }

        if (!dioLaVuelta)
        {
            Fallo("el clip NUNCA volvió a empezar: el loop no disparó");
            return;
        }

        var sobro = nodo.DurationMs - maxima;
        Console.WriteLine($"  posición máxima alcanzada: {maxima:0} ms   (dio la vuelta {sobro:0} ms antes del final)");

        // El piso NO es "algo mayor que cero": es el colchón que tiene que quedar para que el
        // corte siga cayendo del lado bueno cuando la máquina está cargada. Ver TailGuardMs.
        if (sobro > 100)
            Console.WriteLine("  [OK ] dio la vuelta con margen de sobra antes del final");
        else
            Fallo($"cortó a solo {sobro:0} ms del final: sin colchón, bajo carga el clip va a terminar igual y volver al reinicio caro");

        if (!tocoEnded)
            Console.WriteLine("  [OK ] VLC nunca pasó por Ended/Stopped: el input quedó abierto todo el tiempo");
        else
            Fallo("VLC pasó por Ended/Stopped: el archivo se reabrió y la imagen se fue a negro");

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
