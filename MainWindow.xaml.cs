using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AmpzMediaBoard.Board;
using AmpzMediaBoard.Layout;
using AmpzMediaBoard.Media;
using AmpzMediaBoard.Persistence;

namespace AmpzMediaBoard;

public partial class MainWindow : Window
{
    /// <summary>
    /// Las pestañas abiertas, en el orden de la tira. SIEMPRE hay al menos una: cerrar la última
    /// deja una vacía, igual que cerrar el último sector lo vacía en vez de eliminarlo — la
    /// ventana nunca queda sin board.
    /// </summary>
    private readonly ObservableCollection<BoardTab> _tabs = [];

    /// <summary>La pestaña visible. Todos los atajos y botones de la barra actúan sobre ella.</summary>
    private BoardTab _active = null!;

    private BoardViewModel Board => _active.Board;

    /// <summary>
    /// Refresca la marca "•" de la pestaña activa. Por polling y no por eventos por la misma
    /// razón por la que el aviso de cierre no usa un flag "dirty" (ver BoardStore): observar cada
    /// mutación posible es garantía de que alguna se escapa. Serializar un board de 8 sectores
    /// cada medio segundo cuesta microsegundos. Solo la ACTIVA: las demás están suspendidas y
    /// no pueden cambiar.
    /// </summary>
    private readonly DispatcherTimer _modifiedRefresh;

    /// <param name="startupFiles">
    /// Boards a abrir al arrancar, cada uno en su pestaña; el primero queda visible. Vienen del
    /// doble click en Explorer (el shell nos pasa el path como argumento). Si no hay ninguno, la
    /// app arranca con un board VACÍO.
    ///
    /// ⚠ NO se restaura ninguna sesión anterior, y es deliberado. Ver la sección
    /// "Arranque limpio" del CLAUDE.md antes de agregar nada parecido.
    /// </param>
    public MainWindow(IReadOnlyList<string>? startupFiles = null)
    {
        InitializeComponent();

        TabList.ItemsSource = _tabs;
        SwitchTo(AddTab());

        // El primero llena la pestaña inicial (está en blanco, OpenPath la reutiliza); el resto
        // abre en segundo plano, con sus clips esperando a que se muestre su pestaña.
        var first = true;
        foreach (var path in startupFiles ?? [])
        {
            OpenPath(path, activate: first);
            first = false;
        }

        ShowVersion();
        UpdateTitle();

        // El botón de asociar solo aparece si hace falta: una acción que se hace una vez en la
        // vida no merece ocupar espacio permanente en la barra.
        AssociateButton.Visibility = BoardFile.IsRegistered() ? Visibility.Collapsed : Visibility.Visible;

        NewButton.Click += (_, _) => NewTab();
        NewTabButton.Click += (_, _) => NewTab();
        OpenButton.Click += (_, _) => OpenWithDialog();
        SaveButton.Click += (_, _) => Save(_active);
        SaveAsButton.Click += (_, _) => _ = SaveAs(_active);
        DistributeButton.Click += (_, _) => Board.Distribute();
        AssociateButton.Click += (_, _) => Associate();

        // La tira desborda desplazándose: la rueda del mouse la mueve en horizontal (la barra
        // de scroll está oculta, ver el XAML).
        TabScroller.PreviewMouseWheel += (_, e) =>
        {
            TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        };

        _modifiedRefresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _modifiedRefresh.Tick += (_, _) => _active.RefreshModified();
        _modifiedRefresh.Start();

        // PreviewKeyDown y no KeyDown: si el foco quedó en un botón, el KeyDown de Espacio lo
        // consume el botón (lo interpreta como "apretarme") y el atajo nunca llega acá.
        PreviewKeyDown += OnPreviewKeyDown;

        Closing += OnClosing;
    }

    #region Pestañas

    /// <summary>
    /// Crea una pestaña con un board vacío, en SEGUNDO PLANO. Quien la quiera visible llama a
    /// <see cref="SwitchTo"/>. Nacer suspendida no es un detalle: si se le carga un board antes
    /// de mostrarla (abrir varios por línea de comandos), sus clips quedan pendientes hasta que
    /// la pestaña tenga una superficie visible donde dibujar.
    /// </summary>
    private BoardTab AddTab()
    {
        var tab = new BoardTab();
        tab.Deactivate();
        tab.PropertyChanged += OnTabPropertyChanged;
        _tabs.Add(tab);
        BoardHost.Children.Add(tab.View);
        return tab;
    }

    private void NewTab() => SwitchTo(AddTab());

