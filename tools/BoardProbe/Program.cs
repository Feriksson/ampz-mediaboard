// OJO: los ImplicitUsings de un proyecto WPF NO incluyen System.IO (a diferencia de una consola).
// Sin este using, Path y File no resuelven. Mismo gotcha que documenta el CLAUDE.md del repo.
using System.Collections.ObjectModel;
using System.IO;
using AmpzMediaBoard.Board;
using AmpzMediaBoard.Layout;
using AmpzMediaBoard.Media;
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
        VariasPestanasEnUnArchivo();
        ArchivoViejoEsUnaPestanaSinCambios();
        RenombrarAgregarYQuitarSonCambios();
        ReordenarPestanasEsUnCambioQueSeDeshace();
        FijarMudaElSectorAlPanel();
        DesfijarVuelveALaPestanaActiva();
        PanelSobreviveAlGuardado();
        V2SinPanelNoEsUnCambio();
        FijarYDesfijarSonCambios();
        SoloAlcanzaPestanaYPanel();
        PingPongTraduceElTiempoAlOriginal();
        PingPongZonaDemasiadoLargaSeRechaza();
        PingPongClaveDeCache();
        PingPongSeGuardaYViaja();

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
        var error = BoardStore.SaveTo(archivo, [new TabData("Board 1", raiz)], 0);
        Check("se pudo guardar", error is null);

        var leido = BoardStore.LoadFrom(archivo)?.Tabs[0].Root;
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
        Check("se pudo guardar con master 35", BoardStore.SaveTo(archivo, [new TabData("Board 1", raiz, 35)], 0) is null);

        var leido = BoardStore.LoadFrom(archivo);
        Check("el master vuelve en 35", leido?.Tabs[0].MasterVolume == 35);
        Check("el board releido coincide con el archivo", leido is not null && BoardStore.MatchesFile(archivo, leido.Tabs));

        // Sin paths adentro: un path de Windows tipeado en un JSON a mano es JSON invalido (ver
        // CLAUDE.md, test-missing). Un sector vacio alcanza para probar el campo que falta.
        var viejo = Path.Combine(Path.GetTempPath(), "probe-master-viejo.mboard");
        File.WriteAllText(viejo,
            """{"Type":"sector","Ratio":0.5,"LoopStart":0,"LoopEnd":0,"LoopEnabled":true,"Volume":60,"Muted":false}""");
        var leidoViejo = BoardStore.LoadFrom(viejo);
        Check("un archivo viejo (sin el campo) carga con master 100", leidoViejo?.Tabs[0].MasterVolume == 100);
        Check("y NO se lee como modificado", leidoViejo is not null && BoardStore.MatchesFile(viejo, leidoViejo.Tabs));
        Check("pero mover el master SI es un cambio", leidoViejo is not null &&
            !BoardStore.MatchesFile(viejo, [leidoViejo.Tabs[0] with { MasterVolume = 50 }]));

        File.Delete(archivo);
        File.Delete(viejo);
    }

    /// <summary>
    /// Un .mboard v2 guarda TODAS las pestanas. Tres pestanas distintas a proposito: nombres,
    /// master y formas de arbol diferentes, y la activa NO es la primera (el default) — si se
    /// perdiera, volveria en 0 y el check lo ve.
    /// </summary>
    private static void VariasPestanasEnUnArchivo()
    {
        Console.WriteLine();
        Console.WriteLine("=== 8. UN ARCHIVO CON VARIAS PESTANAS (v2, ida y vuelta) ===");

        var tabs = new List<TabData>
        {
            new("Referencias", new SplitNode(SplitOrientation.Horizontal,
                SectorCon(@"D:\clips\r1.mp4", 10, 900, 50, false), SectorCon(@"D:\clips\r2.mp4", 0, 400, 70, true), 0.3), 40),
            new("Tomas", SectorCon(@"D:\clips\t.mp4", 250, 1250, 90, false)),
            new("Board 3", new SplitNode(SplitOrientation.Vertical, new SectorNode(),
                new SplitNode(SplitOrientation.Horizontal, SectorCon(@"D:\clips\x.mp4", 5, 50, 20, false), new SectorNode(), 0.7), 0.6), 75),
        };

        var archivo = Path.Combine(Path.GetTempPath(), "probe-pestanas.mboard");
        Check("se pudo guardar", BoardStore.SaveTo(archivo, tabs, 2) is null);

        var leido = BoardStore.LoadFrom(archivo);
        Check("vuelven las TRES pestanas", leido?.Tabs.Count == 3);
        if (leido is { Tabs.Count: 3 })
        {
            Check("los nombres, en orden", leido.Tabs.Select(t => t.Name).SequenceEqual(["Referencias", "Tomas", "Board 3"]));
            Check("el master de cada una", leido.Tabs.Select(t => t.MasterVolume).SequenceEqual([40, 100, 75]));
            Check("la pestana activa (la tercera)", leido.ActiveTab == 2);
            Check("el layout de cada una (serializa igual pestana por pestana)",
                tabs.Zip(leido.Tabs).All(p => BoardStore.SerializeTab(p.First) == BoardStore.SerializeTab(p.Second)));
            Check("forma del arbol: ratio del split interno de la tercera",
                leido.Tabs[2].Root is SplitNode { Second: SplitNode { Ratio: var r } } && Math.Abs(r - 0.7) < 0.001);
            Check("y el documento releido coincide con el archivo", BoardStore.MatchesFile(archivo, leido.Tabs));
        }

        File.Delete(archivo);
    }

    /// <summary>
    /// Un .mboard VIEJO (un board pelado en la raiz) carga como UNA pestana con el nombre del
    /// archivo — y, lo que de verdad importa, NO se lee como modificado. Guardar lo reescribe en
    /// v2, asi que si la comparacion mirara el texto, todo archivo viejo preguntaria "guardar?"
    /// al cerrarlo sin haberlo tocado. Lleva MasterVolume en la raiz (donde lo guardaba el
    /// formato viejo) y va compacto: ejercita la mudanza del master y la normalizacion.
    /// </summary>
    private static void ArchivoViejoEsUnaPestanaSinCambios()
    {
        Console.WriteLine();
        Console.WriteLine("=== 9. ARCHIVO VIEJO = UNA PESTANA, SIN CAMBIOS ===");

        var viejo = Path.Combine(Path.GetTempPath(), "Mi board viejo.mboard");
        File.WriteAllText(viejo,
            """{"Type":"split","Orientation":"Vertical","Ratio":0.25,"First":{"Type":"sector","Volume":30},"Second":{"Type":"sector","Muted":true},"MasterVolume":45}""");

        var leido = BoardStore.LoadFrom(viejo);
        Check("carga", leido is not null);
        if (leido is null) return;

        Check("como UNA pestana", leido.Tabs.Count == 1);
        Check("con el nombre del archivo", leido.Tabs[0].Name == "Mi board viejo");
        Check("el master que estaba en la raiz se muda a la pestana", leido.Tabs[0].MasterVolume == 45);
        Check("el layout llego", leido.Tabs[0].Root is SplitNode { Ratio: 0.25 });
        Check("recien abierto NO se lee como modificado", BoardStore.MatchesFile(viejo, leido.Tabs));

        // Guardarlo lo pasa a v2: y sigue coincidiendo consigo mismo.
        Check("guardarlo (en v2) funciona", BoardStore.SaveTo(viejo, leido.Tabs, 0) is null);
        Check("el archivo ahora es v2", File.ReadAllText(viejo).Contains("\"Tabs\""));
        Check("y sigue sin leerse como modificado", BoardStore.MatchesFile(viejo, leido.Tabs));

        File.Delete(viejo);
    }

    /// <summary>
    /// Renombrar, agregar y quitar pestanas SON cambios sin guardar (no preguntan en el momento,
    /// asi que el unico aviso es el del archivo: si no los viera, se perderian en silencio).
    /// Cambiar de pestana, en cambio, NO lo es: mirar no es editar.
    /// </summary>
    private static void RenombrarAgregarYQuitarSonCambios()
    {
        Console.WriteLine();
        Console.WriteLine("=== 10. RENOMBRAR / AGREGAR / QUITAR PESTANAS SON CAMBIOS ===");

        var uno = new TabData("Uno", SectorCon(@"D:\clips\1.mp4", 0, 100, 50, false));
        var dos = new TabData("Dos", SectorCon(@"D:\clips\2.mp4", 0, 100, 50, false));
        var archivo = Path.Combine(Path.GetTempPath(), "probe-cambios.mboard");
        BoardStore.SaveTo(archivo, [uno, dos], 0);

        Check("sin tocar nada, coincide", BoardStore.MatchesFile(archivo, [uno, dos]));
        Check("renombrar una pestana es un cambio", !BoardStore.MatchesFile(archivo, [uno with { Name = "Uno bis" }, dos]));
        Check("agregar una pestana es un cambio",
            !BoardStore.MatchesFile(archivo, [uno, dos, new TabData(BoardTab.NextDefaultName(["Uno", "Dos"]), new SectorNode())]));
        Check("quitar una pestana es un cambio", !BoardStore.MatchesFile(archivo, [uno]));

        // Guardado con la SEGUNDA activa: la misma lista de pestanas tiene que coincidir.
        BoardStore.SaveTo(archivo, [uno, dos], 1);
        Check("cambiar de pestana activa NO es un cambio", BoardStore.MatchesFile(archivo, [uno, dos]));

        Check("nombre por defecto: el menor libre", BoardTab.NextDefaultName(["Board 1", "Board 3"]) == "Board 2");
        Check("nombre por defecto en un archivo vacio", BoardTab.NextDefaultName([]) == "Board 1");

        File.Delete(archivo);
    }

    /// <summary>
    /// Reordenar pestanas (arrastre o Ctrl+Shift+RePag/AvPag, ambos terminan en TabOrder.Move):
    ///  - el orden del array Tabs del archivo ES el orden de la tira;
    ///  - la activa es la MISMA pestana despues del movimiento (se guarda por IndexOf de la
    ///    referencia, no por el indice viejo);
    ///  - mover es un cambio sin guardar, y volver al orden original ya NO lo es;
    ///  - DropIndex (la regla del arrastre) no rebota entre una pestana ancha y una angosta.
    /// </summary>
    private static void ReordenarPestanasEsUnCambioQueSeDeshace()
    {
        Console.WriteLine();
        Console.WriteLine("=== 11. REORDENAR PESTANAS: ORDEN, ACTIVA Y CAMBIOS ===");

        var uno = new TabData("Uno", SectorCon(@"D:\clips\uno.mp4", 0, 100, 50, false));
        var dos = new TabData("Dos", SectorCon(@"D:\clips\dos.mp4", 0, 100, 60, false));
        var tres = new TabData("Tres", new SectorNode());
        var tabs = new ObservableCollection<TabData> { uno, dos, tres };
        var activa = uno;

        var archivo = Path.Combine(Path.GetTempPath(), "probe-orden.mboard");
        BoardStore.SaveTo(archivo, tabs, tabs.IndexOf(activa));
        Check("recien guardado, coincide", BoardStore.MatchesFile(archivo, tabs));

        Check("mover a su mismo lugar no hace nada", !TabOrder.Move(tabs, 1, 1) && tabs.SequenceEqual([uno, dos, tres]));
        Check("mover fuera de rango no hace nada", !TabOrder.Move(tabs, 5, 0) && tabs.SequenceEqual([uno, dos, tres]));

        Check("mover la primera al final", TabOrder.Move(tabs, 0, 2));
        Check("el orden nuevo es Dos, Tres, Uno", tabs.Select(t => t.Name).SequenceEqual(["Dos", "Tres", "Uno"]));
        Check("mover ES un cambio sin guardar", !BoardStore.MatchesFile(archivo, tabs));

        var movido = Path.Combine(Path.GetTempPath(), "probe-orden-movido.mboard");
        BoardStore.SaveTo(movido, tabs, tabs.IndexOf(activa));
        var leido = BoardStore.LoadFrom(movido);
        Check("el archivo guarda el orden nuevo",
            leido is not null && leido.Tabs.Select(t => t.Name).SequenceEqual(["Dos", "Tres", "Uno"]));
        Check("y ActiveTab sigue apuntando a la MISMA pestana (Uno)",
            leido is not null && leido.Tabs[leido.ActiveTab].Name == "Uno");

        Check("volver al orden original", TabOrder.Move(tabs, 2, 0) && tabs.SequenceEqual([uno, dos, tres]));
        Check("de vuelta en su lugar YA NO es un cambio", BoardStore.MatchesFile(archivo, tabs));

        // Una ancha (200) al lado de una angosta (50): cruza al pasar la MITAD de la vecina, y
        // despues del intercambio el mismo centro NO la devuelve.
        List<(double, double)> antes = [(0, 200), (200, 50), (250, 100)];
        Check("sin cruzar la mitad de la vecina, se queda", TabOrder.DropIndex(antes, 0, 220) == 0);
        Check("al cruzar la mitad de la vecina, pasa", TabOrder.DropIndex(antes, 0, 226) == 1);
        List<(double, double)> despues = [(0, 50), (50, 200), (250, 100)];
        Check("despues del intercambio, el mismo centro NO rebota", TabOrder.DropIndex(despues, 1, 226) == 1);

        // El rebote de verdad: una ANGOSTA arrastrada hacia una ancha. Con una frontera fija (el
        // borde de la vecina) pasa al tocarla y, ya intercambiada, el mismo centro la devuelve:
        // oscila a cada pixel. Se mira el par entero: donde cae, y que desde ahi no se mueva.
        List<(double, double)> angostaPrimero = [(0, 50), (50, 200)];
        List<(double, double)> anchaPrimero = [(0, 200), (200, 50)];
        var cae = TabOrder.DropIndex(angostaPrimero, 0, 80);
        Check("angosta junto a ancha: desde donde cae, no rebota",
            TabOrder.DropIndex(cae == 0 ? angostaPrimero : anchaPrimero, cae, 80) == cae);
        Check("un tiron largo cruza varias de una", TabOrder.DropIndex(antes, 0, 330) == 2);
        Check("hacia la izquierda tambien", TabOrder.DropIndex(antes, 2, 20) == 0);

        File.Delete(archivo);
        File.Delete(movido);
    }

    /// <summary>
    /// FIJAR: el sector sale de su pestana (el hermano ocupa el lugar, el ultimo se vacia) y el
    /// panel recibe el MISMO media con markers, volumen y silencio. Un sector vacio no se fija. El
    /// segundo fijado se apila debajo y la pila queda en alturas iguales.
    /// </summary>
    private static void FijarMudaElSectorAlPanel()
    {
        Console.WriteLine();
        Console.WriteLine("=== 12. FIJAR: EL SECTOR SE MUDA AL PANEL ===");

        var a = SectorCon(@"D:\clips\fijo.mp4", 120, 880, 37, true);
        var b = SectorCon(@"D:\clips\queda.mp4", 0, 500, 90, false);
        var tab = new BoardViewModel();
        tab.ReplaceRoot(new SplitNode(SplitOrientation.Horizontal, a, b));
        var dock = new PinnedDock();

        Check("el panel arranca sin contenido", !dock.HasContent && dock.ToData() is null);

        var fijado = dock.Pin(tab, a);
        Check("fijar devuelve el sector del panel", fijado is not null);
        Check("el HERMANO ocupa todo el board de la pestana", ReferenceEquals(tab.Root, b));
        Check("el sector fijado ya no esta en la pestana", !tab.AllSectors.Contains(a));
        Check("el panel tiene el media", dock.HasContent && fijado?.MissingPath == @"D:\clips\fijo.mp4");
        Check("con sus markers", fijado is { LoopStartMs: 120, LoopEndMs: 880 });
        Check("con su volumen y su silencio", fijado is { Volume: 37, IsMuted: true });
        Check("el panel ignora el master de la pestana (suena a master 100)", fijado?.BoardMasterVolume == 100);

        Check("un sector VACIO no se fija", dock.Pin(tab, new SectorNode()) is null);

        // El ultimo sector de una pestana: se VACIA en vez de desaparecer.
        var ultimo = dock.Pin(tab, b);
        Check("fijar el ultimo sector de la pestana lo vacia (la pestana sigue teniendo uno)",
            ultimo is not null && tab.Root is SectorNode { HasMedia: false, IsMissing: false });
        Check("el segundo fijado se APILA debajo", dock.Board.AllSectors.Select(s => s.MissingPath)
            .SequenceEqual([@"D:\clips\fijo.mp4", @"D:\clips\queda.mp4"]));
        Check("la pila es vertical y en alturas iguales",
            dock.Board.Root is SplitNode { Orientation: SplitOrientation.Vertical, Ratio: 0.5 });
    }

    /// <summary>
    /// DESFIJAR: el media vuelve a la pestana ACTIVA. Si el sector seleccionado esta vacio lo
    /// llena; si tiene algo, lo PARTE (nunca pisa). La seleccion "de la pestana" es la que tenia
    /// ANTES de que el click en el panel se la llevara (ClearSelection recuerda la ultima).
    /// </summary>
    private static void DesfijarVuelveALaPestanaActiva()
    {
        Console.WriteLine();
        Console.WriteLine("=== 13. DESFIJAR: VUELVE A LA PESTANA ACTIVA ===");

        var dock = new PinnedDock();
        var origen = new BoardViewModel();
        var x = SectorCon(@"D:\clips\x.mp4", 50, 450, 61, false);
        var y = SectorCon(@"D:\clips\y.mp4", 70, 770, 44, true);
        origen.ReplaceRoot(new SplitNode(SplitOrientation.Horizontal, x, new SplitNode(SplitOrientation.Vertical, y, new SectorNode())));
        var enPanelX = dock.Pin(origen, x)!;
        var enPanelY = dock.Pin(origen, y)!;

        // Destino con un sector VACIO seleccionado (y la seleccion "robada" por el panel).
        var vacio = new SectorNode();
        var ocupado = SectorCon(@"D:\clips\ocupado.mp4", 0, 100, 50, false);
        var activa = new BoardViewModel();
        activa.ReplaceRoot(new SplitNode(SplitOrientation.Horizontal, ocupado, vacio));
        activa.Select(vacio);
        activa.ClearSelection();   // lo que hace MainWindow al hacer click en el panel

        var vuelto = dock.Unpin(enPanelX, activa);
        Check("con el seleccionado VACIO, lo llena (no parte)", ReferenceEquals(vuelto, vacio));
        Check("el media volvio con markers, volumen y silencio",
            vacio is { MissingPath: @"D:\clips\x.mp4", LoopStartMs: 50, LoopEndMs: 450, Volume: 61, IsMuted: false });
        Check("salio del panel y quedo el otro", dock.Board.AllSectors.Count() == 1 && dock.HasContent);
        Check("la pestana no cambio de forma", activa.AllSectors.Count() == 2);

        // Ahora el seleccionado TIENE algo: se parte y el media va a la mitad nueva.
        activa.Select(ocupado);
        activa.ClearSelection();
        var partido = dock.Unpin(enPanelY, activa);
        Check("con el seleccionado OCUPADO, lo parte", activa.AllSectors.Count() == 3 && partido is not null && !ReferenceEquals(partido, ocupado));
        Check("el ocupado conserva su clip", ocupado.MissingPath == @"D:\clips\ocupado.mp4");
        Check("la mitad nueva tiene el media", partido?.MissingPath == @"D:\clips\y.mp4" && partido.IsMuted && partido.Volume == 44);
        Check("la mitad nueva queda seleccionada", ReferenceEquals(activa.Selected, partido));
        Check("vaciado el panel, no tiene contenido (se esconde)", !dock.HasContent && dock.ToData() is null);

        // Sin nada seleccionado y la raiz partida: se parte el board ENTERO.
        var otro = dock.Pin(activa, ocupado)!;
        var sinSeleccion = new BoardViewModel();
        var r1 = SectorCon(@"D:\clips\r1.mp4", 0, 100, 50, false);
        var r2 = SectorCon(@"D:\clips\r2.mp4", 0, 100, 50, false);
        var raizVieja = new SplitNode(SplitOrientation.Vertical, r1, r2);
        sinSeleccion.ReplaceRoot(raizVieja);
        sinSeleccion.ClearSelection();
        typeof(BoardViewModel).GetField("_lastSelected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(sinSeleccion, null);
        var alRaiz = dock.Unpin(otro, sinSeleccion);
        Check("sin seleccion: se parte la RAIZ (el board viejo queda entero a un lado)",
            sinSeleccion.Root is SplitNode { First: var f, Second: var s2 } && ReferenceEquals(f, raizVieja) && ReferenceEquals(s2, alRaiz));
    }

    /// <summary>
    /// El panel es del ARCHIVO: sus sectores (DTO completo) y su ancho como proporcion. Ida y
    /// vuelta con dos sectores distintos y un ancho que no es el de por defecto.
    /// </summary>
    private static void PanelSobreviveAlGuardado()
    {
        Console.WriteLine();
        Console.WriteLine("=== 14. EL PANEL SOBREVIVE AL .mboard ===");

        var dock = new PinnedDock();
        var tab = new BoardViewModel();
        tab.ReplaceRoot(new SplitNode(SplitOrientation.Horizontal,
            SectorCon(@"D:\clips\p1.mp4", 11, 222, 33, true),
            new SplitNode(SplitOrientation.Vertical, SectorCon(@"D:\clips\p2.mp4", 44, 555, 66, false), new SectorNode())));
        foreach (var s in tab.AllSectors.Where(s => s.IsMissing).ToList()) dock.Pin(tab, s);
        dock.Width = 0.3;

        var tabs = new List<TabData> { new("Board 1", tab.Root) };
        var archivo = Path.Combine(Path.GetTempPath(), "probe-panel.mboard");
        Check("se pudo guardar", BoardStore.SaveTo(archivo, tabs, 0, dock.ToData()) is null);
        Check("el archivo tiene el panel", File.ReadAllText(archivo).Contains("\"Dock\""));

        var leido = BoardStore.LoadFrom(archivo);
        Check("vuelve el panel", leido?.Dock is not null);
        if (leido?.Dock is { } d)
        {
            Check("con su ancho", Math.Abs(d.Width - 0.3) < 0.0001);
            Check("con sus DOS sectores, en orden", d.Sectors.Select(s => s.MissingPath)
                .SequenceEqual([@"D:\clips\p1.mp4", @"D:\clips\p2.mp4"]));
            Check("markers, volumen y silencio de cada uno",
                d.Sectors[0] is { LoopStartMs: 11, LoopEndMs: 222, Volume: 33, IsMuted: true } &&
                d.Sectors[1] is { LoopStartMs: 44, LoopEndMs: 555, Volume: 66, IsMuted: false });

            var otroPanel = new PinnedDock();
            otroPanel.Replace(d);
            Check("cargado en un panel, se apila igual", otroPanel.Board.AllSectors.Count() == 2 && otroPanel.Width == 0.3);
            Check("y el documento releido coincide con el archivo", BoardStore.MatchesFile(archivo, leido.Tabs, otroPanel.ToData()));
            Check("pero sin el panel NO coincide", !BoardStore.MatchesFile(archivo, leido.Tabs));
        }

        File.Delete(archivo);
    }

    /// <summary>
    /// Un v2 de ANTES del panel (sin el campo "Dock") carga con el panel vacio y NO se lee como
    /// modificado. Si el panel vacio se escribiera como { Sectors: [] } en vez de ausente, todo
    /// archivo viejo preguntaria "guardar?" al cerrarlo sin haberlo tocado.
    /// </summary>
    private static void V2SinPanelNoEsUnCambio()
    {
        Console.WriteLine();
        Console.WriteLine("=== 15. UN v2 SIN PANEL NO SE LEE COMO MODIFICADO ===");

        var archivo = Path.Combine(Path.GetTempPath(), "probe-sin-panel.mboard");
        File.WriteAllText(archivo,
            """{"Version":2,"ActiveTab":0,"Tabs":[{"Name":"Uno","Root":{"Type":"sector","Volume":80}}]}""");

        var leido = BoardStore.LoadFrom(archivo);
        Check("carga", leido is not null);
        Check("sin panel", leido?.Dock is null);

        var dock = new PinnedDock();
        dock.Replace(leido?.Dock);
        Check("el panel queda vacio", !dock.HasContent);
        Check("recien abierto NO se lee como modificado",
            leido is not null && BoardStore.MatchesFile(archivo, leido.Tabs, dock.ToData()));

        File.Delete(archivo);
    }

    /// <summary>
    /// Fijar, desfijar y mover el divisor del panel son cambios sin guardar: no preguntan en el
    /// momento, asi que el aviso del ARCHIVO es la unica red — si no los viera, se perderian.
    /// </summary>
    private static void FijarYDesfijarSonCambios()
    {
        Console.WriteLine();
        Console.WriteLine("=== 16. FIJAR / DESFIJAR / ANCHO DEL PANEL SON CAMBIOS ===");

        var tab = new BoardViewModel();
        var a = SectorCon(@"D:\clips\c1.mp4", 0, 100, 50, false);
        var b = SectorCon(@"D:\clips\c2.mp4", 0, 100, 50, false);
        tab.ReplaceRoot(new SplitNode(SplitOrientation.Horizontal, a, b));
        var dock = new PinnedDock();
        List<TabData> Tabs() => [new TabData("Uno", tab.Root)];

        var archivo = Path.Combine(Path.GetTempPath(), "probe-panel-cambios.mboard");
        BoardStore.SaveTo(archivo, Tabs(), 0, dock.ToData());
        Check("recien guardado, coincide", BoardStore.MatchesFile(archivo, Tabs(), dock.ToData()));

        var fijado = dock.Pin(tab, a)!;
        Check("fijar ES un cambio", !BoardStore.MatchesFile(archivo, Tabs(), dock.ToData()));

        BoardStore.SaveTo(archivo, Tabs(), 0, dock.ToData());
        Check("guardado con el panel, coincide", BoardStore.MatchesFile(archivo, Tabs(), dock.ToData()));

        dock.Width = 0.4;
        Check("mover el divisor del panel ES un cambio", !BoardStore.MatchesFile(archivo, Tabs(), dock.ToData()));
        dock.Width = PinnedDock.DefaultWidth;
        Check("volver al mismo ancho ya no lo es", BoardStore.MatchesFile(archivo, Tabs(), dock.ToData()));

        dock.Unpin(fijado, tab);
        Check("desfijar ES un cambio", !BoardStore.MatchesFile(archivo, Tabs(), dock.ToData()));

        File.Delete(archivo);
    }

    /// <summary>
    /// SOLO con el panel: silencia la pestana ACTIVA y el panel, y deja sonando solo el pedido,
    /// viva donde viva. Una pestana de fondo NO se toca (esta pausada y es otro trabajo).
    /// </summary>
    private static void SoloAlcanzaPestanaYPanel()
    {
        Console.WriteLine();
        Console.WriteLine("=== 17. SOLO: PESTANA ACTIVA + PANEL ===");

        var t1 = SectorCon(@"D:\clips\t1.mp4", 0, 100, 50, false);
        var t2 = SectorCon(@"D:\clips\t2.mp4", 0, 100, 50, false);
        var fijar = SectorCon(@"D:\clips\panel.mp4", 0, 100, 50, true);
        var activa = new BoardViewModel();
        activa.ReplaceRoot(new SplitNode(SplitOrientation.Horizontal, t1, new SplitNode(SplitOrientation.Vertical, t2, fijar)));
        var fondo = SectorCon(@"D:\clips\fondo.mp4", 0, 100, 50, false);
        var deFondo = new BoardViewModel();
        deFondo.ReplaceRoot(fondo);

        var dock = new PinnedDock();
        var enPanel = dock.Pin(activa, fijar)!;

        dock.Solo(enPanel, activa);
        Check("solo desde el panel: el del panel suena (estaba silenciado)", !enPanel.IsMuted);
        Check("y la pestana activa queda silenciada", t1.IsMuted && t2.IsMuted);
        Check("la pestana de fondo NO se toca", !fondo.IsMuted);

        dock.Solo(t2, activa);
        Check("solo desde la pestana: el panel queda silenciado", enPanel.IsMuted);
        Check("y suena solo el pedido", !t2.IsMuted && t1.IsMuted);

        // Por el camino de la vista: RequestSolo con el alcance enganchado como en MainWindow.
        activa.SoloRequested += s => dock.Solo(s, activa);
        dock.Board.SoloRequested += s => dock.Solo(s, activa);
        dock.Board.RequestSolo(enPanel);
        Check("RequestSolo desde el panel usa el alcance pestana + panel", !enPanel.IsMuted && t1.IsMuted && t2.IsMuted);
    }

    private static void PingPongTraduceElTiempoAlOriginal()
    {
        Console.WriteLine();
        Console.WriteLine("=== 18. PING-PONG: TIEMPO DEL GENERADO -> TIEMPO ORIGINAL ===");

        // Zona A=2000, B=5000 (d=3000). Generado: ida [0,3000), vuelta [3000,6000), colchon despues.
        const double a = 2000, b = 5000;
        Check("t=0 es el marker A", PingPongMath.ToOriginal(0, a, b) == 2000);
        Check("ida: t=1000 -> 3000", PingPongMath.ToOriginal(1000, a, b) == 3000);
        Check("el giro: t=d es el marker B", PingPongMath.ToOriginal(3000, a, b) == 5000);
        Check("vuelta: t=4000 -> 4000 (B - (t - d))", PingPongMath.ToOriginal(4000, a, b) == 4000);
        Check("vuelta: t=5500 -> 2500", PingPongMath.ToOriginal(5500, a, b) == 2500);
        Check("fin de la vuelta: t=2d vuelve a A", PingPongMath.ToOriginal(6000, a, b) == 2000);
        Check("colchon: t=2d+300 -> A+300 (continua la ida)", PingPongMath.ToOriginal(6300, a, b) == 2300);
        Check("nunca sale de la zona", PingPongMath.ToOriginal(99999, a, b) <= b && PingPongMath.ToOriginal(-5, a, b) == a);
        Check("el loop del generado vuelve a 0 en 2d", PingPongMath.LoopEndMs(a, b) == 6000);

        // Simetria: el mismo cuadro original aparece a t (ida) y a 2d - t (vuelta).
        var simetrico = true;
        for (var t = 0.0; t <= 3000; t += 250)
            simetrico &= Math.Abs(PingPongMath.ToOriginal(t, a, b) - PingPongMath.ToOriginal(6000 - t, a, b)) < 0.001;
        Check("ida y vuelta son espejo (t y 2d - t son el mismo cuadro)", simetrico);

        Check("click en la timeline: original 3500 -> ida t=1500", PingPongMath.FromOriginal(3500, a, b) == 1500);
        Check("click fuera de la zona se acota a la zona", PingPongMath.FromOriginal(100, a, b) == 0 && PingPongMath.FromOriginal(9000, a, b) == 3000);
        Check("FromOriginal(ToOriginal(t)) == t sobre la ida",
            PingPongMath.FromOriginal(PingPongMath.ToOriginal(1234, a, b), a, b) == 1234);
    }

    private static void PingPongZonaDemasiadoLargaSeRechaza()
    {
        Console.WriteLine();
        Console.WriteLine("=== 19. PING-PONG: ZONA > 30 s SE RECHAZA, ZONA EFECTIVA = LA DEL LOOP ===");

        Check("30 s justos se aceptan", PingPongMath.Check(1000, 31000) == PingPongMath.ZoneCheck.Ok);
        Check("30,001 s se rechazan", PingPongMath.Check(1000, 31001) == PingPongMath.ZoneCheck.TooLong);
        Check("una zona de 5 s es valida", PingPongMath.Check(0, 5000) == PingPongMath.ZoneCheck.Ok);
        Check("una zona de 50 ms es demasiado corta", PingPongMath.Check(0, 50) == PingPongMath.ZoneCheck.TooShort);
        Check("NaN no pasa como valida", PingPongMath.Check(double.NaN, 5000) != PingPongMath.ZoneCheck.Ok);

        // La zona efectiva es la MISMA regla que usa EnforceLoop: LoopEnd sin inicializar = clip entero.
        Check("LoopEnd en 0 = hasta el final del clip", PingPongMath.EffectiveZone(0, 0, 60000) == (0, 60000));
        Check("clip de 60 s con la zona por defecto se rechaza",
            PingPongMath.Check(0, PingPongMath.EffectiveZone(0, 0, 60000).End) == PingPongMath.ZoneCheck.TooLong);
        Check("B fuera del clip se acota a la duracion", PingPongMath.EffectiveZone(1000, 90000, 20000) == (1000, 20000));
        Check("el colchon nunca es mas largo que la zona", PingPongMath.PadFor(150) == 150 && PingPongMath.PadFor(5000) == PingPongMath.PadMs);
    }

    private static void PingPongClaveDeCache()
    {
        Console.WriteLine();
        Console.WriteLine("=== 20. PING-PONG: CLAVE DEL CACHE Y ARGUMENTOS DE FFMPEG ===");

        var fecha = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var k = PingPongMath.CacheKey(@"D:\clips\x.mp4", fecha, 2000, 5000);

        Check("misma fuente, fecha y zona = misma clave", k == PingPongMath.CacheKey(@"D:\clips\x.mp4", fecha, 2000, 5000));
        Check("la ruta no distingue mayusculas (Windows)", k == PingPongMath.CacheKey(@"d:\CLIPS\X.MP4", fecha, 2000, 5000));
        Check("ruido de punto flotante en un marker NO regenera", k == PingPongMath.CacheKey(@"D:\clips\x.mp4", fecha, 2000.2, 4999.8));
        Check("mover A 1 ms SI cambia la clave", k != PingPongMath.CacheKey(@"D:\clips\x.mp4", fecha, 2001, 5000));
        Check("mover B SI cambia la clave", k != PingPongMath.CacheKey(@"D:\clips\x.mp4", fecha, 2000, 5100));
        Check("re-exportar el clip (otra fecha) SI cambia la clave", k != PingPongMath.CacheKey(@"D:\clips\x.mp4", fecha.AddSeconds(1), 2000, 5000));
        Check("otro archivo, otra clave", k != PingPongMath.CacheKey(@"D:\clips\y.mp4", fecha, 2000, 5000));
        Check("la clave es un nombre de archivo seguro", k.Length == 32 && k.All(Uri.IsHexDigit));

        // Los segundos de ffmpeg van con PUNTO aunque Windows este en espanol (coma decimal).
        var antes = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-AR");
        var args = PingPongMath.BuildArguments(@"D:\x.mp4", 12400, 15650, @"C:\t\o.mkv").ToList();
        System.Globalization.CultureInfo.CurrentCulture = antes;
        var ss = args[args.IndexOf("-ss") + 1];
        var t = args[args.IndexOf("-t") + 1];
        Check($"-ss con punto decimal ({ss})", ss == "12.400");
        Check($"-t es la duracion de la zona ({t})", t == "3.250");
        Check("-ss/-t van ANTES de -i (corte en la entrada)", args.IndexOf("-ss") < args.IndexOf("-i") && args.IndexOf("-t") < args.IndexOf("-i"));
        Check("sin audio (-an)", args.Contains("-an"));
        Check("codec intra-only (mjpeg)", args.Contains("mjpeg"));
        Check("el filtro invierte la zona", args.Any(x => x.Contains("reverse")));
    }

    private static void PingPongSeGuardaYViaja()
    {
        Console.WriteLine();
        Console.WriteLine("=== 21. PING-PONG: SE GUARDA SOLO SI ESTA PRENDIDO, Y VIAJA CON EL CLIP ===");

        var con = SectorCon(@"D:\clips\pp.mp4", 1000, 4000, 100, false);
        con.PingPong = true;
        var sin = SectorCon(@"D:\clips\normal.mp4", 0, 2000, 100, false);
        LayoutNode raiz = new SplitNode(SplitOrientation.Horizontal, con, sin);

        var texto = BoardStore.Serialize([new TabData("Board 1", raiz)]);
        Check("el prendido se escribe (\"PingPong\": true)", texto.Contains("\"PingPong\": true"));
        Check("el apagado NO se escribe (archivos viejos comparan igual)", !texto.Contains("\"PingPong\": false"));

        var archivo = Path.Combine(Path.GetTempPath(), "probe-pingpong.mboard");
        BoardStore.SaveTo(archivo, [new TabData("Board 1", raiz)], 0);
        var leido = BoardStore.LoadFrom(archivo);
        if (leido?.Tabs[0].Root is SplitNode { First: SectorNode a, Second: SectorNode b })
        {
            Check("vuelve prendido (aunque el archivo este AUSENTE: no se pierde al guardar)", a.PingPong);
            Check("el otro vuelve apagado", !b.PingPong);
            Check("releido sin tocar NO es un cambio", BoardStore.MatchesFile(archivo, leido.Tabs));
        }
        else Check("la estructura leida es un split con dos sectores", false);

        // Un archivo VIEJO (sin el campo) y uno con "PingPong": false escrito a mano son el mismo board.
        var viejo = Path.Combine(Path.GetTempPath(), "probe-pingpong-viejo.mboard");
        File.WriteAllText(viejo, """{ "Version": 2, "Tabs": [ { "Name": "B", "Root": { "Type": "sector", "Path": "D:\\clips\\v.mp4", "LoopStart": 0, "LoopEnd": 900, "PingPong": false } } ] }""");
        var leidoViejo = BoardStore.LoadFrom(viejo);
        Check("\"PingPong\": false carga apagado", leidoViejo?.Tabs[0].Root is SectorNode { PingPong: false });
        Check("y NO se lee como modificado", leidoViejo is not null && BoardStore.MatchesFile(viejo, leidoViejo.Tabs));

        // Intercambiar y fijar viajan por MediaSnapshot: el toggle va con su clip.
        var vm = new BoardViewModel();
        vm.ReplaceRoot(raiz);
        vm.SwapMedia(con, sin);
        Check("intercambiar: el destino recibe el ping-pong", sin.PingPong && sin.MissingPath == @"D:\clips\pp.mp4");
        Check("intercambiar: el origen recibe el apagado", !con.PingPong);

        var dock = new PinnedDock();
        var fijado = dock.Pin(vm, sin)!;
        Check("fijar: el ping-pong viaja al panel", fijado.PingPong);

        // Prenderlo prende el LOOP; apagar el LOOP lo apaga (es un modo del loop, no otra cosa).
        var s = SectorCon(@"D:\clips\l.mp4", 0, 1000, 100, false);
        s.LoopEnabled = false;
        s.PingPong = true;
        Check("prender ping-pong prende el LOOP", s.LoopEnabled);
        s.LoopEnabled = false;
        Check("apagar el LOOP apaga el ping-pong", !s.PingPong);

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
