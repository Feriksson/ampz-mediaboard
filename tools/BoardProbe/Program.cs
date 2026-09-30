// OJO: los ImplicitUsings de un proyecto WPF NO incluyen System.IO (a diferencia de una consola).
// Sin este using, Path y File no resuelven. Mismo gotcha que documenta el CLAUDE.md del repo.
using System.IO;
using AmpzMediaBoard.Board;
using AmpzMediaBoard.Layout;
using AmpzMediaBoard.Persistence;

// Pruebas de MODELO. Deliberadamente NO tocan VLC: los sectores se marcan como "archivo
// ausente", que ejercita exactamente el mismo camino de snapshot/restore y persistencia sin
// abrir un solo decodificador. Asi la prueba es deterministica y corre en cualquier maquina.

internal static class Program
{
    private static int _fallos;

    [STAThread]
    private static int Main()
    {
        SwapIntercambiaContenidos();
        SwapSobreSectorVacioEsUnMovimiento();
        VolumenYSilencioSobrevivenElGuardado();
        DistribuirDejaTodosLosSectoresIguales();
        SoloSilenciaATodosMenosUno();
        MasterEscalaSinPisar();
        MasterSobreviveAlGuardadoYLosViejosNoCambian();

        Console.WriteLine();
        Console.WriteLine(_fallos == 0 ? "=== TODO OK ===" : $"=== {_fallos} FALLO(S) ===");
        return _fallos == 0 ? 0 : 1;
    }

