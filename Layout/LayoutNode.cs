using CommunityToolkit.Mvvm.ComponentModel;

namespace AmpzMediaBoard.Layout;

/// <summary>
/// El layout del board es un ÁRBOL BINARIO, no una grilla NxM. La razón es el requisito:
/// "dividir la grilla" tipo tmux/OBS es RECURSIVO por naturaleza — una grilla fija de filas y
/// columnas NO puede representar "A ocupa toda la mitad izquierda, y la derecha está partida
/// en tres pedazos de distinto alto". Un árbol lo representa gratis, y además serializa a JSON
/// sin ninguna gimnasia (que es exactamente lo que el board guardable necesita).
///
/// Solo hay dos formas de nodo: <see cref="SplitNode"/> (interno, siempre 2 hijos) y
/// <see cref="SectorNode"/> (hoja, es donde cae el archivo). No hay un tercer caso, a propósito.
/// </summary>
public abstract class LayoutNode : ObservableObject
{
    /// <summary>
    /// El padre se mantiene para poder COLAPSAR un sector: al cerrar una hoja, su hermano
    /// tiene que subir a ocupar el lugar del split. Sin puntero al padre eso obliga a
    /// re-recorrer el árbol entero en cada cierre.
    /// </summary>
    public SplitNode? Parent { get; internal set; }
}

/// <summary>Horizontal = los hijos van lado a lado (dos columnas). Vertical = uno arriba del otro.</summary>
public enum SplitOrientation
{
    Horizontal,
    Vertical,
}

/// <summary>Nodo interno: parte el espacio en dos según <see cref="Ratio"/>.</summary>
public sealed partial class SplitNode : LayoutNode
{
    private LayoutNode _first = null!;
    private LayoutNode _second = null!;

    public SplitNode(SplitOrientation orientation, LayoutNode first, LayoutNode second, double ratio = 0.5)
    {
        Orientation = orientation;
        Ratio = ratio;
        First = first;
        Second = second;
    }

    [ObservableProperty]
    private SplitOrientation _orientation;

    /// <summary>
    /// Proporción del PRIMER hijo, 0..1. Se guarda como proporción y NO como píxeles porque el
    /// board se reabre en otra resolución / con la ventana de otro tamaño: los píxeles no
    /// sobreviven, la proporción sí.
    /// </summary>
    [ObservableProperty]
    private double _ratio = 0.5;

    public LayoutNode First
    {
        get => _first;
        set { _first = value; value.Parent = this; OnPropertyChanged(); }
    }

    public LayoutNode Second
    {
        get => _second;
        set { _second = value; value.Parent = this; OnPropertyChanged(); }
    }

    /// <summary>Devuelve el OTRO hijo. Lo usa el colapso al cerrar un sector.</summary>
    public LayoutNode Sibling(LayoutNode child) => ReferenceEquals(child, First) ? Second : First;

    public void Replace(LayoutNode oldChild, LayoutNode newChild)
    {
        if (ReferenceEquals(oldChild, First)) First = newChild;
        else if (ReferenceEquals(oldChild, Second)) Second = newChild;
    }

    /// <summary>Recorrido en profundidad: todas las hojas de este subárbol.</summary>
    public static IEnumerable<SectorNode> Sectors(LayoutNode root)
    {
        switch (root)
        {
            case SectorNode leaf:
                yield return leaf;
                break;
            case SplitNode split:
                foreach (var s in Sectors(split.First)) yield return s;
                foreach (var s in Sectors(split.Second)) yield return s;
                break;
        }
    }
}
