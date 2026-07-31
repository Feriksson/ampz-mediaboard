using System.Reflection;
using System.Windows;
using System.Windows.Input;
using AmpzMediaBoard.Board;
using AmpzMediaBoard.Layout;
using AmpzMediaBoard.Persistence;

namespace AmpzMediaBoard;

public partial class MainWindow : Window
{
    private readonly BoardViewModel _board = new();

    /// <summary>Archivo `.mboard` abierto, o null si el board todavía no se guardó en ninguno.</summary>
    private string? _currentFile;

    /// <param name="startupFile">
    /// Board a abrir al arrancar. Viene del doble click en Explorer (el shell nos pasa el path
    /// como argumento). Si es null, se restaura la sesión anterior.
    /// </param>
    public MainWindow(string? startupFile = null)
    {
        InitializeComponent();

        if (startupFile is not null) OpenFile(startupFile);
        else RestoreSession();

        BoardHost.Content = new BoardView(_board);
        ShowVersion();
        UpdateTitle();

        // El botón de asociar solo aparece si hace falta: una acción que se hace una vez en la
        // vida no merece ocupar espacio permanente en la barra.
        AssociateButton.Visibility = BoardFile.IsRegistered() ? Visibility.Collapsed : Visibility.Visible;

        NewButton.Click += (_, _) => NewBoard();
        OpenButton.Click += (_, _) => OpenWithDialog();
        SaveButton.Click += (_, _) => Save();
        SaveAsButton.Click += (_, _) => SaveAs();
        AssociateButton.Click += (_, _) => Associate();

        // PreviewKeyDown y no KeyDown: si el foco quedó en un botón, el KeyDown de Espacio lo
        // consume el botón (lo interpreta como "apretarme") y el atajo nunca llega acá.
        PreviewKeyDown += OnPreviewKeyDown;

        // Al cerrar se guarda la SESIÓN (no el archivo .mboard). Son cosas distintas: la sesión
        // te salva de perder trabajo, el archivo es tu documento y solo se escribe cuando vos lo
        // pedís. Autoguardar sobre el archivo sería pisarle cambios al usuario sin permiso.
        Closing += (_, _) =>
        {
            BoardStore.SaveSession(_board.Root, _currentFile);
            _board.Dispose();
        };
    }

    #region Archivos de board

    private void RestoreSession()
    {
        var session = BoardStore.LoadSession();
        if (session.Root is not null) _board.ReplaceRoot(session.Root);

        // El archivo recordado solo se adopta si TODAVÍA existe: mostrar en el título un board
        // que ya no está sería mentir sobre dónde va a escribir el próximo "Guardar".
        _currentFile = session.CurrentFile is not null && File.Exists(session.CurrentFile)
            ? session.CurrentFile
            : null;
    }

    private void NewBoard()
    {
        _board.ReplaceRoot(new SectorNode());
        _currentFile = null;
        UpdateTitle();
    }

    private void OpenWithDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Abrir board",
            Filter = BoardFile.DialogFilter,
            DefaultExt = BoardFile.Extension,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true) OpenFile(dialog.FileName);
    }

    private void OpenFile(string path)
    {
        var root = BoardStore.LoadFrom(path);
        if (root is null)
        {
            MessageBox.Show(
                $"No se pudo leer el board:\n\n{path}\n\nEl archivo puede estar corrupto o no ser un .mboard válido.",
                "Ampz MediaBoard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _board.ReplaceRoot(root);
        _currentFile = path;
        UpdateTitle();
    }

    /// <summary>Guarda en el archivo actual; si todavía no hay ninguno, se comporta como "Guardar como".</summary>
    private void Save()
    {
        if (_currentFile is null)
        {
            SaveAs();
            return;
        }

        Write(_currentFile);
    }

    private void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Guardar board",
            Filter = BoardFile.DialogFilter,
            DefaultExt = BoardFile.Extension,
            AddExtension = true,
            FileName = _currentFile is not null
                ? Path.GetFileName(_currentFile)
                : "board" + BoardFile.Extension,
        };

        if (dialog.ShowDialog() != true) return;

        Write(BoardFile.EnsureExtension(dialog.FileName));
    }

    private void Write(string path)
    {
        var error = BoardStore.SaveTo(path, _board.Root);
        if (error is not null)
        {
            // Un "guardar" que falla en silencio es la peor mentira que le podés decir al usuario:
            // se va tranquilo creyendo que su trabajo está a salvo.
            MessageBox.Show($"No se pudo guardar el board:\n\n{error}",
                "Ampz MediaBoard", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _currentFile = path;
        UpdateTitle();
    }

    private void Associate()
    {
        if (BoardFile.Register())
        {
            AssociateButton.Visibility = Visibility.Collapsed;
            MessageBox.Show(
                $"Listo. Los archivos {BoardFile.Extension} ahora abren con esta app al doble click.",
                "Ampz MediaBoard", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                "No se pudo registrar la extensión. Puede estar bloqueado por una política del sistema.",
                "Ampz MediaBoard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>El título dice SIEMPRE sobre qué archivo estás trabajando — o que todavía no hay ninguno.</summary>
    private void UpdateTitle() =>
        Title = _currentFile is null
            ? "Ampz MediaBoard — board sin guardar"
            : $"Ampz MediaBoard — {Path.GetFileNameWithoutExtension(_currentFile)}";

    #endregion

    /// <summary>
    /// Pinta la versión en la barra, LEÍDA DEL ASSEMBLY en runtime.
    ///
    /// El número no se escribe acá ni en el XAML a propósito: la única fuente de verdad es
    /// <c>&lt;Version&gt;</c> en el .csproj, de donde el SDK deriva la versión del assembly.
    /// Tipearlo en la UI crearía un segundo número que tarde o temprano deja de coincidir con el
    /// binario — y una versión que miente es peor que no mostrar ninguna.
    /// </summary>
    private void ShowVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionLabel.Text = version is null ? string.Empty : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.S when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                    SaveAs();
                    e.Handled = true;
                    return;
                case Key.S:
                    Save();
                    e.Handled = true;
                    return;
                case Key.O:
                    OpenWithDialog();
                    e.Handled = true;
                    return;
                case Key.N:
                    NewBoard();
                    e.Handled = true;
                    return;
            }
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
