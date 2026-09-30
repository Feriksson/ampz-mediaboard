using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
    /// Las pestañas del archivo abierto, en el orden de la tira. SIEMPRE hay al menos una: quitar
    /// la última deja una vacía, igual que cerrar el último sector lo vacía en vez de eliminarlo —
    /// la ventana nunca queda sin board.
    /// </summary>
    private readonly ObservableCollection<BoardTab> _tabs = [];

    /// <summary>La pestaña visible. Todos los atajos y botones de la barra actúan sobre ella.</summary>
    private BoardTab? _active;

    private BoardViewModel Board => _active!.Board;

    #region Documento: UN archivo con TODAS las pestañas

    /// <summary>
    /// El `.mboard` de la ventana, o null si todavía no se guardó en ninguno. Es del DOCUMENTO y
    /// no de cada pestaña: el archivo guarda todas las pestañas juntas (decisión del usuario:
    /// un archivo por pestaña era el modelo equivocado). Dos archivos a la vez = dos ventanas.
    /// </summary>
    private string? _filePath;

    /// <summary>
    /// El documento como estaba al abrirlo o guardarlo, normalizado y SIN la pestaña activa (ver
    /// <see cref="BoardStore.ReadSnapshot"/>). Sin archivo, es el documento EN BLANCO (una
    /// pestaña vacía con el nombre por defecto): así "¿hay algo que perder?" es la MISMA
    /// comparación con y sin archivo. Contra esto se pinta el "•" del título.
    /// </summary>
    private string _savedSnapshot = string.Empty;

    private bool _isModified;

    /// <summary>
    /// Refresca las marcas "•" (título y pestañas). Por polling y no por eventos por la misma
    /// razón por la que el aviso de cierre no usa un flag "dirty" (ver BoardStore): observar cada
    /// mutación posible es garantía de que alguna se escapa. Serializar unos boards de 8 sectores
    /// cada medio segundo cuesta microsegundos.
    /// </summary>
    private readonly DispatcherTimer _modifiedRefresh;

    private List<TabData> CurrentTabs() => _tabs.Select(t => t.ToData()).ToList();

    /// <param name="startupFile">
    /// El archivo a abrir al arrancar: viene del doble click en Explorer (el shell nos pasa el
    /// path como argumento). Si no hay ninguno, la app arranca con un documento VACÍO.
    ///
    /// ⚠ NO se restaura ninguna sesión anterior, y es deliberado. Ver la sección
    /// "Arranque limpio" del CLAUDE.md antes de agregar nada parecido.
    /// </param>
    public MainWindow(string? startupFile = null)
    {
        InitializeComponent();

        TabList.ItemsSource = _tabs;
        LoadBlankDocument();
        if (startupFile is not null) OpenPath(startupFile);

        ShowVersion();

        // El botón de asociar solo aparece si hace falta: una acción que se hace una vez en la
        // vida no merece ocupar espacio permanente en la barra.
        AssociateButton.Visibility = BoardFile.IsRegistered() ? Visibility.Collapsed : Visibility.Visible;

        NewButton.Click += (_, _) => NewDocument();
        NewTabButton.Click += (_, _) => AddTabToDocument();
        OpenButton.Click += (_, _) => OpenWithDialog();
        SaveButton.Click += (_, _) => Save();
        SaveAsButton.Click += (_, _) => SaveAs();
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
        _modifiedRefresh.Tick += (_, _) => RefreshModified();
        _modifiedRefresh.Start();

        // PreviewKeyDown y no KeyDown: si el foco quedó en un botón, el KeyDown de Espacio lo
        // consume el botón (lo interpreta como "apretarme") y el atajo nunca llega acá.
        PreviewKeyDown += OnPreviewKeyDown;

        // Confirmar el renombre con un click en CUALQUIER otro lado, o al irse a otra ventana.
        // Ver OnWindowPreviewMouseDown: el LostKeyboardFocus de la caja NO alcanza.
        PreviewMouseDown += OnWindowPreviewMouseDown;
        Deactivated += (_, _) => CommitPendingRename();

        Closing += OnClosing;
    }

    /// <summary>
    /// Reemplaza TODAS las pestañas por las de un documento. Lo usan "Nuevo", "Abrir" y el
    /// arranque. Las pestañas viejas se liberan por el camino que no bloquea (cada player se
    /// detiene en otro hilo, VlcEngine.Release).
    ///
    /// Las nuevas nacen TODAS en segundo plano y recién después se muestra la activa: sus clips
    /// quedan pendientes y arrancan cuando su pestaña tiene una superficie visible. Abrir un
    /// archivo de ocho pestañas pone a decodificar UNA, no ocho.
    /// </summary>
    private void ReplaceDocument(IReadOnlyList<TabData> tabs, int activeTab)
    {
        foreach (var old in _tabs.ToList()) RemoveTabView(old);
        _tabs.Clear();
        _active = null;

        foreach (var data in tabs)
        {
            var tab = AddTab(data.Name);
            tab.Board.ReplaceRoot(data.Root);
            tab.Board.MasterVolume = data.MasterVolume;
        }

        SwitchTo(_tabs[Math.Clamp(activeTab, 0, _tabs.Count - 1)]);
    }

    /// <summary>Documento en blanco: sin archivo, una pestaña vacía con el nombre por defecto.</summary>
    private void LoadBlankDocument()
    {
        foreach (var old in _tabs.ToList()) RemoveTabView(old);
        _tabs.Clear();
        _active = null;
        SwitchTo(AddTab(BoardTab.NextDefaultName([])));

        _filePath = null;
        _savedSnapshot = BoardStore.Serialize(CurrentTabs());
        foreach (var tab in _tabs) tab.SavedSnapshot = BoardStore.SerializeTab(tab.ToData());
        RefreshModified();
    }

    /// <summary>
    /// El documento quedó igual a <paramref name="path"/> (recién abierto o recién guardado): ese
    /// pasa a ser su archivo y la foto contra la que se miden los cambios. La foto se toma
    /// LEYENDO el archivo (no serializando la memoria): la regla es comparar contra el archivo.
    /// </summary>
    private void MarkSaved(string path)
    {
        _filePath = path;
        var snapshot = BoardStore.ReadSnapshot(path);
        _savedSnapshot = snapshot?.Document ?? BoardStore.Serialize(CurrentTabs());
        for (var i = 0; i < _tabs.Count; i++)
            _tabs[i].SavedSnapshot = snapshot is not null && i < snapshot.Tabs.Count ? snapshot.Tabs[i] : null;
        RefreshModified();
    }

    private void RefreshModified()
    {
        foreach (var tab in _tabs) tab.RefreshModified();
        _isModified = BoardStore.Serialize(CurrentTabs()) != _savedSnapshot;
        UpdateTitle();
    }

    /// <summary>
    /// ¿Cerrar / "Nuevo" / "Abrir" perdería trabajo?
    ///  · documento con archivo → se compara contra el archivo EN DISCO;
    ///  · documento sin archivo → si difiere del documento en blanco, nunca se guardó.
    /// Un documento en blanco no tiene nada que perder: preguntar ahí sería puro ruido, y un
    /// aviso que salta cuando no hace falta es un aviso que el usuario aprende a ignorar.
    /// </summary>
    private bool HasUnsavedWork() =>
        _filePath is null
            ? BoardStore.Serialize(CurrentTabs()) != _savedSnapshot
            : !BoardStore.MatchesFile(_filePath, CurrentTabs());

    /// <summary>
    /// Pregunta UNA vez, por el archivo entero, antes de perder trabajo. Devuelve false si el
    /// usuario decidió NO seguir. Ya no hay avisos por pestaña: quitar una pestaña no pregunta
    /// (es un cambio sin guardar más), así que el único momento de perder algo es este.
    ///
    /// Cubre los DOS casos (con y sin archivo), y cubrir el segundo es lo que hace seguro no
    /// tener sesión: sin él, sacar la restauración de sesión habría cambiado "te restaura cosas
    /// que no pediste" por "te pierde cosas sin avisar", que es estrictamente peor.
    /// </summary>
    private bool ConfirmDiscardChanges()
    {
        if (!HasUnsavedWork()) return true;

        var question = _filePath is { } file
            ? $"\"{Path.GetFileNameWithoutExtension(file)}\" tiene cambios sin guardar.\n\n¿Guardarlos antes de seguir?"
            : "Estos boards no están guardados en ningún archivo.\n\n¿Guardarlos antes de seguir?";

        return MessageBox.Show(question, "Ampz MediaBoard", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false, // Cancelar: todo queda como estaba.
        };
    }

    #endregion

    #region Pestañas

    /// <summary>
    /// Crea una pestaña con un board vacío, en SEGUNDO PLANO. Quien la quiera visible llama a
    /// <see cref="SwitchTo"/>. Nacer suspendida no es un detalle: si se le carga un board antes
    /// de mostrarla (abrir un archivo de varias pestañas), sus clips quedan pendientes hasta que
    /// la pestaña tenga una superficie visible donde dibujar.
    /// </summary>
    private BoardTab AddTab(string name)
    {
        var tab = new BoardTab(name);
        tab.Deactivate();
        _tabs.Add(tab);
        BoardHost.Children.Add(tab.View);
        return tab;
    }

    /// <summary>Ctrl+T / "+": una pestaña más EN el archivo. Es un cambio sin guardar.</summary>
    private void AddTabToDocument()
    {
        SwitchTo(AddTab(BoardTab.NextDefaultName(_tabs.Select(t => t.Name))));
        RefreshModified();
    }

    /// <summary>
    /// Libera una pestaña y saca su vista. Board ANTES que vista: el board desengancha cada
    /// player de su VideoView y los detiene en paralelo (VlcEngine.Release); recién después se
    /// destruyen las ventanas nativas. Mismo orden que ReplaceRoot → Rebuild.
    /// </summary>
    private void RemoveTabView(BoardTab tab)
    {
        tab.Dispose();
        BoardHost.Children.Remove(tab.View);
    }

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
        previous?.Deactivate();

        tab.Activate();

        // El slider de volumen general es UNO solo en la barra y actúa sobre la pestaña ACTIVA:
        // se re-apunta al board entrante y así muestra SU valor (cada board tiene el suyo).
        MasterVolumePanel.DataContext = tab.Board;

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
    /// Quita una pestaña del archivo, SIN preguntar: sus boards siguen en el archivo hasta que
    /// guardes, así que no se pierde nada que "No guardar" no pueda devolver — queda como un
    /// cambio sin guardar más, y el aviso del ARCHIVO lo cubre al cerrar. Si era la última,
    /// queda una vacía en su lugar.
    /// </summary>
    private void CloseTab(BoardTab tab)
    {
        if (_tabs.Count == 1)
        {
            SwitchTo(AddTab(BoardTab.NextDefaultName([])));
        }
        else if (ReferenceEquals(tab, _active))
        {
            // La de la derecha, o la de la izquierda si era la última: como un navegador.
            var i = _tabs.IndexOf(tab);
            SwitchTo(_tabs[i + 1 < _tabs.Count ? i + 1 : i - 1]);
        }

        _tabs.Remove(tab);
        RemoveTabView(tab);
        RefreshModified();
    }

    private void CycleTab(int step)
    {
        var i = _tabs.IndexOf(_active!);
        SwitchTo(_tabs[((i + step) % _tabs.Count + _tabs.Count) % _tabs.Count]);
    }

    /// <summary>Ctrl+1..8 van a esa pestaña; Ctrl+9 a la ÚLTIMA, como en cualquier navegador.</summary>
    private void JumpToTab(int number)
    {
        if (number == 9) SwitchTo(_tabs[^1]);
        else if (number <= _tabs.Count) SwitchTo(_tabs[number - 1]);
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
        if ((sender as FrameworkElement)?.DataContext is BoardTab tab && _tabs.Contains(tab)) CloseTab(tab);
    }

    /// <summary>Click del medio sobre una pestaña = cerrarla, como en cualquier navegador.</summary>
    private void OnTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is BoardTab tab && _tabs.Contains(tab)) CloseTab(tab);
    }

    #endregion

    #region Renombrar pestañas (doble click)

    /// <summary>
    /// Doble click sobre la pestaña = renombrarla en línea. El primer click ya la activó (Click
    /// del botón), así que siempre se renombra la que estás mirando.
    /// </summary>
    private void OnTabDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Un doble click sobre el × NO es renombrar: el MouseDoubleClick de WPF lo levanta el
        // botón de la pestaña aunque el × ya haya manejado el click — y el primer click cerró
        // esta pestaña, así que la del doble click puede ni existir.
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is { } button && !ReferenceEquals(button, sender)) return;
        if ((sender as FrameworkElement)?.DataContext is not BoardTab tab || !_tabs.Contains(tab)) return;

        e.Handled = true;
        tab.IsRenaming = true;
    }

    /// <summary>La caja aparece (IsRenaming) → se llena con el nombre actual, foco y todo seleccionado.</summary>
    private void OnRenameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible || box.DataContext is not BoardTab tab) return;
        _renameBox = box;
        box.Text = tab.Name;
        // Diferido: el foco no prende sobre un elemento que todavía no terminó de hacerse visible.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    private void OnRenameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not BoardTab tab) return;
        switch (e.Key)
        {
            case Key.Enter:
                CommitRename(tab, box.Text);
                e.Handled = true;
                break;
            case Key.Escape:
                // Cancelar: se apaga la edición SIN tocar el nombre. El LostKeyboardFocus que
                // viene después ya encuentra IsRenaming en false y no confirma nada.
                _renameBox = null;
                tab.IsRenaming = false;
                FocusBoard();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Perder el foco (Tab, otro control) confirma, como en el Explorer.</summary>
    private void OnRenameBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is BoardTab { IsRenaming: true } tab) CommitRename(tab, box.Text);
    }

    /// <summary>La caja de renombrar abierta, si hay una. La usa el click afuera para leer el texto.</summary>
    private TextBox? _renameBox;

    /// <summary>
    /// Un click FUERA de la caja confirma el renombre ("perder el foco" del pedido).
    ///
    /// ⚠ No alcanza con el LostKeyboardFocus de la caja, y se vio en la prueba: en WPF un click
    /// sobre algo NO enfocable (el fondo de un sector, la tira, casi todo el board) no mueve el
    /// foco de teclado. La caja se quedaba abierta con el texto nuevo sin confirmar, y el
    /// próximo atajo iba a parar a ella. Preview y sin Handled: el click sigue su camino (si
    /// era sobre otra pestaña, además la activa).
    /// </summary>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_renameBox is null) return;
        if (e.OriginalSource is DependencyObject source && ReferenceEquals(FindAncestor<TextBox>(source), _renameBox)) return;
        CommitPendingRename();
    }

    private void CommitPendingRename()
    {
        if (_renameBox is { DataContext: BoardTab { IsRenaming: true } tab } box) CommitRename(tab, box.Text);
    }

    /// <summary>
    /// Confirma el nombre. Vacío (o solo espacios) REVIERTE al que tenía: una pestaña sin nombre
    /// no se puede distinguir en la tira, y confirmar un vacío casi siempre es un accidente.
    /// Renombrar es un cambio sin guardar como cualquier otro: el nombre vive en el archivo.
    /// </summary>
    private void CommitRename(BoardTab tab, string text)
    {
        _renameBox = null;
        tab.IsRenaming = false;
        var name = text.Trim();
        if (name.Length > 0) tab.Name = name;
        FocusBoard();
        RefreshModified();
    }

    /// <summary>
    /// Devuelve el foco a la ventana. Sin esto el foco queda en la caja ya oculta y el próximo
    /// Espacio o A/B no llega a los atajos (que se saltean mientras se escribe en una caja).
    /// </summary>
    private void FocusBoard() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Focus());

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        return node as T;
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

        // UNA sola pregunta, por el archivo entero. Cancelar aborta el cierre.
        if (!ConfirmDiscardChanges())
        {
            e.Cancel = true;
            return;
        }

        // No se escribe NINGÚN estado al cerrar. El único lugar donde viven los boards es su
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

    #region Archivos de board

    /// <summary>Ctrl+N: archivo nuevo con una sola pestaña vacía (con el aviso del archivo actual).</summary>
    private void NewDocument()
    {
        if (!ConfirmDiscardChanges()) return;
        LoadBlankDocument();
    }

    private void OpenWithDialog()
    {
        // "Abrir" REEMPLAZA el documento entero (todas sus pestañas): primero el aviso.
        if (!ConfirmDiscardChanges()) return;

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
    /// Abre un archivo en ESTA ventana, reemplazando todas las pestañas. El aviso de cambios sin
    /// guardar lo hace quien llama (al arrancar no hay nada que perder).
    /// </summary>
    private void OpenPath(string path)
    {
        // Se lee ANTES de tocar las pestañas: si el archivo está corrupto, el documento actual
        // queda intacto en vez de reemplazado por nada.
        var full = Path.GetFullPath(path);
        if (BoardStore.LoadFrom(full) is not { } document)
        {
            MessageBox.Show(
                $"No se pudo leer el board:\n\n{path}\n\nEl archivo puede estar corrupto o no ser un .mboard válido.",
                "Ampz MediaBoard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ReplaceDocument(document.Tabs, document.ActiveTab);
        MarkSaved(full);
    }

    /// <summary>
    /// Guarda TODAS las pestañas en el archivo; si todavía no hay ninguno, se comporta como
    /// "Guardar como". Devuelve true si quedó guardado — el aviso de cierre depende de ese dato
    /// para saber si puede dejar cerrar.
    /// </summary>
    private bool Save() => _filePath is null ? SaveAs() : Write(_filePath);

    /// <summary>Devuelve false si el usuario canceló el diálogo o si el guardado falló.</summary>
    private bool SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Guardar board",
            Filter = BoardFile.DialogFilter,
            DefaultExt = BoardFile.Extension,
            AddExtension = true,
            FileName = _filePath is not null
                ? Path.GetFileName(_filePath)
                : "board" + BoardFile.Extension,
        };

        if (dialog.ShowDialog() != true) return false;

        return Write(BoardFile.EnsureExtension(dialog.FileName));
    }

    private bool Write(string path)
    {
        var error = BoardStore.SaveTo(path, CurrentTabs(), _tabs.IndexOf(_active!));
        if (error is not null)
        {
            // Un "guardar" que falla en silencio es la peor mentira que le podés decir al usuario:
            // se va tranquilo creyendo que su trabajo está a salvo.
            MessageBox.Show($"No se pudo guardar el board:\n\n{error}",
                "Ampz MediaBoard", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        MarkSaved(path);
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
    /// El título es el del ARCHIVO (no el de la pestaña activa: el archivo es el documento, y
    /// cambiar de pestaña no cambia en qué archivo estás) y dice SIEMPRE sobre cuál trabajás — o
    /// que todavía no hay ninguno. Con cambios sin guardar lleva "•" pegado al nombre.
    ///
    /// ⚠ El nombre del board va PRIMERO y la app se abrevia a "AMB". No es capricho estético:
    /// el botón de la barra de tareas de Windows trunca por la DERECHA y da lugar a unos pocos
    /// caracteres. Con "Ampz MediaBoard — " adelante (18 caracteres antes de empezar a decir
    /// algo), el nombre del board se cortaba SIEMPRE y todas las ventanas se veían idénticas
    /// — que es justo cuando más lo necesitás, con varios boards abiertos a la vez (la app es
    /// multi-instancia a propósito). Lo que identifica va primero; la marca, al final, donde
    /// puede perderse sin costo.
    /// </summary>
    private void UpdateTitle()
    {
        var name = _filePath is null ? "Board sin guardar" : Path.GetFileNameWithoutExtension(_filePath);
        var title = $"{name}{(_isModified ? " •" : "")} — AMB";
        if (Title != title) Title = title;
    }

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
        // Escribiendo el nombre de una pestaña, las teclas son TEXTO: sin esto, tipear una "A"
        // movería el marker de loop y el Espacio daría play en vez de escribir un espacio.
        if (Keyboard.FocusedElement is TextBox) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            switch (e.Key)
            {
                case Key.S when shift:
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

                // Nuevo = ARCHIVO nuevo (con el aviso del actual); Ctrl+T = una pestaña más EN
                // el archivo. Antes eran el mismo gesto, cuando cada pestaña era su propio archivo.
                case Key.N:
                    NewDocument();
                    e.Handled = true;
                    return;
                case Key.T:
                    AddTabToDocument();
                    e.Handled = true;
                    return;
                case Key.W:
                    CloseTab(_active!);
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
