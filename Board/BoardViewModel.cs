using System.Windows.Threading;
using AmpzMediaBoard.Layout;
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

    /// <summary>
    /// Se dispara cuando cambiaron los RATIOS de los splits pero NO la forma del árbol.
    ///
    /// Es un evento aparte de <see cref="LayoutChanged"/> a propósito, y la diferencia no es
    /// cosmética: LayoutChanged reconstruye el árbol visual ENTERO, lo que obliga a re-montar
    /// todos los clips (los VideoView se destruyen y VLC tiene que reabrir cada archivo desde
    /// donde iba — ver SectorNode.Remount). Para "repartir el espacio en partes iguales" eso
    /// sería pagar un re-decode de todo el board por cambiar tres números: la vista solo tiene
    /// que reescribir las GridLength que ya existen, sin tocar una sola ventana de video.
    /// </summary>
    public event Action? RatiosChanged;

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

    /// <summary>
    /// Mueve el contenido de un sector a otro, INTERCAMBIÁNDOLOS.
    ///
    /// Es un swap y no un "mover y vaciar el origen" por una razón concreta: arrastrar sobre un
    /// sector OCUPADO tiene que reacomodar, no destruir. Con "mover" a secas, soltar sobre una
    /// celda con un clip te lo borraría en silencio — y el usuario que está reordenando su board
    /// no está pidiendo borrar nada. Si el destino está vacío, el swap ES un movimiento simple.
    /// </summary>
    public void SwapMedia(SectorNode source, SectorNode target)
    {
        if (ReferenceEquals(source, target)) return;

        // Las dos fotos se toman ANTES de tocar nada: restaurar sobre uno modifica ese nodo, y
        // si sacáramos la segunda foto después ya estaría leyendo el contenido recién puesto.
        var fromSource = source.TakeSnapshot();
        var fromTarget = target.TakeSnapshot();

        target.Restore(fromSource);
        source.Restore(fromTarget);

        Select(target);
    }

    /// <summary>
    /// Reparte el espacio en partes IGUALES entre todos los sectores, en los dos ejes a la vez.
    ///
    /// ⚠ Poner todos los splits en 0.5 NO alcanza, y es el error obvio. En un árbol binario el
    /// tamaño de una hoja es el PRODUCTO de los ratios que hay desde la raíz hasta ella: con un
    /// board partido en A | (B / C), los tres al 0.5 dan A=50%, B=25% y C=25%. Igualar los
    /// ratios iguala los HERMANOS, no los sectores.
    ///
    /// Lo que sí funciona es repartir según CUÁNTAS HOJAS cuelgan de cada lado. Si cada split le
    /// da a cada hijo una porción proporcional a sus sectores, los factores se telescopean por el
    /// camino y toda hoja termina valiendo exactamente 1/N del board — sin importar la forma del
    /// árbol ni cómo se mezclen las orientaciones. En el ejemplo: la raíz queda en 1/3 (A) contra
    /// 2/3 (B y C), y el split interno en 0.5. Los tres al 33%.
    /// </summary>
    public void Distribute()
    {
        DistributeInto(Root);
        RatiosChanged?.Invoke();
    }

    private static void DistributeInto(LayoutNode node)
    {
        if (node is not SplitNode split) return;

        var first = LeafCount(split.First);
        var second = LeafCount(split.Second);
        split.Ratio = (double)first / (first + second);

        DistributeInto(split.First);
        DistributeInto(split.Second);
    }

    /// <summary>Cuántos sectores (hojas) cuelgan de este subárbol.</summary>
    private static int LeafCount(LayoutNode node) =>
        node is SplitNode split ? LeafCount(split.First) + LeafCount(split.Second) : 1;

    /// <summary>
    /// Arranca un arrastre de splitter: congela TODOS los sectores (pausa + oculta el video).
    ///
    /// Es global y no solo para los dos sectores del splitter que se está moviendo, por un
    /// motivo geométrico: mover un divisor cambia el ancho de una columna, y eso re-layoutea a
    /// todos los que viven dentro de ella. Congelar solo el par vecino dejaría el resto del
    /// board igual de trabado. Ver SectorNode.Freeze para el por qué del problema.
    /// </summary>
    public void BeginInteractiveResize()
    {
        foreach (var sector in AllSectors) sector.Freeze();
    }

    /// <summary>Termina el arrastre: descongela y devuelve a Play lo que estaba reproduciendo.</summary>
    public void EndInteractiveResize()
    {
        foreach (var sector in AllSectors) sector.Thaw();
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

    public void Dispose()
    {
        _tick.Stop();
        foreach (var sector in SplitNode.Sectors(Root))
            sector.Dispose();
    }
}