    private static void Check(string que, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "OK " : "MAL")}] {que}");
        if (!ok) _fallos++;
    }

    private static SectorNode SectorCon(string path, double a, double b, int volumen, bool mute)
    {
        var s = new SectorNode();
        s.MarkMissing(path);           // no toca VLC, pero llena MediaPath/markers como un clip real
        s.LoopStartMs = a;
        s.LoopEndMs = b;
        s.Volume = volumen;
        s.IsMuted = mute;
        return s;
    }

    private static void SwapIntercambiaContenidos()
    {
        Console.WriteLine("=== 1. ARRASTRAR SOBRE UN SECTOR OCUPADO INTERCAMBIA ===");

        var a = SectorCon(@"D:\clips\A.mp4", 100, 900, 42, true);
        var b = SectorCon(@"D:\clips\B.mp4", 300, 700, 88, false);

        var vm = new BoardViewModel();
        vm.ReplaceRoot(new SplitNode(SplitOrientation.Horizontal, a, b));
        vm.SwapMedia(a, b);

        Check("el destino recibio el clip del origen", b.MissingPath == @"D:\clips\A.mp4");
        Check("el origen recibio el clip del destino", a.MissingPath == @"D:\clips\B.mp4");
        Check("los markers viajaron con su clip", b.LoopStartMs == 100 && b.LoopEndMs == 900);
        Check("el volumen viajo con su clip", b.Volume == 42 && b.IsMuted);
        Check("el volumen del otro tambien", a.Volume == 88 && !a.IsMuted);
        Check("NADA se perdio (ningun sector quedo vacio)", a.IsMissing && b.IsMissing);
    }

    private static void SwapSobreSectorVacioEsUnMovimiento()
    {
        Console.WriteLine();
        Console.WriteLine("=== 2. ARRASTRAR SOBRE UN SECTOR VACIO ES UN MOVIMIENTO ===");

        var origen = SectorCon(@"D:\clips\solo.mp4", 50, 500, 70, false);
        var vacio = new SectorNode();

        var vm = new BoardViewModel();
        vm.ReplaceRoot(new SplitNode(SplitOrientation.Vertical, origen, vacio));
        vm.SwapMedia(origen, vacio);

        Check("el clip quedo en el destino", vacio.MissingPath == @"D:\clips\solo.mp4");
        Check("los markers llegaron", vacio.LoopStartMs == 50 && vacio.LoopEndMs == 500);
        Check("el volumen llego", vacio.Volume == 70);
        Check("el origen quedo VACIO", !origen.IsMissing && !origen.HasMedia);
    }

    private static void VolumenYSilencioSobrevivenElGuardado()
    {
        Console.WriteLine();
        Console.WriteLine("=== 3. VOLUMEN Y SILENCIO SOBREVIVEN AL .mboard ===");

        var izq = SectorCon(@"D:\clips\izq.mp4", 10, 200, 33, true);
        var der = SectorCon(@"D:\clips\der.mp4", 20, 400, 77, false);
        LayoutNode raiz = new SplitNode(SplitOrientation.Horizontal, izq, der, 0.4);

        var archivo = Path.Combine(Path.GetTempPath(), "probe-volumen.mboard");
        var error = BoardStore.SaveTo(archivo, raiz);
        Check("se pudo guardar", error is null);

        var leido = BoardStore.LoadFrom(archivo);
        Check("se pudo leer", leido is not null);

        if (leido is SplitNode split &&
            split.First is SectorNode a && split.Second is SectorNode b)
        {
            Check("volumen del primer sector", a.Volume == 33);
            Check("silencio del primer sector", a.IsMuted);
            Check("volumen del segundo sector", b.Volume == 77);
            Check("el segundo NO quedo silenciado", !b.IsMuted);
            Check("los markers siguen ahi", a.LoopStartMs == 10 && b.LoopEndMs == 400);
            Check("el ratio del split sobrevivio", Math.Abs(split.Ratio - 0.4) < 0.001);
        }
        else
        {
            Check("la estructura leida es un split con dos sectores", false);
        }

        File.Delete(archivo);
    }

    /// <summary>
    /// El caso que hace de esta prueba algo mas que decoracion: el arbol es ASIMETRICO.
    ///
    /// Con A | (B / C), la solucion ingenua —poner todos los splits en 0.5— da A=50%, B=25% y
    /// C=25%, y "se ve casi bien" en un board de dos celdas, que es justo donde uno lo probaria a
    /// ojo. Recien con tres sectores desbalanceados el error salta. Verificado AL REVES: cambiando
    /// el Ratio de Distribute a 0.5 fijo, esta prueba FALLA en el primer Check.
    /// </summary>
    private static void DistribuirDejaTodosLosSectoresIguales()
    {
        Console.WriteLine();
        Console.WriteLine("=== 4. DISTRIBUIR REPARTE EN PARTES IGUALES (ARBOL ASIMETRICO) ===");

        var a = new SectorNode();
        var b = new SectorNode();
        var c = new SectorNode();

        // A | (B / C) con ratios torcidos a proposito: distribuir tiene que enderezarlos.
        var derecha = new SplitNode(SplitOrientation.Vertical, b, c, 0.8);
        LayoutNode raiz = new SplitNode(SplitOrientation.Horizontal, a, derecha, 0.15);

        var vm = new BoardViewModel();
        vm.ReplaceRoot(raiz);
        vm.Distribute();

        // El area de una hoja es el PRODUCTO de los ratios desde la raiz. Con tres sectores,
        // cada uno tiene que valer 1/3 exacto.
        foreach (var (nombre, sector) in new[] { ("A", a), ("B", b), ("C", c) })
            Check($"el sector {nombre} ocupa un tercio del board", Math.Abs(Area(sector) - 1.0 / 3) < 0.0001);

        // Y la suma cierra en 1: si diera menos, habria espacio muerto; si diera mas, se pisan.
        Check("las tres areas suman el board entero", Math.Abs(Area(a) + Area(b) + Area(c) - 1) < 0.0001);
    }

    /// <summary>
    /// Shift+click en el silencio = SOLO. Mezcla a proposito sectores que NO suenan (una imagen
    /// ausente y un sector vacio): el solo no les puede tocar el mute. Y el objetivo arranca
    /// SILENCIADO, que es el caso que importa: pedir "solo este" tiene que des-silenciarlo.
    /// </summary>
    private static void SoloSilenciaATodosMenosUno()
    {
        Console.WriteLine();
        Console.WriteLine("=== 5. SOLO: SILENCIA A LOS DEMAS Y DEJA SONANDO ESTE ===");

        var a = SectorCon(@"D:\clips\a.mp4", 0, 100, 40, false);
        var b = SectorCon(@"D:\clips\b.mp4", 0, 100, 65, true);   // el objetivo, silenciado
        var c = SectorCon(@"D:\clips\c.mp4", 0, 100, 90, false);
        var foto = SectorCon(@"D:\clips\foto.png", 0, 0, 100, false);
        var vacio = new SectorNode();

        var vm = new BoardViewModel();
        vm.ReplaceRoot(new SplitNode(SplitOrientation.Horizontal,
            new SplitNode(SplitOrientation.Vertical, a, b),
            new SplitNode(SplitOrientation.Vertical, c, new SplitNode(SplitOrientation.Horizontal, foto, vacio))));
        vm.Solo(b);

        Check("el objetivo quedo SIN silencio", !b.IsMuted);
        Check("los otros videos quedaron silenciados", a.IsMuted && c.IsMuted);
        Check("los volumenes NO se tocaron", a.Volume == 40 && b.Volume == 65 && c.Volume == 90);
        Check("la imagen y el vacio no se tocaron", !foto.IsMuted && !vacio.IsMuted);
    }

    /// <summary>
    /// Volumen general: ESCALA, nunca pisa. Se prueba la cuenta pura (lo que recibe VLC) y que el
    /// board le EMPUJE el master a todos sus sectores — incluido uno nacido de partir despues.
    /// </summary>
    private static void MasterEscalaSinPisar()
    {
        Console.WriteLine();
        Console.WriteLine("=== 6. VOLUMEN GENERAL: ESCALA LA MEZCLA SIN PISARLA ===");

        Check("80 al 50% = 40", SectorNode.EffectiveVolume(80, 50) == 40);
        Check("20 al 50% = 10 (la mezcla 4:1 se conserva)", SectorNode.EffectiveVolume(20, 50) == 10);
        Check("master 100 no cambia nada", SectorNode.EffectiveVolume(73, 100) == 73);
        Check("master 0 silencia", SectorNode.EffectiveVolume(73, 0) == 0);
        Check("redondea (33 al 50% = 17)", SectorNode.EffectiveVolume(33, 50) == 17);
        Check("fuera de rango se sanea", SectorNode.EffectiveVolume(150, 200) == 100);

        var x = SectorCon(@"D:\clips\x.mp4", 0, 100, 80, false);
        var y = SectorCon(@"D:\clips\y.mp4", 0, 100, 20, false);
        var vm = new BoardViewModel();
        vm.ReplaceRoot(new SplitNode(SplitOrientation.Horizontal, x, y));
        vm.MasterVolume = 30;
        vm.Split(y, SplitOrientation.Vertical);
        var nuevo = vm.Selected!;

        Check("todos los sectores recibieron el master", x.BoardMasterVolume == 30 && y.BoardMasterVolume == 30);
        Check("el sector nacido de partir tambien", nuevo.BoardMasterVolume == 30);
        Check("el volumen PROPIO de cada sector quedo intacto", x.Volume == 80 && y.Volume == 20);
    }

    /// <summary>
    /// El master se persiste en el .mboard, y un archivo VIEJO (sin el campo) carga con 100 y NO
    /// se lee como modificado — si no, abrir y cerrar un board de antes preguntaria "guardar?".
    /// El archivo viejo se escribe COMPACTO a proposito: ejercita tambien la normalizacion.
    /// </summary>
    private static void MasterSobreviveAlGuardadoYLosViejosNoCambian()
    {
        Console.WriteLine();
        Console.WriteLine("=== 7. VOLUMEN GENERAL EN EL .mboard (Y ARCHIVOS VIEJOS) ===");

        var archivo = Path.Combine(Path.GetTempPath(), "probe-master.mboard");
        LayoutNode raiz = SectorCon(@"D:\clips\m.mp4", 10, 200, 60, false);
        Check("se pudo guardar con master 35", BoardStore.SaveTo(archivo, raiz, 35) is null);

        var leido = BoardStore.LoadFrom(archivo, out var master);
        Check("el master vuelve en 35", master == 35);
        Check("el board releido coincide con el archivo", leido is not null && BoardStore.MatchesFile(archivo, leido, master));

        // Sin paths adentro: un path de Windows tipeado en un JSON a mano es JSON invalido (ver
        // CLAUDE.md, test-missing). Un sector vacio alcanza para probar el campo que falta.
        var viejo = Path.Combine(Path.GetTempPath(), "probe-master-viejo.mboard");
        File.WriteAllText(viejo,
            """{"Type":"sector","Ratio":0.5,"LoopStart":0,"LoopEnd":0,"LoopEnabled":true,"Volume":60,"Muted":false}""");
        var leidoViejo = BoardStore.LoadFrom(viejo, out var masterViejo);
        Check("un archivo viejo (sin el campo) carga con master 100", masterViejo == 100);
        Check("y NO se lee como modificado", leidoViejo is not null && BoardStore.MatchesFile(viejo, leidoViejo, masterViejo));
        Check("pero mover el master SI es un cambio", leidoViejo is not null && !BoardStore.MatchesFile(viejo, leidoViejo, 50));

        File.Delete(archivo);
        File.Delete(viejo);
    }

    /// <summary>Fraccion del board que ocupa una hoja: el producto de los ratios hasta la raiz.</summary>
    private static double Area(LayoutNode hoja)
    {
        var area = 1.0;
        var nodo = hoja;
        while (nodo.Parent is { } padre)
        {
            area *= ReferenceEquals(padre.First, nodo) ? padre.Ratio : 1 - padre.Ratio;
            nodo = padre;
        }
        return area;
    }
}
