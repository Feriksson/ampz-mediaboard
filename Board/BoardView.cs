using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AmpzMediaBoard.Controls;
using AmpzMediaBoard.Layout;

namespace AmpzMediaBoard.Board;

/// <summary>
/// Materializa el ÁRBOL de layout en controles de WPF. Está escrito en C# y no en XAML con
/// DataTemplates recursivos por una razón concreta: cada <see cref="SplitNode"/> necesita un
/// Grid cuyas definiciones son COLUMNAS o FILAS según su orientación, y hay que enganchar el
/// <c>DragCompleted</c> del splitter para escribir el ratio de vuelta al modelo. Hacer eso con
/// DataTemplates exige un selector de plantillas más converters de GridLength con binding
/// bidireccional — más piezas, más frágil y bastante más difícil de leer que este método.
/// </summary>
public sealed class BoardView : ContentControl
{
    private readonly BoardViewModel _board;

    /// <summary>
    /// Qué Grid materializa a cada split. Existe para poder reescribir los ratios EN CALIENTE
    /// (ver <see cref="ApplyRatios"/>) sin reconstruir el árbol visual: reconstruir obliga a
    /// re-montar todos los clips, y para mover tres GridLength eso es un precio absurdo.
    /// Se vacía en cada Rebuild — los Grid de la pasada anterior ya no existen.
    /// </summary>
    private readonly Dictionary<SplitNode, Grid> _grids = new();

    private const double SplitterSize = 6;

    public BoardView(BoardViewModel board)
    {
        _board = board;
        _board.LayoutChanged += Rebuild;
        _board.RatiosChanged += ApplyRatios;
        _board.PropertyChanged += OnBoardPropertyChanged;
        Rebuild();
    }