    /// <summary>
    /// Cambia de pestaña. Tiene que sentirse INSTANTÁNEO, y lo es porque no reconstruye nada:
    /// la saliente se pausa y se colapsa, la entrante se hace visible y reanuda lo que estaba
    /// reproduciendo. Ningún archivo se reabre. Ver BoardTab.Activate/Deactivate.
    /// </summary>
    private void SwitchTo(BoardTab tab)
    {
        if (ReferenceEquals(_active, tab)) return;

        var clock = DiagLog.Enabled ? System.Diagnostics.Stopwatch.StartNew() : null;

        // La saliente PRIMERO: así nunca hay dos pestañas decodificando a la vez, ni siquiera
        // durante el cambio.
        var previous = _active;
        _active = tab;
        if (previous is not null)
        {
            previous.RefreshModified();
            previous.Deactivate();
        }

        tab.Activate();
        tab.RefreshModified();
        UpdateTitle();

        // Con la tira desbordada, la pestaña activa puede estar fuera de la vista (Ctrl+Tab
        // hacia una del final). Se trae a la vista después del layout, cuando ya tiene posición.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            (TabList.ItemContainerGenerator.ContainerFromItem(tab) as FrameworkElement)?.BringIntoView();

            // Prioridad Loaded corre DESPUÉS de las de Render: para acá el layout de la pestaña
            // nueva ya se hizo. Es el costo del cambio tal como lo paga el hilo de UI. Ver DiagLog.
            if (clock is not null) DiagLog.Write($"switch {clock.ElapsedMilliseconds}");
        });
    }

    /// <summary>
    /// Cierra una pestaña, con el aviso de cambios sin guardar de ESA pestaña. Si era la última,
    /// queda una vacía en su lugar.
    /// </summary>
    private void CloseTab(BoardTab tab)
    {
        if (!ConfirmDiscardChanges(tab)) return;

        if (_tabs.Count == 1)
        {
            NewTab();
        }
        else if (ReferenceEquals(tab, _active))
        {
            // La de la derecha, o la de la izquierda si era la última: como un navegador.
            var i = _tabs.IndexOf(tab);
            SwitchTo(_tabs[i + 1 < _tabs.Count ? i + 1 : i - 1]);
        }

        _tabs.Remove(tab);
        tab.PropertyChanged -= OnTabPropertyChanged;

        // Liberar ANTES de sacar la vista del árbol: el board desengancha cada player de su
        // VideoView y los detiene en paralelo (VlcEngine.Release); recién después se destruyen
        // las ventanas nativas. Mismo orden que ReplaceRoot → Rebuild.
        tab.Dispose();
        BoardHost.Children.Remove(tab.View);
    }

    private void CycleTab(int step)
    {
        var i = _tabs.IndexOf(_active);
        SwitchTo(_tabs[((i + step) % _tabs.Count + _tabs.Count) % _tabs.Count]);
    }

    /// <summary>Ctrl+1..8 van a esa pestaña; Ctrl+9 a la ÚLTIMA, como en cualquier navegador.</summary>
    private void JumpToTab(int number)
    {
        if (number == 9) SwitchTo(_tabs[^1]);
        else if (number <= _tabs.Count) SwitchTo(_tabs[number - 1]);
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _active) && e.PropertyName == nameof(BoardTab.WindowTitle)) UpdateTitle();
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BoardTab tab) SwitchTo(tab);
    }

    private void OnTabCloseClick(object sender, RoutedEventArgs e)
    {
        // Handled: el Click del × BURBUJEA hasta el botón de la pestaña, que lo tomaría como
        // "activar" una pestaña que acabamos de cerrar.
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is BoardTab tab) CloseTab(tab);
    }

    /// <summary>Click del medio sobre una pestaña = cerrarla, como en cualquier navegador.</summary>
    private void OnTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is BoardTab tab) CloseTab(tab);
    }

    #endregion

    /// <summary>
    /// Segunda pasada del cierre: los reproductores ya se detuvieron (o se venció la espera) y
    /// la ventana se puede cerrar de verdad. Ver <see cref="BeginFastClose"/>.
    /// </summary>
    private bool _playersReleased;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_playersReleased) return;

        // Se pregunta por CADA pestaña con cambios (ConfirmDiscardChanges la trae al frente
        // antes de preguntar, para que se vea de qué board se habla). Cancelar en cualquiera
        // aborta el cierre entero: el usuario dijo que no quería irse.
        foreach (var tab in _tabs.ToList())
        {
            if (ConfirmDiscardChanges(tab)) continue;
            e.Cancel = true;
            return;
        }

        // No se escribe NINGÚN estado al cerrar. El único lugar donde vive un board es su
        // archivo .mboard, y ahí se escribe cuando el usuario lo pide.
        //
        // El cierre de verdad se POSTERGA una vuelta: WPF no deja esconder una ventana desde
        // adentro de su propio Closing (tira InvalidOperationException), y esconderla es
        // justamente lo primero que hay que hacer. Ver BeginFastClose.
        e.Cancel = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Send, BeginFastClose);
    }

    /// <summary>
    /// Cierre rápido: la ventana desaparece YA y los reproductores se detienen en paralelo por
    /// detrás.
    ///
    /// Bug reportado: con varios videos el cierre tardaba segundos y se veía cerrar "video por
    /// video". Cada <c>MediaPlayer.Stop()</c> de libvlc bloquea ~100-500ms y se hacían en fila en
    /// el hilo de UI, con la ventana visible y congelada. Ahora el Stop corre en otros hilos
    /// (VlcEngine.Release) y el que mira solo ve la ventana irse.
    ///
    /// Con pestañas es el MISMO camino, estirado: se sueltan los boards de TODAS, y todos sus
    /// players se detienen en paralelo — el cierre sigue costando lo del Stop más lento, no la
    /// suma de todas las pestañas.
    ///
    /// ⚠ Se ESCONDE y no se cierra hasta que terminaron los Stop. No es cosmético: cerrar
    /// destruye los HWND de los VideoView, y un vout de VLC que todavía no se detuvo quedaría
    /// dibujando sobre una ventana hija de una ventana muerta. Escondida, la ventana nativa
    /// sigue viva hasta que el último player soltó su salida. Mismo invariante que
    /// "desenganchar antes de liberar", estirado a todo el cierre.
    ///
    /// La espera tiene techo (<see cref="VlcEngine.ReleaseTimeout"/>): un VLC trabado no puede
    /// dejar un proceso invisible vivo para siempre.
    /// </summary>
    private async void BeginFastClose()
    {
        try
        {
            Hide();
            _modifiedRefresh.Stop();
            foreach (var tab in _tabs) tab.Board.Dispose();
            await Task.WhenAny(VlcEngine.WhenReleased(), Task.Delay(VlcEngine.ReleaseTimeout));
        }
        finally
        {
            // En el finally: pase lo que pase, la app se termina cerrando. Una excepción acá
            // dejaría un proceso escondido que el usuario ni ve para matarlo.
            _playersReleased = true;
            Close();
        }
    }

    /// <summary>
    /// ¿Cerrar esta pestaña perdería trabajo?
    ///  · board con archivo → se compara contra el archivo;
    ///  · board SIN archivo → si tiene algo adentro, nunca se guardó.
    /// Un board vacío sin archivo no tiene nada que perder: preguntar ahí sería puro ruido, y un
    /// aviso que salta cuando no hace falta es un aviso que el usuario aprende a ignorar.
    /// </summary>
    private static bool HasUnsavedWork(BoardTab tab) =>
        tab.FilePath is null ? !tab.IsEmpty : !BoardStore.MatchesFile(tab.FilePath, tab.Board.Root);

    /// <summary>
    /// Pregunta antes de perder trabajo de UNA pestaña. Devuelve false si el usuario decidió NO
    /// cerrar.
    ///
    /// Cubre los DOS casos (con y sin archivo), y cubrir el segundo es lo que hace seguro no
    /// tener sesión: sin él, sacar la restauración de sesión habría cambiado "te restaura cosas
    /// que no pediste" por "te pierde cosas sin avisar", que es estrictamente peor.
    ///
    /// Si hay que preguntar, la pestaña se trae al frente ANTES: con varias abiertas, un
    /// "¿guardar los cambios?" sobre un board que no estás viendo es una pregunta a ciegas.
    /// </summary>
    private bool ConfirmDiscardChanges(BoardTab tab)
    {
        if (!HasUnsavedWork(tab)) return true;

        SwitchTo(tab);

        if (tab.FilePath is not { } file)
        {
            return MessageBox.Show(
                "Este board no está guardado en ningún archivo.\n\n¿Guardarlo antes de cerrar?",
                "Ampz MediaBoard", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
            {
                MessageBoxResult.Yes => SaveAs(tab),
                MessageBoxResult.No => true,
                _ => false,
            };
        }

        return MessageBox.Show(
            $"El board \"{Path.GetFileNameWithoutExtension(file)}\" tiene cambios sin guardar.\n\n¿Guardarlos antes de cerrar?",
            "Ampz MediaBoard", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => Write(tab, file),
            MessageBoxResult.No => true,
            _ => false, // Cancelar: se queda abierto.
        };
    }

    #region Archivos de board

    private void OpenWithDialog()
    {
        // Sin aviso de cambios sin guardar: "Abrir" ya no reemplaza el board actual, abre en
        // una pestaña. No hay nada que se pierda.
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Abrir board",
            Filter = BoardFile.DialogFilter,
            DefaultExt = BoardFile.Extension,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true) OpenPath(dialog.FileName);
    }

    /// <summary>
    /// Abre un board en una pestaña, con dos excepciones que evitan pestañas de más:
    ///  · si ese archivo YA está abierto, va a su pestaña (dos pestañas del mismo archivo se
    ///    pisarían al guardar);
    ///  · si la activa está en blanco (sin archivo y vacía), la reutiliza.
    /// </summary>
    private void OpenPath(string path, bool activate = true)
    {
        var full = Path.GetFullPath(path);
        var existing = _tabs.FirstOrDefault(t =>
            t.FilePath is { } f && string.Equals(Path.GetFullPath(f), full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (activate) SwitchTo(existing);
            return;
        }

        // Se lee ANTES de elegir pestaña: si el archivo está corrupto no queda una pestaña
        // vacía creada de más.
        var root = BoardStore.LoadFrom(path);
        if (root is null)
        {
            MessageBox.Show(
                $"No se pudo leer el board:\n\n{path}\n\nEl archivo puede estar corrupto o no ser un .mboard válido.",
                "Ampz MediaBoard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var target = _active.IsBlank ? _active : AddTab();
        target.Open(root, full);
        if (activate) SwitchTo(target);
        UpdateTitle();
    }

    /// <summary>
    /// Guarda en el archivo de la pestaña; si todavía no hay ninguno, se comporta como "Guardar
    /// como". Devuelve true si el board quedó guardado — el aviso de cierre depende de ese dato
    /// para saber si puede dejar cerrar.
    /// </summary>
    private bool Save(BoardTab tab) => tab.FilePath is null ? SaveAs(tab) : Write(tab, tab.FilePath);

    /// <summary>Devuelve false si el usuario canceló el diálogo o si el guardado falló.</summary>
    private bool SaveAs(BoardTab tab)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Guardar board",
            Filter = BoardFile.DialogFilter,
            DefaultExt = BoardFile.Extension,
            AddExtension = true,
            FileName = tab.FilePath is not null
                ? Path.GetFileName(tab.FilePath)
                : "board" + BoardFile.Extension,
        };

        if (dialog.ShowDialog() != true) return false;

        return Write(tab, BoardFile.EnsureExtension(dialog.FileName));
    }

    private bool Write(BoardTab tab, string path)
    {
        var error = BoardStore.SaveTo(path, tab.Board.Root);
        if (error is not null)
        {
            // Un "guardar" que falla en silencio es la peor mentira que le podés decir al usuario:
            // se va tranquilo creyendo que su trabajo está a salvo.
            MessageBox.Show($"No se pudo guardar el board:\n\n{error}",
                "Ampz MediaBoard", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        tab.MarkSaved(path);
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
        if (Board.Selected is not { } sector) return;

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
    /// El título es el de la pestaña ACTIVA y dice SIEMPRE sobre qué archivo estás trabajando —
    /// o que todavía no hay ninguno. El formato lo arma <see cref="BoardTab.WindowTitle"/>:
    ///
    /// ⚠ El nombre del board va PRIMERO y la app se abrevia a "AMB". No es capricho estético:
    /// el botón de la barra de tareas de Windows trunca por la DERECHA y da lugar a unos pocos
    /// caracteres. Con "Ampz MediaBoard — " adelante (18 caracteres antes de empezar a decir
    /// algo), el nombre del board se cortaba SIEMPRE y todas las ventanas se veían idénticas
    /// — que es justo cuando más lo necesitás, con varios boards abiertos a la vez (la app es
    /// multi-instancia a propósito). Lo que identifica va primero; la marca, al final, donde
    /// puede perderse sin costo.
    /// </summary>
    private void UpdateTitle() => Title = _active.WindowTitle;

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

        // La tira de pestañas se va con la barra: pantalla completa es el BOARD, nada más.
        TopBar.Visibility = Visibility.Collapsed;
        TabStrip.Visibility = Visibility.Collapsed;
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
        TabStrip.Visibility = Visibility.Visible;
        _pantallaCompleta = false;
    }

    #endregion

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            switch (e.Key)
            {
                case Key.S when shift:
                    SaveAs(_active);
                    e.Handled = true;
                    return;
                case Key.S:
                    Save(_active);
                    e.Handled = true;
                    return;
                case Key.O:
                    OpenWithDialog();
                    e.Handled = true;
                    return;

                // "Nuevo" ya no pisa el board actual: abre una pestaña. Ctrl+T es el mismo gesto
                // con el atajo de cualquier navegador.
                case Key.N:
                case Key.T:
                    NewTab();
                    e.Handled = true;
                    return;
                case Key.W:
                    CloseTab(_active);
                    e.Handled = true;
                    return;
                case Key.Tab:
                    CycleTab(shift ? -1 : 1);
                    e.Handled = true;
                    return;
                case >= Key.D1 and <= Key.D9:
                    JumpToTab(e.Key - Key.D0);
                    e.Handled = true;
                    return;
                case >= Key.NumPad1 and <= Key.NumPad9:
                    JumpToTab(e.Key - Key.NumPad0);
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
                    Board.Distribute();
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

        if (Board.Selected is not { } sector) return;

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
