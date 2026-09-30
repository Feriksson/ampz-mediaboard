using AmpzMediaBoard.Layout;
using AmpzMediaBoard.Media;
using AmpzMediaBoard.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmpzMediaBoard.Board;

/// <summary>
/// Una PESTAÑA: un board con su vista y su archivo. Es todo el estado por-board que antes vivía
/// suelto en MainWindow (el ViewModel, la vista, el `.mboard` actual y el título).
///
/// La vista se crea UNA vez y vive lo mismo que la pestaña. No es un detalle: cada BoardView
/// hostea ventanas nativas de VLC, y recrearla al volver a la pestaña obligaría a re-montar cada
/// clip (bug #4 del CLAUDE.md). Una pestaña inactiva está suspendida, no destruida.
/// </summary>
public sealed partial class BoardTab : ObservableObject, IDisposable
{
    public BoardViewModel Board { get; } = new();

    public BoardView View { get; }

    /// <summary>Archivo `.mboard` de la pestaña, o null si el board todavía no se guardó en ninguno.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header), nameof(ToolTipText), nameof(WindowTitle))]
    private string? _filePath;

    [ObservableProperty] private bool _isActive;

    /// <summary>El board difiere de su archivo (o, sin archivo, ya tiene algo adentro). Pinta el "•".</summary>
    [ObservableProperty] private bool _isModified;

    /// <summary>
    /// El archivo tal como estaba al abrirlo o guardarlo, NORMALIZADO (ver
    /// <see cref="BoardStore.ReadNormalized"/>). Contra esto se compara la marca "•": la misma
    /// comparación que el aviso de cambios sin guardar, sin leer el disco en cada refresco.
    /// </summary>
    private string? _savedSnapshot;

    public BoardTab() => View = new BoardView(Board);

    public string Header => FilePath is null ? "Sin guardar" : Path.GetFileNameWithoutExtension(FilePath);

    public string ToolTipText => FilePath ?? "Board sin guardar en ningún archivo";

    /// <summary>
    /// Título de la ventana cuando esta pestaña es la activa. Board primero, marca al final: la
    /// barra de tareas trunca por la derecha (ver "El título va board primero" en el CLAUDE.md).
    /// </summary>
    public string WindowTitle => FilePath is null ? "Board sin guardar — AMB" : $"{Header} — AMB";

    /// <summary>
    /// Board recién nacido: un solo sector, sin nada adentro. Si el usuario partió la pantalla
    /// o cargó un clip, eso YA es trabajo y merece el aviso.
    /// </summary>
    public bool IsEmpty => Board.Root is SectorNode { Kind: MediaKind.None, MissingPath: null };

    /// <summary>Pestaña en blanco: sin archivo y vacía. "Abrir" la reutiliza en vez de abrir otra.</summary>
    public bool IsBlank => FilePath is null && IsEmpty;

    /// <summary>Pone en la pestaña un board leído de <paramref name="path"/>.</summary>
    public void Open(LayoutNode root, string path)
    {
        Board.ReplaceRoot(root);
        MarkSaved(path);
    }

    /// <summary>El board quedó escrito en <paramref name="path"/>: ese pasa a ser su archivo.</summary>
    public void MarkSaved(string path)
    {
        FilePath = path;
        _savedSnapshot = BoardStore.ReadNormalized(path);
        RefreshModified();
    }

    public void RefreshModified() =>
        IsModified = FilePath is null
            ? !IsEmpty
            : _savedSnapshot is not null && BoardStore.Serialize(Board.Root) != _savedSnapshot;

    /// <summary>Trae la pestaña a primer plano: vista visible y clips reanudados.</summary>
    public void Activate()
    {
        // Primero la vista y después el board: el Visible dispara el arranque DIFERIDO de los
        // clips pendientes (SectorView.StartWhenSurfaceReady), que así corre con el board ya
        // descongelado.
        View.SetSuspended(false);
        Board.Resume();
        IsActive = true;
    }

    /// <summary>Manda la pestaña a segundo plano: clips pausados, latido apagado, vista colapsada.</summary>
    public void Deactivate()
    {
        IsActive = false;
        Board.Suspend();
        View.SetSuspended(true);
    }

    /// <summary>
    /// Libera la pestaña. Board ANTES que vista: el board desengancha cada player de su
    /// VideoView (SectorNode.Unload) y recién después la vista suelta los VideoView.
    /// </summary>
    public void Dispose()
    {
        Board.Dispose();
        View.Release();
    }
}
