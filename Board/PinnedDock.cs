using AmpzMediaBoard.Layout;
using AmpzMediaBoard.Persistence;

namespace AmpzMediaBoard.Board;

/// <summary>
/// El PANEL FIJADO: sectores que quedan a la vista en TODAS las pestañas, a la derecha del área
/// de boards. El 📌 de la cabecera de un sector lo manda acá; el 📌 de un sector del panel lo
/// devuelve a la pestaña activa.
///
/// ⚠ Vive FUERA de las pestañas, y no es un detalle de layout: cada video es una ventana nativa
/// hosteada que NO puede cambiar de padre sin re-montarse (bug #4 del CLAUDE.md). Si el panel
/// viviera dentro del contenedor de una pestaña, cambiar de pestaña lo colapsaría, lo pausaría o
/// lo re-montaría. Al lado de las pestañas, cambiar de pestaña ni se entera de que existe: el
/// panel nunca se pausa, nunca se congela y nunca se reconstruye por un cambio de pestaña.
///
/// Es un <see cref="BoardViewModel"/> más (con <see cref="BoardViewModel.IsDock"/>) y no un
/// modelo propio a propósito: así hereda gratis su PROPIO latido (que nadie suspende), la
/// selección, el congelado del splitter y la vista (<see cref="BoardView"/> + SectorView, sin
/// una línea de video fuera de la capa de render). Su árbol es siempre una pila VERTICAL: la
/// vista del panel no ofrece partir, y cada sector fijado se apila abajo con alturas iguales.
///
/// Esta clase tiene la lógica que CRUZA boards (fijar, desfijar, el solo) y no toca WPF, así la
/// prueba tools/BoardProbe la ejercita sin ventanas.
/// </summary>
public sealed class PinnedDock : IDisposable
{
    public const double DefaultWidth = 0.25;

    /// <summary>
    /// El board del panel. Su volumen general queda SIEMPRE en 100 y no hay control para
    /// moverlo: ver <see cref="Pin"/> sobre por qué el panel ignora el master de las pestañas.
    /// </summary>
    public BoardViewModel Board { get; } = new() { IsDock = true };

    private double _width = DefaultWidth;

    /// <summary>
    /// Ancho del panel como PROPORCIÓN del área de boards, nunca en píxeles: el archivo se
    /// reabre en otra pantalla o con la ventana de otro tamaño. Se persiste en el `.mboard`.
    /// </summary>
    public double Width
    {
        get => _width;
        set => _width = BoardStore.NormalizeWidth(value);
    }

    /// <summary>
    /// ¿El panel tiene algo que mostrar? Sin nada, se esconde entero (ancho cero, sin splitter).
    /// "Algo" = un sector con media o con un archivo ausente: el árbol del panel siempre tiene
    /// al menos un sector (igual que cualquier board), y uno vacío no justifica robarle ancho a
    /// las pestañas.
    /// </summary>
    public bool HasContent => Board.AllSectors.Any(HasSomething);

    private static bool HasSomething(SectorNode sector) => sector.HasMedia || sector.IsMissing;

    /// <summary>
    /// Fija un sector de una pestaña: su media se muda al panel y el sector sale de la pestaña.
    ///
    /// - Se mueve la DESCRIPCIÓN del media (<see cref="SectorNode.MediaSnapshot"/>), igual que el
    ///   intercambio entre sectores y por el mismo motivo: un MediaPlayer queda atado a la
    ///   ventana donde arrancó. Es UN re-montaje deliberado, desde la posición donde iba, con
    ///   markers, volumen y silencio.
    /// - El sector se CIERRA en su pestaña con la lógica de siempre: su hermano ocupa el lugar,
    ///   y si era el último, se vacía (<see cref="BoardViewModel.Close"/>).
    /// - En el panel cae en el primer sector vacío, o se apila abajo; y la pila se reparte en
    ///   alturas iguales.
    /// - El master de la pestaña NO viaja (no está en la foto: es del board, ver
    ///   <see cref="SectorNode.BoardMasterVolume"/>). En el panel suena a su volumen propio, con
    ///   master 100: el panel es de TODAS las pestañas, y que lo gobernara el slider de la que
    ///   estás mirando haría que el mismo clip suene distinto según qué pestaña tengas al frente.
    ///
    /// Un sector vacío no se fija (no hay nada que mover). Devuelve el sector del panel.
    /// </summary>
    public SectorNode? Pin(BoardViewModel from, SectorNode sector)
    {
        if (ReferenceEquals(from, Board) || !HasSomething(sector)) return null;
        if (!from.AllSectors.Contains(sector)) return null;

        var snapshot = sector.TakeSnapshot();

        // Primero se suelta el origen: su player se detiene en otro hilo (VlcEngine.Release) y
        // no hay un instante con el mismo clip decodificándose dos veces.
        from.Close(sector);

        var target = Board.AllSectors.FirstOrDefault(s => !HasSomething(s));
        if (target is null)
        {
            // Split del ÚLTIMO en vertical: la pila crece hacia abajo. Split re-monta los que ya
            // estaban en el panel (reconstruye su vista, como partir en una pestaña): es el costo
            // de una acción deliberada, y nunca lo dispara un cambio de pestaña.
            Board.Split(Board.AllSectors.Last(), SplitOrientation.Vertical);
            target = Board.Selected!;
        }

        target.Restore(snapshot);
        Board.Distribute();
        Board.Select(target);
        return target;
    }

