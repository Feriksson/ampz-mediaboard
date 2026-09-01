using System.Reflection;
using System.Windows;
using System.Windows.Input;
using AmpzMediaBoard.Board;
using AmpzMediaBoard.Layout;
using AmpzMediaBoard.Media;
using AmpzMediaBoard.Persistence;

namespace AmpzMediaBoard;

public partial class MainWindow : Window
{
    private readonly BoardViewModel _board = new();

    /// <summary>Archivo `.mboard` abierto, o null si el board todavía no se guardó en ninguno.</summary>
    private string? _currentFile;

    /// <param name="startupFile">
    /// Board a abrir al arrancar. Viene del doble click en Explorer (el shell nos pasa el path
    /// como argumento). Si es null, la app arranca con un board VACÍO.
    ///
    /// ⚠ NO se restaura ninguna sesión anterior, y es deliberado. Ver la sección
    /// "Arranque limpio" del CLAUDE.md antes de agregar nada parecido.
    /// </param>
    public MainWindow(string? startupFile = null)
    {
        InitializeComponent();

        if (startupFile is not null) OpenFile(startupFile);

        BoardHost.Content = new BoardView(_board);
        ShowVersion();
        UpdateTitle();

        // El botón de asociar solo aparece si hace falta: una acción que se hace una vez en la
        // vida no merece ocupar espacio permanente en la barra.
        AssociateButton.Visibility = BoardFile.IsRegistered() ? Visibility.Collapsed : Visibility.Visible;

        NewButton.Click += (_, _) => NewBoard();
        OpenButton.Click += (_, _) => OpenWithDialog();
        SaveButton.Click += (_, _) => Save();
        SaveAsButton.Click += (_, _) => _ = SaveAs();
        DistributeButton.Click += (_, _) => _board.Distribute();
        AssociateButton.Click += (_, _) => Associate();

        // PreviewKeyDown y no KeyDown: si el foco quedó en un botón, el KeyDown de Espacio lo
        // consume el botón (lo interpreta como "apretarme") y el atajo nunca llega acá.
        PreviewKeyDown += OnPreviewKeyDown;

        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges())
        {
            e.Cancel = true;
            return;
        }