    private void OnBoardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BoardViewModel.Expanded)) ApplyExpand();
    }

    /// <summary>
    /// Materializa <see cref="BoardViewModel.Expanded"/> SIN reconstruir nada.
    ///
    /// ⚠ No se re-parenta el VideoView del expandido (re-parentar = re-montar, bug #4): se sube
    /// por los splits de su camino a la raíz y en cada uno el lado del sector pasa a 1*, y el otro
    /// lado y el splitter a 0 + Collapsed. Restaurar = reescribir las GridLength desde los Ratio
    /// (que NUNCA se tocaron: tocar Ratio marcaría el documento como modificado) y devolver
    /// Visible a todo.
    ///
    /// Los tapados además se congelan en la vista (SetFrozen): colapsar el ancestro esconde el
    /// HWND, pero la ventana de overlay de LibVLCSharp necesita el mismo camino probado que el
    /// arrastre y la pestaña de fondo. El congelado del NODO lo hace BoardViewModel.Expand.
    /// </summary>
    private void ApplyExpand()
    {
        WriteRatios();
        foreach (var grid in _grids.Values)
            foreach (UIElement child in grid.Children)
                child.Visibility = Visibility.Visible;

        if (_board.Expanded is { } expanded)
        {
            foreach (var (split, keepFirst) in SplitNode.PathToRoot(expanded))
            {
                if (!_grids.TryGetValue(split, out var grid)) continue;
                var keep = keepFirst ? 0 : 2;
                var horizontal = split.Orientation == SplitOrientation.Horizontal;

                for (var i = 0; i < 3; i++)
                {
                    var len = i == keep ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                    if (horizontal) grid.ColumnDefinitions[i].Width = len;
                    else grid.RowDefinitions[i].Height = len;
                }

                foreach (UIElement child in grid.Children)
                {
                    var index = horizontal ? Grid.GetColumn(child) : Grid.GetRow(child);
                    if (index != keep) child.Visibility = Visibility.Collapsed;
                }
            }
        }

        foreach (var view in FindSectorViews(Content as DependencyObject))
            view.SetFrozen(_suspended || (view.DataContext is SectorNode node && _board.IsHiddenByExpand(node)));
    }

    /// <summary>
    /// Deja rastro (DiagLog) cada vez que la vista SALE de un padre visual: re-parentarla o
    /// sacarla de BoardHost. Reordenar pestañas no puede hacerlo nunca, y desde afuera no se nota
    /// — ni reabre archivos ni apaga el video — así que sin este aviso la prueba no lo ve. Ver
    /// tools/test-tab-reorder.ps1.
    ///
    /// ⚠ Por qué acá y no en Unloaded: se midió que un Remove + Insert en la misma vuelta del
    /// Dispatcher NO dispara Unloaded (WPF lo posterga y lo anula al volver a cargarse), y la
    /// mutación "re-parentar al reordenar" pasaba en verde. Este override corre sincrónico en
    /// cada cambio de padre. Colapsar la vista (cambiar de pestaña) no cambia el padre.
    /// </summary>
    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (oldParent is not null) DiagLog.Write($"viewreparent {oldParent.GetType().Name}");
    }

    /// <summary>
    /// Reconstruye el árbol visual COMPLETO. Es un martillo, sí, pero solo se dispara cuando el
    /// usuario parte o cierra un sector — acciones deliberadas y poco frecuentes. Reconstruir
    /// únicamente el subárbol afectado sería más eficiente y bastante más código; no vale el
    /// cambio hasta que se note.
    /// </summary>
    private void Rebuild()
    {
        // Los VideoView de la pasada anterior se sueltan a mano: cada uno hostea una ventana
        // nativa, y tirar el árbol visual sin desengancharlos deja esas ventanas colgadas.
        foreach (var view in FindSectorViews(Content as DependencyObject))
            view.Detach();

        // Y los clips se re-montan: la ventana de salida de libvlc se fija en el Play(), así que
        // un reproductor que sobrevive a la reconstrucción se queda dibujando en el HWND viejo,
        // que ya no existe. Remount() lo relanza desde donde iba. Ver SectorNode.Remount.
        // Los clips que todavía no arrancaron (recién abiertos desde un .mboard) NO se tocan:
        // Remount los saltea y la vista nueva los arranca. Antes se cargaban dos veces.
        foreach (var sector in SplitNode.Sectors(_board.Root))
            sector.Remount();

        _grids.Clear();
        Content = Build(_board.Root);

        // Una vista de segundo plano que se reconstruye (se abrió un board en una pestaña que
        // todavía no se mostró) nace con sus sectores congelados, igual que los que reemplaza.
        if (_suspended) SetSectorsFrozen(true);

        // Red de seguridad: el modelo suelta la expansión antes de cambiar la forma del árbol,
        // pero si alguna ruta llegara acá con un expandido vivo, los Grid nuevos lo respetan.
        if (_board.Expanded is not null) ApplyExpand();
    }

    private UIElement Build(LayoutNode node) => node switch
    {
        SectorNode sector => new SectorView { DataContext = sector, Board = _board },
        SplitNode split => BuildSplit(split),
        _ => new Border(),
    };

    private UIElement BuildSplit(SplitNode split)
    {
        var horizontal = split.Orientation == SplitOrientation.Horizontal;
        var grid = new Grid();

        // Tres pistas: primer hijo (star), splitter (fijo), segundo hijo (star). Los star
        // guardan la PROPORCIÓN, así el layout se adapta solo a cualquier tamaño de ventana.
        var firstLen = new GridLength(split.Ratio, GridUnitType.Star);
        var secondLen = new GridLength(1 - split.Ratio, GridUnitType.Star);

        var splitter = new GridSplitter
        {
            Background = (Brush)Application.Current.Resources["SplitterBrush"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        };

        if (horizontal)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = firstLen });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SplitterSize) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = secondLen });
            splitter.ResizeDirection = GridResizeDirection.Columns;
            splitter.Cursor = System.Windows.Input.Cursors.SizeWE;
            Grid.SetColumn(splitter, 1);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = firstLen });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SplitterSize) });
            grid.RowDefinitions.Add(new RowDefinition { Height = secondLen });
            splitter.ResizeDirection = GridResizeDirection.Rows;
            splitter.Cursor = System.Windows.Input.Cursors.SizeNS;
            Grid.SetRow(splitter, 1);
        }

        var first = Build(split.First);
        var second = Build(split.Second);

        if (horizontal)
        {
            Grid.SetColumn(first, 0);
            Grid.SetColumn(second, 2);
        }
        else
        {
            Grid.SetRow(first, 0);
            Grid.SetRow(second, 2);
        }

        grid.Children.Add(first);
        grid.Children.Add(splitter);
        grid.Children.Add(second);

        // El ratio se escribe al MODELO recién al soltar, no en cada píxel del arrastre:
        // durante el drag el GridSplitter ya maneja las GridLength solo. Escribir en cada delta
        // solo sirve para meter ruido y para que un guardado a mitad de arrastre grabe basura.
        _grids[split] = grid;

        // ══ Congelado durante el arrastre ══
        // Arrastrar un divisor con varios videos corriendo se ponía inusable: cada sector con
        // video hostea una ventana nativa que WPF reposiciona en CADA píxel del movimiento, y
        // VLC tiene que reconfigurar su salida en cada una de esas veces mientras decodifica.
        // Mientras dura el arrastre los clips se pausan y su superficie se colapsa; al soltar,
        // vuelven a donde estaban. Ver SectorNode.Freeze.
        splitter.DragStarted += (_, _) =>
        {
            _board.BeginInteractiveResize();
            SetSectorsFrozen(true);
        };

        // ⚠ DragCompleted dispara TAMBIÉN cuando el arrastre se cancela con Esc, que es
        // justamente lo que hace falta: si el descongelado colgara de un final "exitoso", un
        // Esc a mitad de camino te dejaría el board entero pausado y en negro para siempre.
        splitter.DragCompleted += (_, _) =>
        {
            SetSectorsFrozen(false);
            _board.EndInteractiveResize();

            var a = horizontal ? grid.ColumnDefinitions[0].Width.Value : grid.RowDefinitions[0].Height.Value;
            var b = horizontal ? grid.ColumnDefinitions[2].Width.Value : grid.RowDefinitions[2].Height.Value;
            var total = a + b;
            if (total > 0) split.Ratio = Math.Clamp(a / total, 0.05, 0.95);
        };

        return grid;
    }

    /// <summary>
    /// Reescribe las GridLength de los splits desde el modelo, SIN reconstruir nada.
    ///
    /// Lo dispara <c>BoardViewModel.RatiosChanged</c> (hoy: el botón "Distribuir"). La diferencia
    /// con un Rebuild es grande y se nota: acá los VideoView no se tocan, así que ningún clip se
    /// re-monta, ninguno parpadea y ninguno pierde su posición — los sectores simplemente se
    /// acomodan al tamaño nuevo.
    /// </summary>
    private void ApplyRatios()
    {
        WriteRatios();
        if (_board.Expanded is not null) ApplyExpand();
    }

    /// <summary>Las GridLength desde los Ratio del modelo, tal cual (sin mirar la expansión).</summary>
    private void WriteRatios()
    {
        foreach (var (split, grid) in _grids)
        {
            var first = new GridLength(split.Ratio, GridUnitType.Star);
            var second = new GridLength(1 - split.Ratio, GridUnitType.Star);

            if (split.Orientation == SplitOrientation.Horizontal)
            {
                grid.ColumnDefinitions[0].Width = first;
                grid.ColumnDefinitions[2].Width = second;
            }
            else
            {
                grid.RowDefinitions[0].Height = first;
                grid.RowDefinitions[2].Height = second;
            }

            // El splitter vuelve a su ancho fijo: la expansión lo pudo haber dejado en 0.
            if (split.Orientation == SplitOrientation.Horizontal) grid.ColumnDefinitions[1].Width = new GridLength(SplitterSize);
            else grid.RowDefinitions[1].Height = new GridLength(SplitterSize);
        }
    }

    /// <summary>Esconde (o devuelve) la superficie de video de todos los sectores del board.</summary>
    private void SetSectorsFrozen(bool frozen)
    {
        // ⚠ Descongelar NO alcanza a los tapados por la expansión: su video tiene que seguir
        // escondido (ver ApplyExpand). Congelar sí alcanza a todos (es idempotente).
        foreach (var view in FindSectorViews(Content as DependencyObject))
        {
            if (!frozen && view.DataContext is SectorNode node && _board.IsHiddenByExpand(node)) continue;
            view.SetFrozen(frozen);
        }
    }

    /// <summary>
    /// Congela el board entero por un arrastre que NO es de un splitter propio: el divisor entre
    /// las pestañas y el panel fijado (MainWindow). Ese divisor cambia el ancho de los DOS lados,
    /// así que mueve las ventanas nativas de los dos: MainWindow congela la pestaña activa Y el
    /// panel con esto. Mismo arreglo en dos mitades que el splitter interno (pausa + colapso).
    /// </summary>
    public void BeginExternalResize()
    {
        _board.BeginInteractiveResize();
        SetSectorsFrozen(true);
    }

    /// <summary>
    /// Termina el congelado de <see cref="BeginExternalResize"/>. ⚠ Si la pestaña pasó a segundo
    /// plano a mitad del arrastre (Ctrl+Tab con el mouse apretado) NO se descongela la vista:
    /// devolvería el video de una pestaña colapsada. Resume la despierta al volver.
    /// </summary>
    public void EndExternalResize()
    {
        if (_suspended) return;
        SetSectorsFrozen(false);
        _board.EndInteractiveResize();
    }

    /// <summary>La vista está en una pestaña de segundo plano. Ver <see cref="SetSuspended"/>.</summary>
    private bool _suspended;

    /// <summary>
    /// Manda la vista a segundo plano (o la trae de vuelta) SIN reconstruir nada.
    ///
    /// ⚠ Es la decisión central de las pestañas: la vista de una pestaña inactiva sigue VIVA,
    /// colapsada. Reconstruirla al volver obligaría a Remount en cada sector (VLC reabriendo
    /// cada archivo: frames negros y un salto visible), y eso es justo lo que hace que cambiar
    /// de pestaña se sienta lento. Así, cambiar de pestaña es cambiar dos Visibility.
    ///
    /// Colapsa la vista entera (sale del layout: sus ventanas nativas no se miden ni se
    /// posicionan) y ADEMÁS congela cada sector con el mismo SetFrozen del arrastre de
    /// splitter, que es el camino ya probado para esconder la ventana del VideoView y su
    /// ventana de overlay. Collapsed y no Hidden, por el mismo motivo que en SetFrozen: Hidden
    /// sigue participando del layout.
    /// </summary>
    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        SetSectorsFrozen(suspended);
        Visibility = suspended ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Suelta todos los VideoView antes de tirar la vista (se cerró la pestaña). Igual que en
    /// Rebuild: cada uno hostea una ventana nativa que hay que desenganchar a mano. Se llama
    /// DESPUÉS de liberar el board, así los players ya se desengancharon por el camino de
    /// SectorNode.Unload (desenganchar antes de liberar).
    /// </summary>
    public void Release()
    {
        _board.LayoutChanged -= Rebuild;
        _board.RatiosChanged -= ApplyRatios;
        _board.PropertyChanged -= OnBoardPropertyChanged;
        foreach (var view in FindSectorViews(Content as DependencyObject))
            view.Detach();
        Content = null;
        _grids.Clear();
    }

    /// <summary>Recorre el árbol visual juntando los SectorView para poder soltarlos.</summary>
    private static IEnumerable<SectorView> FindSectorViews(DependencyObject? root)
    {
        if (root is null) yield break;
        if (root is SectorView view)
        {
            yield return view;
            yield break;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            foreach (var found in FindSectorViews(VisualTreeHelper.GetChild(root, i)))
                yield return found;
        }
    }
}