    /// <summary>
    /// Desfija: el media vuelve a la pestaña ACTIVA (<paramref name="into"/>), al sector que
    /// resuelve <see cref="BoardViewModel.PrepareReturnTarget"/> (el seleccionado si está vacío;
    /// si no, la mitad nueva de partirlo). UN re-montaje, igual que fijar. El sector sale del
    /// panel; si era el último, el panel se vacía y se esconde.
    /// </summary>
    public SectorNode? Unpin(SectorNode sector, BoardViewModel into)
    {
        if (ReferenceEquals(into, Board) || !Board.AllSectors.Contains(sector)) return null;

        var snapshot = sector.TakeSnapshot();
        Board.Close(sector);

        // Si quedaron solo vacíos (el usuario vació alguno con ⏏), se vuelve a UN sector vacío:
        // si no, el próximo fijado aparecería apilado debajo de huecos que nadie pidió.
        if (!HasContent && Board.Root is not SectorNode) Board.ReplaceRoot(new SectorNode());
        else if (HasContent) Board.Distribute();

        var target = into.PrepareReturnTarget();
        target.Restore(snapshot);
        into.Select(target);
        return target;
    }

    /// <summary>
    /// SOLO con el panel de por medio: el alcance es la pestaña ACTIVA más el panel. Es lo que
    /// está sonando: el panel suena en todas las pestañas, y las otras pestañas están pausadas.
    /// Un solo que dejara sonando al panel no sería un solo.
    /// </summary>
    public void Solo(SectorNode sector, BoardViewModel activeTab) =>
        BoardViewModel.SoloAmong(sector, activeTab.AllSectors.Concat(Board.AllSectors));

    /// <summary>El panel como se guarda, o null si está vacío (vacío = ausente en el archivo).</summary>
    public DockData? ToData() => HasContent ? new DockData(Width, Board.AllSectors.ToList()) : null;

    /// <summary>
    /// Reemplaza el contenido del panel (abrir un archivo, "Nuevo"). Los sectores viejos se
    /// liberan por el camino que no bloquea (ReplaceRoot → VlcEngine.Release). Los nuevos nacen
    /// con sus clips PENDIENTES: arrancan cuando la vista del panel se hace visible (bug #1).
    /// </summary>
    public void Replace(DockData? data)
    {
        Width = data?.Width ?? DefaultWidth;
        Board.ReplaceRoot(data is { Sectors.Count: > 0 } ? Stack(data.Sectors, 0) : new SectorNode());
        Board.Distribute();
        // Abrir un archivo no selecciona nada del panel: la selección es de la pestaña activa.
        Board.ClearSelection();
    }

    /// <summary>Pila vertical: cada split le da al de arriba 1/n del resto → alturas iguales.</summary>
    private static LayoutNode Stack(IReadOnlyList<SectorNode> sectors, int from)
    {
        var remaining = sectors.Count - from;
        if (remaining == 1) return sectors[from];
        return new SplitNode(SplitOrientation.Vertical, sectors[from], Stack(sectors, from + 1), 1.0 / remaining);
    }

    public void Dispose() => Board.Dispose();
}