        // No se escribe NINGÚN estado al cerrar. El único lugar donde vive un board es su
        // archivo .mboard, y ahí se escribe cuando el usuario lo pide.
        _board.Dispose();
    }

    /// <summary>
    /// Pregunta antes de perder trabajo. Devuelve false si el usuario decidió NO cerrar.
    ///
    /// Cubre los DOS casos, y cubrir el segundo es lo que hace seguro no tener sesión:
    ///  · board con archivo → se compara contra el archivo y se avisa si difiere;
    ///  · board SIN archivo → si tiene algo adentro, se avisa que nunca se guardó.
    ///
    /// Sin el segundo caso, sacar la restauración de sesión habría cambiado "te restaura cosas
    /// que no pediste" por "te pierde cosas sin avisar", que es estrictamente peor.
    /// </summary>
    private bool ConfirmDiscardChanges()
    {
        if (_currentFile is null)
        {
            // Un board vacío no tiene nada que perder: preguntar sería puro ruido, y un aviso
            // que salta cuando no hace falta es un aviso que el usuario aprende a ignorar.
            if (IsBoardEmpty()) return true;

            return MessageBox.Show(
                "Este board no está guardado en ningún archivo.\n\n¿Guardarlo antes de cerrar?",
                "Ampz MediaBoard", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
            {
                MessageBoxResult.Yes => SaveAs(),
                MessageBoxResult.No => true,
                _ => false,
            };
        }

        if (BoardStore.MatchesFile(_currentFile, _board.Root)) return true;

        return MessageBox.Show(
            $"El board \"{Path.GetFileNameWithoutExtension(_currentFile)}\" tiene cambios sin guardar.\n\n¿Guardarlos antes de cerrar?",
            "Ampz MediaBoard", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => BoardStore.SaveTo(_currentFile, _board.Root) is null,
            MessageBoxResult.No => true,
            _ => false, // Cancelar: se queda abierto.
        };
    }

    /// <summary>
    /// Board recién nacido: un solo sector, sin nada adentro. Si el usuario partió la pantalla
    /// o cargó un clip, eso YA es trabajo y merece el aviso.
    /// </summary>
    private bool IsBoardEmpty() =>
        _board.Root is SectorNode { Kind: Media.MediaKind.None, MissingPath: null };

    #region Archivos de board

    private void NewBoard()
    {
        // Mismo guard que al cerrar: sin sesión de respaldo, descartar el board actual sin
        // avisar sería perder trabajo en silencio. Vale para "Nuevo" y para "Abrir".
        if (!ConfirmDiscardChanges()) return;

        _board.ReplaceRoot(new SectorNode());
        _currentFile = null;
        UpdateTitle();
    }

    private void OpenWithDialog()
    {
        if (!ConfirmDiscardChanges()) return;

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

    /// <summary>
    /// Guarda en el archivo actual; si todavía no hay ninguno, se comporta como "Guardar como".
    /// Devuelve true si el board quedó guardado — el aviso de cierre depende de ese dato para
    /// saber si puede dejar cerrar la ventana.
    /// </summary>
    private bool Save() => _currentFile is null ? SaveAs() : Write(_currentFile);

    /// <summary>Devuelve false si el usuario canceló el diálogo o si el guardado falló.</summary>
    private bool SaveAs()
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

        if (dialog.ShowDialog() != true) return false;

        return Write(BoardFile.EnsureExtension(dialog.FileName));
    }

    private bool Write(string path)
    {
        var error = BoardStore.SaveTo(path, _board.Root);
        if (error is not null)
        {
            // Un "guardar" que falla en silencio es la peor mentira que le podés decir al usuario:
            // se va tranquilo creyendo que su trabajo está a salvo.
            MessageBox.Show($"No se pudo guardar el board:\n\n{error}",
                "Ampz MediaBoard", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        _currentFile = path;
        UpdateTitle();
        return true;
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

    /// <summary>
    /// Carga en el sector seleccionado el archivo que haya en el portapapeles.
    ///
    /// Acepta las DOS formas en que un path llega al portapapeles, porque el usuario no piensa
    /// en cuál es: copiar el archivo en el Explorer (Ctrl+C, que deja una LISTA DE ARCHIVOS) o
    /// copiar la ruta como texto ("Copiar como ruta de acceso" de Windows, o pegada de cualquier
    /// lado). Soportar solo una de las dos haría que la feature funcione día por medio.
    /// </summary>
    private void PasteMediaPath()
    {
        if (_board.Selected is not { } sector) return;

        string? path = null;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                path = Clipboard.GetFileDropList().Cast<string?>().FirstOrDefault(
                    p => p is not null && MediaKinds.IsSupported(p));
            }
            else if (Clipboard.ContainsText())
            {
                // "Copiar como ruta de acceso" de Windows envuelve el path en comillas.
                var texto = Clipboard.GetText().Trim().Trim('"');
                if (MediaKinds.IsSupported(texto)) path = texto;
            }
        }
        catch
        {
            // El portapapeles es un recurso compartido de todo el sistema: otra app puede
            // tenerlo tomado justo en este instante. Que falle no puede voltear la app.
            return;
        }

        if (path is null)
        {
            MessageBox.Show(
                "En el portapapeles no hay ningún archivo de media soportado.\n\n"
                + "Copiá el archivo desde el Explorer, o copiá su ruta como texto.",
                "Ampz MediaBoard", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!File.Exists(path))
        {
            MessageBox.Show($"El archivo no existe:\n\n{path}",
                "Ampz MediaBoard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        sector.Adopt(path);
    }

    /// <summary>
    /// El título dice SIEMPRE sobre qué archivo estás trabajando — o que todavía no hay ninguno.
    ///
    /// ⚠ El nombre del board va PRIMERO y la app se abrevia a "AMB". No es capricho estético:
    /// el botón de la barra de tareas de Windows trunca por la DERECHA y da lugar a unos pocos
    /// caracteres. Con "Ampz MediaBoard — " adelante (18 caracteres antes de empezar a decir
    /// algo), el nombre del board se cortaba SIEMPRE y todas las ventanas se veían idénticas
    /// — que es justo cuando más lo necesitás, con varios boards abiertos a la vez (la app es
    /// multi-instancia a propósito). Lo que identifica va primero; la marca, al final, donde
    /// puede perderse sin costo.
    /// </summary>
    private void UpdateTitle() =>
        Title = _currentFile is null
            ? "Board sin guardar — AMB"
            : $"{Path.GetFileNameWithoutExtension(_currentFile)} — AMB";

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
        var texto = version is null ? string.Empty : $"v{version.Major}.{version.Minor}.{version.Build}";

        // ⚠ El sufijo "dev" NO es decoración: con la cadencia de release actual el trabajo en
        // develop NO mueve la versión, así que el build de Debug y el Release publicado muestran
        // el MISMO número. Sin esta marca es imposible saber a ojo cuál de los dos estás usando
        // — y eso ya costó una sesión entera de debug persiguiendo una feature que sí existía,
        // pero en el otro binario.
#if DEBUG
        texto += "  dev";
#endif

        VersionLabel.Text = texto;
    }

    #region Pantalla completa

    /// <summary>
    /// Cómo estaba la ventana antes de irse a pantalla completa. Se guarda el trío entero porque
    /// volver "a lo que había" es parte del contrato de F11: si al salir te dejara la ventana
    /// maximizada cuando la tenías a medio monitor, el atajo dejaría de ser un toggle y pasaría a
    /// ser un cambio de layout permanente.
    /// </summary>
    private WindowStyle _styleAntesDePantallaCompleta;
    private WindowState _stateAntesDePantallaCompleta;
    private ResizeMode _resizeAntesDePantallaCompleta;

    private bool _pantallaCompleta;

    private void TogglePantallaCompleta()
    {
        if (_pantallaCompleta) SalirDePantallaCompleta();
        else EntrarEnPantallaCompleta();
    }

    private void EntrarEnPantallaCompleta()
    {
        if (_pantallaCompleta) return;

        _styleAntesDePantallaCompleta = WindowStyle;
        _stateAntesDePantallaCompleta = WindowState;
        _resizeAntesDePantallaCompleta = ResizeMode;

        // ⚠ El paso por Normal ANTES de Maximized NO es redundante. Si la ventana YA estaba
        // maximizada, cambiarle el WindowStyle no la re-maximiza: Windows le deja el rectángulo
        // que ya tenía, que es el ÁREA DE TRABAJO — o sea, con la barra de tareas encima del
        // board. Se ve como "casi pantalla completa" y es de esos bugs que uno atribuye al
        // monitor. Re-maximizar desde Normal recalcula el rect contra la pantalla COMPLETA.
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;

        TopBar.Visibility = Visibility.Collapsed;
        _pantallaCompleta = true;
    }

    private void SalirDePantallaCompleta()
    {
        if (!_pantallaCompleta) return;

        // El orden inverso al de entrada, por el mismo motivo: se restaura el estilo con la
        // ventana en Normal y recién después el estado que tenía.
        WindowState = WindowState.Normal;
        WindowStyle = _styleAntesDePantallaCompleta;
        ResizeMode = _resizeAntesDePantallaCompleta;
        WindowState = _stateAntesDePantallaCompleta;

        TopBar.Visibility = Visibility.Visible;
        _pantallaCompleta = false;
    }

    #endregion

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
                case Key.V:
                    PasteMediaPath();
                    e.Handled = true;
                    return;

                // Reparte el espacio en partes iguales. NO pasa por el guard de "hay un sector
                // seleccionado" que está más abajo: es una acción sobre el board ENTERO, y un
                // board recién abierto puede no tener ninguna celda activa.
                case Key.E:
                    _board.Distribute();
                    e.Handled = true;
                    return;
            }
        }

        // ⚠ F11 y Esc van ANTES del guard de "hay un sector seleccionado". La pantalla completa no
        // tiene NADA que ver con qué sector está seleccionado, y un board recién abierto puede no
        // tener ninguno: colgarla del guard haría que el atajo funcione día por medio.
        switch (e.Key)
        {
            case Key.F11:
                TogglePantallaCompleta();
                e.Handled = true;
                return;

            // Esc SALE de pantalla completa, pero solo si estás en pantalla completa. Es la
            // convención universal (y sin bordes ni barra, la salida a mano no es evidente).
            // Marcarlo como Handled fuera de ese caso sería robarle el Esc a cualquier otra cosa.
            case Key.Escape when _pantallaCompleta:
                SalirDePantallaCompleta();
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
