using System.Windows;
using System.Windows.Input;
using AmpzMediaBoard.Board;
using AmpzMediaBoard.Layout;
using AmpzMediaBoard.Persistence;

namespace AmpzMediaBoard;

public partial class MainWindow : Window
{
    private readonly BoardViewModel _board = new();

    public MainWindow()
    {
        InitializeComponent();

        // Si hay un board guardado, se restaura; si no (o si el JSON está roto), arrancamos con
        // el sector único con el que nace BoardViewModel. Nunca se bloquea el arranque por esto.
        if (BoardStore.Load() is { } saved) _board.ReplaceRoot(saved);

        BoardHost.Content = new BoardView(_board);

        SplitVButton.Click += (_, _) => SplitSelected(SplitOrientation.Horizontal);
        SplitHButton.Click += (_, _) => SplitSelected(SplitOrientation.Vertical);
        SaveButton.Click += (_, _) => _board.Save();
        ResetButton.Click += (_, _) => _board.ReplaceRoot(new SectorNode());

        // PreviewKeyDown y no KeyDown: si el foco quedó en un botón, el KeyDown de Espacio lo
        // consume el botón (lo interpreta como "apretarme") y el atajo nunca llega acá.
        PreviewKeyDown += OnPreviewKeyDown;

        // Guardado automático al cerrar. El botón "Guardar board" queda igual para el que quiere
        // asegurar el estado sin cerrar; pero perder el layout por cerrar sin guardar sería
        // exactamente la clase de fricción que esta app viene a sacar del medio.
        Closing += (_, _) =>
        {
            _board.Save();
            _board.Dispose();
        };
    }

    private void SplitSelected(SplitOrientation orientation)
    {
        if (_board.Selected is { } sector) _board.Split(sector, orientation);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            _board.Save();
            e.Handled = true;
            return;
        }

        if (_board.Selected is not { } sector) return;

        switch (e.Key)
        {
            case Key.Space:
                sector.TogglePlay();
                e.Handled = true;
                break;

            // A y B fijan los markers DONDE ESTÁ EL PLAYHEAD. Es el flujo real de marcar una
            // zona: mirás el clip, y en el momento exacto apretás la tecla. Buscar el frame
            // arrastrando el marker a ojo es mucho más lento y mucho menos preciso.
            case Key.A when sector.DurationMs > 0:
                sector.LoopStartMs = Math.Min(sector.PositionMs, sector.LoopEndMs - 120);
                e.Handled = true;
                break;

            case Key.B when sector.DurationMs > 0:
                sector.LoopEndMs = Math.Max(sector.PositionMs, sector.LoopStartMs + 120);
                e.Handled = true;
                break;

            case Key.L:
                sector.LoopEnabled = !sector.LoopEnabled;
                e.Handled = true;
                break;
        }
    }
}
