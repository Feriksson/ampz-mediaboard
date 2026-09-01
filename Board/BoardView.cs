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

    public BoardView(BoardViewModel board)
    {
        _board = board;
        _board.LayoutChanged += Rebuild;
        _board.RatiosChanged += ApplyRatios;
        Rebuild();
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
        foreach (var sector in SplitNode.Sectors(_board.Root))
            sector.Remount();

        _grids.Clear();
        Content = Build(_board.Root);
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
        const double SplitterSize = 6;

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
        }
    }

    /// <summary>Esconde (o devuelve) la superficie de video de todos los sectores del board.</summary>
    private void SetSectorsFrozen(bool frozen)
    {
        foreach (var view in FindSectorViews(Content as DependencyObject))
            view.SetFrozen(frozen);
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
