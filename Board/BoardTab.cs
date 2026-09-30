using AmpzMediaBoard.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmpzMediaBoard.Board;

/// <summary>
/// Una PESTAÑA: un board con su vista y su nombre. El ARCHIVO no es de la pestaña: un `.mboard`
/// guarda TODAS las pestañas de la ventana, así que ruta, "¿hay cambios?" y el aviso de descarte
/// viven en MainWindow, a nivel documento.
///
/// La vista se crea UNA vez y vive lo mismo que la pestaña. No es un detalle: cada BoardView
/// hostea ventanas nativas de VLC, y recrearla al volver a la pestaña obligaría a re-montar cada
/// clip (bug #4 del CLAUDE.md). Una pestaña inactiva está suspendida, no destruida.
/// </summary>
public sealed partial class BoardTab : ObservableObject, IDisposable
{
    public BoardViewModel Board { get; } = new();

    public BoardView View { get; }

    /// <summary>Nombre visible en la tira. Se persiste; renombrar es un cambio sin guardar.</summary>
    [ObservableProperty] private string _name;

    [ObservableProperty] private bool _isActive;

    /// <summary>Se está editando el nombre (doble click sobre la pestaña). Cambia etiqueta por caja de texto.</summary>
    [ObservableProperty] private bool _isRenaming;

    /// <summary>La pestaña difiere de cómo estaba en el archivo (o es nueva). Pinta el "•" de la pestaña.</summary>
    [ObservableProperty] private bool _isModified;

    /// <summary>
    /// Esta pestaña tal como estaba en el archivo al abrirlo o guardarlo, normalizada (ver
    /// <see cref="BoardStore.ReadSnapshot"/>), o null si nunca se guardó — una pestaña nueva
    /// ES un cambio sin guardar, así que null cuenta como modificada. Es solo para la marca
    /// de la pestaña: el aviso de cierre compara el DOCUMENTO entero, que además ve pestañas
    /// agregadas y quitadas.
    /// </summary>
    public string? SavedSnapshot { get; set; }

    public BoardTab(string name)
    {
        _name = name;
        View = new BoardView(Board);
    }

    /// <summary>
    /// Nombre por defecto de una pestaña nueva: "Board N" con el MENOR número libre. No "cantidad
    /// + 1": con Board 1, 2 y 3, cerrar la 2 y abrir otra daría "Board 3" REPETIDO. El menor
    /// libre nunca repite un nombre por defecto (sí puede repetir uno que el usuario tipeó).
    /// </summary>
    public static string NextDefaultName(IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = 1;
        while (used.Contains($"Board {n}")) n++;
        return $"Board {n}";
    }

    /// <summary>La pestaña como se guarda en el archivo.</summary>
    public TabData ToData() => new(Name, Board.Root, Board.MasterVolume);

    public void RefreshModified() =>
        IsModified = SavedSnapshot is null || BoardStore.SerializeTab(ToData()) != SavedSnapshot;

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
