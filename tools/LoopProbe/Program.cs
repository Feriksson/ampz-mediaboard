using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using AmpzMediaBoard.Controls;

// Verifica sobre la clase REAL LoopTimeline que el click en el riel ya NO mata el binding
// del playhead.
//
// El flujo replica el de la app:
//   fuente (SectorNode.PositionMs)  --binding OneWay-->  LoopTimeline.PositionMs
//   click en el riel  -->  SeekRequested  -->  el dueño hace el seek y actualiza LA FUENTE
//
// El bug original era que OnTrackClick ademas le asignaba un valor LOCAL a la DP, lo que
// destruye la BindingExpression de un binding OneWay y congela la marca para siempre.

internal sealed class Fuente : INotifyPropertyChanged
{
    private double _pos;
    public double Pos
    {
        get => _pos;
        set { _pos = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pos))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var fuente = new Fuente();
        var timeline = new LoopTimeline { DurationMs = 10000 };

        BindingOperations.SetBinding(timeline, LoopTimeline.PositionMsProperty,
            new Binding(nameof(Fuente.Pos)) { Source = fuente, Mode = BindingMode.OneWay });

        // Asi lo cablea SectorView: el evento hace el seek y quien actualiza la posicion es
        // el NODO (la fuente), nunca el control.
        var seeks = 0;
        timeline.SeekRequested += (_, ms) => { seeks++; fuente.Pos = ms; };

        Console.WriteLine("=== 1. ANTES DEL CLICK (el Tick del board) ===");
        fuente.Pos = 1000;
        Console.WriteLine($"  fuente=1000  ->  control={timeline.PositionMs}");
        var antes = timeline.PositionMs == 1000;

        Console.WriteLine();
        Console.WriteLine("=== 2. CLICK REAL EN EL RIEL (OnTrackClick por reflexion) ===");
        var handler = typeof(LoopTimeline).GetMethod("OnTrackClick", BindingFlags.NonPublic | BindingFlags.Instance);
        if (handler is null) { Console.WriteLine("  NO ENCONTRE OnTrackClick"); return 1; }

        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
        };

        try
        {
            handler.Invoke(timeline, [timeline, args]);
            Console.WriteLine($"  OnTrackClick ejecutado. SeekRequested disparado {seeks} vez/veces.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  no se pudo invocar: {ex.InnerException?.Message ?? ex.Message}");
            return 1;
        }

        var bindingVivo = BindingOperations.GetBindingExpression(timeline, LoopTimeline.PositionMsProperty) is not null;
        Console.WriteLine($"  ¿sobrevivio el binding?  {(bindingVivo ? "SI" : "NO")}");

        Console.WriteLine();
        Console.WriteLine("=== 3. DESPUES DEL CLICK: el video sigue reproduciendo ===");
        fuente.Pos = 2000;
        Console.WriteLine($"  fuente=2000  ->  control={timeline.PositionMs}");
        fuente.Pos = 3500;
        Console.WriteLine($"  fuente=3500  ->  control={timeline.PositionMs}");
        var despues = timeline.PositionMs == 3500;

        Console.WriteLine();
        Console.WriteLine("=== VEREDICTO ===");
        Console.WriteLine($"  el binding funcionaba antes del click : {(antes ? "SI" : "NO")}");
        Console.WriteLine($"  el click pidio el seek                : {(seeks == 1 ? "SI" : "NO")}");
        Console.WriteLine($"  la marca SIGUE avanzando despues      : {(despues ? "SI" : "NO -> SIGUE CLAVADA")}");
        Console.WriteLine();
        Console.WriteLine($"  {(antes && bindingVivo && despues ? "ARREGLADO" : "SIGUE ROTO")}");
        return antes && bindingVivo && despues ? 0 : 1;
    }
}
