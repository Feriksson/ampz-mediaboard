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
}
