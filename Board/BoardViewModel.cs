using System.Windows.Threading;
using AmpzMediaBoard.Layout;
using AmpzMediaBoard.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmpzMediaBoard.Board;

/// <summary>
/// Dueño del árbol de layout y del ÚNICO latido de la app.
///
/// Sobre el timer: hay UNO solo para todo el board, no uno por sector. Ocho DispatcherTimer
/// compitiendo por la misma cola del Dispatcher se pisan entre ellos y el jitter arruina
/// justamente lo que queremos preciso: el punto de corte del loop. Uno que itera ocho sectores
/// es más simple Y más estable.
/// </summary>
public sealed partial class BoardViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _tick;

    /// <summary>
    /// Se dispara cuando la FORMA del árbol cambió (split o cierre de sector) y la vista tiene
    /// que reconstruirse. Los cambios de contenido de un sector NO pasan por acá: esos van por
    /// binding normal.
    /// </summary>
    public event Action? LayoutChanged;

    [ObservableProperty] private LayoutNode _root;
    [ObservableProperty] private SectorNode? _selected;

    public BoardViewModel()
    {
        // El board arranca con un único sector vacío ocupando todo. Desde ahí el usuario parte.
        var first = new SectorNode();
        _root = first;
        _selected = first;

        // ~33ms ≈ 30 Hz. Suficiente para que la timeline se vea fluida y para que el corte del
        // loop caiga dentro del margen de LoopGuardMs. Bajarlo más solo quema CPU: la precisión
        // real está limitada por la latencia del seek de VLC, no por el polling.
        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _tick.Tick += (_, _) =>
        {
            foreach (var sector in SplitNode.Sectors(Root))
                sector.Tick();
        };
        _tick.Start();
    }

    public IEnumerable<SectorNode> AllSectors => SplitNode.Sectors(Root);

    public void Select(SectorNode sector)
    {
        if (ReferenceEquals(Selected, sector)) return;
        if (Selected is not null) Selected.IsSelected = false;
        Selected = sector;
        sector.IsSelected = true;
    }

    /// <summary>
    /// Parte un sector en dos. El sector existente pasa a ser el PRIMER hijo de un split nuevo,
    /// y el segundo hijo es un sector vacío. El clip que había NO se toca: se achica y punto.
    /// </summary>
    public void Split(SectorNode sector, SplitOrientation orientation)
    {
        // ⚠ EL PADRE SE CAPTURA ANTES DE CREAR EL SPLIT. No es cosmético: el constructor de
        // SplitNode reasigna sector.Parent al split recién creado. Si lo leyéramos después,
        // "el padre del sector" sería el split nuevo, y el Replace de más abajo haría
        // split.First = split — el árbol se apunta a sí mismo, la raíz nunca cambia y el board
        // se queda con una sola celda para siempre. Este bug ya pasó una vez; no lo revivas.
        var previousParent = sector.Parent;

        var fresh = new SectorNode();
        var split = new SplitNode(orientation, sector, fresh);

        if (previousParent is { } parent)
        {
            split.Parent = parent;
            parent.Replace(sector, split);
        }
        else
        {
            // Era la raíz: el split nuevo pasa a ser la raíz.
            split.Parent = null;
            Root = split;
        }

        Select(fresh);
        LayoutChanged?.Invoke();
    }

    /// <summary>
    /// Cierra un sector. El HERMANO sube a ocupar el lugar del split que los contenía — así el
    /// árbol nunca queda con un split de un solo hijo (un split con un hijo no es un split).
    /// Cerrar el último sector no lo elimina: lo VACÍA, porque un board sin sectores no se
    /// puede volver a partir y dejaría la app en un callejón sin salida.
    /// </summary>
    public void Close(SectorNode sector)
    {
        if (sector.Parent is not { } parent)
        {
            sector.Unload();
            LayoutChanged?.Invoke();
            return;
        }

        var sibling = parent.Sibling(sector);
        sector.Dispose();

        if (parent.Parent is { } grandParent)
        {
            sibling.Parent = grandParent;
            grandParent.Replace(parent, sibling);
        }
        else
        {
            sibling.Parent = null;
            Root = sibling;
        }

        Select(SplitNode.Sectors(Root).First());
        LayoutChanged?.Invoke();
    }

    /// <summary>Reemplaza el board entero (lo usa la carga desde disco).</summary>
    public void ReplaceRoot(LayoutNode root)
    {
        foreach (var sector in SplitNode.Sectors(Root))
            sector.Dispose();

        Root = root;
        root.Parent = null;
        Select(SplitNode.Sectors(root).First());
        LayoutChanged?.Invoke();
    }

    public void Save() => BoardStore.Save(Root);

    public void Dispose()
    {
        _tick.Stop();
        foreach (var sector in SplitNode.Sectors(Root))
            sector.Dispose();
    }
}
