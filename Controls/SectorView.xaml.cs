using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AmpzMediaBoard.Board;
using AmpzMediaBoard.Layout;
using AmpzMediaBoard.Media;

namespace AmpzMediaBoard.Controls;

/// <summary>
/// La vista de UN sector. Su DataContext es siempre un <see cref="SectorNode"/>.
///
/// Todo lo que toca al <c>VideoView</c> vive acá adentro y en ningún otro lado: es la CAPA DE
/// RENDER, deliberadamente aislada. Hoy usa el VideoView estándar de LibVLCSharp (rápido de
/// poner a andar, pero hostea un HWND → puede parpadear al arrastrar los splitters y puede
/// tragarse los eventos de drop). Si eso molesta, se migra a custom rendering (VLC escribiendo
/// frames a un WriteableBitmap) tocando ESTE archivo y ninguno más.
/// </summary>
public partial class SectorView : UserControl
{
    /// <summary>
    /// Formato del arrastre ENTRE sectores. Solo marca "esto es un movimiento de media"; el nodo
    /// de origen viaja por <see cref="_dragSource"/>.
    ///
    /// ¿Por qué no meter el nodo adentro del DataObject? Porque el DataObject de WPF está pensado
    /// para cruzar procesos y envuelve lo que le metas en COM; con objetos vivos y no
    /// serializables (y SectorNode arrastra un MediaPlayer nativo) eso es pedir problemas. Como
    /// el arrastre nunca sale de esta ventana, un campo estático es más simple y no puede fallar.
    /// </summary>
    private const string SectorMediaFormat = "AmpzMediaBoard.SectorMedia";

    private static SectorNode? _dragSource;

    private Point _dragOrigin;
    private bool _dragArmed;

    private SectorNode? _node;

    /// <summary>
    /// La superficie de video está escondida porque el usuario está arrastrando un divisor.
    /// Vive acá (en la CAPA DE RENDER) y no en el nodo, porque es puramente visual.
    /// </summary>
    private bool _frozen;

    /// <summary>Lo inyecta <c>BoardView</c> al crear la vista. Es quien sabe partir y cerrar sectores.</summary>
    public BoardViewModel? Board { get; set; }

    public SectorView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        SplitVerticalButton.Click += (_, _) => Split(SplitOrientation.Horizontal);
        SplitHorizontalButton.Click += (_, _) => Split(SplitOrientation.Vertical);
        ClearButton.Click += (_, _) => _node?.Unload();
        CopyPathButton.Click += (_, _) => CopyPathToClipboard();
        CloseButton.Click += (_, _) => { if (_node is not null) Board?.Close(_node); };
        PlayButton.Click += (_, _) => _node?.TogglePlay();
        BrowseButton.Click += (_, _) => BrowseForMedia();
        RelinkButton.Click += (_, _) => BrowseForMedia();

        Timeline.SeekRequested += (_, ms) => _node?.SeekTo(ms);

        // PreviewMouseDown (no MouseDown): el evento tiene que llegarnos ANTES de que un botón
        // de la cabecera lo consuma, así hacer click en "partir" también selecciona el sector.
        PreviewMouseDown += (_, _) => { if (_node is not null) Board?.Select(_node); };

        // El VideoView crea su ventana nativa al cargarse. Recién ahí el MediaPlayer tiene a
        // dónde dibujar, así que este es el momento correcto para arrancar un clip pendiente
        // (el caso del board restaurado desde disco: el clip se preparó antes de que la vista
        // existiera).
        Video.Loaded += (_, _) => StartWhenSurfaceReady();

        // La cabecera ARMA el arrastre, pero el movimiento se escucha en el SECTOR ENTERO.
        // ⚠ No es un detalle: WPF no captura el mouse al apretar, así que los eventos de
        // movimiento solo llegan al elemento que está DEBAJO del cursor. La cabecera mide ~24px
        // de alto; si escucháramos solo ahí, salirse de esos 24px antes de superar el umbral
        // de arrastre dejaba el drag sin arrancar nunca, sin ningún error visible.
        Header.PreviewMouseLeftButtonDown += OnHeaderMouseDown;
        PreviewMouseMove += OnHeaderMouseMove;
        PreviewMouseLeftButtonUp += (_, _) => _dragArmed = false;

        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
    }

    #region Arrastre del media entre sectores

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Un click sobre los botones de la cabecera (partir, desvincular, cerrar) NO arma un
        // arrastre: si lo hiciera, el más mínimo temblor del mouse convertiría "cerrar sector"
        // en "mover el clip a otro lado".
        if (e.OriginalSource is DependencyObject source && FindAncestor<ButtonBase>(source) is not null)
        {
            DragLog($"click en un boton de la cabecera de '{_node?.Title}': NO se arma arrastre");
            return;
        }

        _dragOrigin = e.GetPosition(this);
        _dragArmed = true;
        DragLog($"ARMADO el arrastre en la cabecera de '{_node?.Title}' (tiene media: {_node?.HasMedia}, ausente: {_node?.IsMissing})");
    }

    private void OnHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        if (_node is null || (!_node.HasMedia && !_node.IsMissing)) return;

        // Se espera a superar el umbral del sistema antes de arrancar el arrastre. Sin esto,
        // cualquier click con un pixel de movimiento se convierte en un drag y la cabecera se
        // vuelve imposible de clickear.
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragArmed = false;
        _dragSource = _node;

        try
        {
            DragLog($"DoDragDrop ARRANCA desde '{_node.Title}'");
            var result = DragDrop.DoDragDrop(this, new DataObject(SectorMediaFormat, "sector"), DragDropEffects.Move);
            DragLog($"DoDragDrop TERMINA con efecto = {result}");
        }
        finally
        {
            // DoDragDrop es BLOQUEANTE: recién vuelve cuando el usuario soltó o canceló con Esc.
            // El finally garantiza que el origen se limpie incluso si el drop tiró una excepción,
            // porque un _dragSource colgado haría que el próximo arrastre mueva el clip equivocado.
            _dragSource = null;
        }
    }

    /// <summary>
    /// Instrumentación del arrastre entre sectores: poniéndola en <c>true</c> escribe la
    /// secuencia real de eventos a `ampz-drag.log`, junto al exe.
    ///
    /// Queda APAGADA pero presente a propósito. El drag&amp;drop de WPF conviviendo con el HWND
    /// del VideoView no se diagnostica con teoría — o ves la secuencia (armado → DoDragDrop →
    /// DragOver aceptado/rechazado → drop), o adivinás. Reescribirla cada vez que haga falta es
    /// perder media hora; dejarla prendida es ensuciar el disco del usuario para siempre.
    /// </summary>
    private static readonly bool DragDiagnostics = false;

    private static string _lastDragState = string.Empty;

    private static void DragLog(string message)
    {
        if (!DragDiagnostics) return;
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "ampz-drag.log"),
                $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Un log que falla jamás puede voltear la app.
        }
    }

    /// <summary>DragOver dispara decenas de veces por segundo: solo se loguea cuando el estado CAMBIA.</summary>
    private static void DragLogState(string state)
    {
        if (state == _lastDragState) return;
        _lastDragState = state;
        DragLog(state);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    #endregion

    private void Split(SplitOrientation orientation)
    {
        if (_node is not null) Board?.Split(_node, orientation);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_node is not null) _node.PropertyChanged -= OnNodeChanged;

        _node = DataContext as SectorNode;

        if (_node is not null) _node.PropertyChanged += OnNodeChanged;

        SyncRender();
        SyncTimeLabel();
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SectorNode.Player):
            case nameof(SectorNode.Kind):
            case nameof(SectorNode.MissingPath):
                SyncRender();
                break;
            case nameof(SectorNode.PositionMs):
            case nameof(SectorNode.DurationMs):
                SyncTimeLabel();
                break;
        }
    }

    /// <summary>
    /// Decide qué superficie se ve (video / imagen / estado vacío) y le engancha el reproductor
    /// al VideoView.
    ///
    /// ⚠ El orden importa: el <c>MediaPlayer</c> se DESENGANCHA del VideoView ANTES de que el
    /// sector lo libere. <c>SectorNode.Unload()</c> pone Player en null (lo que dispara esto)
    /// y recién después llama a Dispose(); si el VideoView siguiera apuntando al player mientras
    /// se destruye, el hilo de render de VLC escribiría sobre una ventana que ya no existe.
    /// </summary>
    private void SyncRender()
    {
        var kind = _node?.Kind ?? MediaKind.None;

        Video.MediaPlayer = _node?.Player;

        var missing = _node?.MissingPath;

        // ⚠ El congelado entra en la MISMA expresión que decide la visibilidad, no como una
        // asignación aparte: SyncRender puede dispararse en medio de un arrastre (un tick que
        // cambia MissingPath, por ejemplo) y devolvería el video a la pantalla justo cuando lo
        // estamos escondiendo. Que la condición sea una sola hace imposible esa carrera.
        Video.Visibility = kind == MediaKind.Video && !_frozen ? Visibility.Visible : Visibility.Collapsed;
        FreezeVeil.Visibility = kind == MediaKind.Video && _frozen ? Visibility.Visible : Visibility.Collapsed;
        Still.Visibility = kind == MediaKind.Image ? Visibility.Visible : Visibility.Collapsed;

        // "Vacío" y "falta el archivo" son estados DISTINTOS y se ven distinto: uno te invita a
        // soltar algo, el otro te dice qué se perdió y cómo recuperarlo.
        MissingHint.Visibility = missing is not null ? Visibility.Visible : Visibility.Collapsed;

        // El asa y el cursor de "movible" solo aparecen si hay algo que mover.
        var movable = kind != MediaKind.None || missing is not null;
        DragGrip.Visibility = movable ? Visibility.Visible : Visibility.Collapsed;

        // Copiar el path solo tiene sentido si hay un path. Se COLAPSA en vez de deshabilitarse
        // para que los botones no bailen de posición: el StackPanel se corre igual, pero un botón
        // gris que no hace nada invita a clickearlo y no explica por qué no pasa nada.
        // ⚠ Se muestra TAMBIEN con el archivo ausente, y es el caso donde MAS sirve: es la ruta
        // que perdiste, y con ella en el portapapeles la pegás en el Explorer para ir a buscarla.
        CopyPathButton.Visibility = movable ? Visibility.Visible : Visibility.Collapsed;
        Header.Cursor = movable ? Cursors.SizeAll : Cursors.Arrow;
        EmptyHint.Visibility = kind == MediaKind.None && missing is null
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (missing is not null)
        {
            MissingName.Text = Path.GetFileName(missing);
            MissingFullPath.Text = Path.GetDirectoryName(missing) ?? missing;
        }

        StartWhenSurfaceReady();
    }

    /// <summary>
    /// Esconde o devuelve la superficie de video mientras dura un arrastre de splitter.
    ///
    /// Lo llama <c>BoardView</c> desde el DragStarted/DragCompleted del GridSplitter, en tándem
    /// con <c>SectorNode.Freeze/Thaw</c> (que pausa el clip). Acá pasa la otra mitad del arreglo:
    /// COLAPSAR el VideoView saca su ventana nativa del layout, así WPF deja de reposicionarla
    /// en cada píxel del arrastre — que es lo que trababa el board con varios videos corriendo.
    ///
    /// ⚠ Se colapsa y NO se pone en Hidden. Hidden sigue participando del layout: la ventana se
    /// seguiría midiendo y arreglando en cada movimiento, o sea que el costo que queremos evitar
    /// se pagaría igual. Collapsed la saca del cálculo.
    ///
    /// El velo solo se muestra si el sector tiene video: en uno vacío o con una imagen no hay
    /// nada que esconder, y taparlo sería avisar de algo que no está pasando.
    /// </summary>
    public void SetFrozen(bool frozen)
    {
        if (_frozen == frozen) return;
        _frozen = frozen;

        var isVideo = (_node?.Kind ?? MediaKind.None) == MediaKind.Video;

        Video.Visibility = isVideo && !frozen ? Visibility.Visible : Visibility.Collapsed;
        FreezeVeil.Visibility = isVideo && frozen ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Copia al portapapeles el path COMPLETO del archivo del sector.
    ///
    /// Es el camino de vuelta del Ctrl+V que ya existe (ver <c>MainWindow.PasteMediaPath</c>):
    /// si la app acepta que le peguen una ruta, tiene que saber devolverla — el sector es el
    /// único lugar donde esa ruta está escrita, y sacarla a ojo desde el título (que va trimado
    /// con puntos suspensivos) es imposible.
    ///
    /// ⚠ Va con <c>SetDataObject(..., copy: true)</c> y no con <c>SetText</c>. Sin ese "copy",
    /// lo copiado vive en la memoria de ESTE proceso y el portapapeles se VACÍA al cerrar la app:
    /// copiar el path, cerrar el board y pegarlo en el Explorer —que es el flujo obvio— no
    /// pegaría nada. El true le pide a Windows que se quede con el contenido.
    ///
    /// ⚠ El portapapeles de Windows es un recurso EXCLUSIVO y de un solo dueño: cualquier otra
    /// app que lo tenga abierto en ese instante hace fallar la llamada con
    /// <c>CLIPBRD_E_CANT_OPEN</c>. Es transitorio y no es culpa nuestra, pero una excepción sin
    /// atrapar acá voltea la app entera por un botón accesorio. Se traga en silencio, igual que
    /// el resto de la app (ver la convención de Load/Save).
    /// </summary>
    private void CopyPathToClipboard()
    {
        // El path del media, o el del archivo que se perdió: en el estado "ausente" el sector
        // conserva la ruta a propósito, y ahí copiarla es justo lo que te deja ir a buscarlo.
        if ((_node?.MediaPath ?? _node?.MissingPath) is not { Length: > 0 } path) return;

        try
        {
            Clipboard.SetDataObject(path, true);
            FlashCopyFeedback();
        }
        catch
        {
            // Portapapeles ocupado por otra app. No hay nada que hacer y no se pierde nada:
            // el usuario vuelve a apretar. Sin el visto, el botón ya está diciendo que falló.
        }
    }

    /// <summary>
    /// Confirma la copia cambiando el glifo por un visto durante un segundo.
    ///
    /// No es adorno: copiar al portapapeles es la acción SIN retorno visible por excelencia —
    /// la app se ve exactamente igual antes y después. Sin confirmación el usuario no sabe si
    /// el click prendió, y la duda se resuelve apretando de nuevo.
    /// </summary>
    private void FlashCopyFeedback()
    {
        CopyPathButton.Content = "✓";

        // El timer se apaga a sí mismo en el primer tick: es un disparo único, no un latido.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            CopyPathButton.Content = "⧉";
        };
        timer.Start();
    }

    /// <summary>Abre el diálogo de archivo para re-vincular un sector huérfano.</summary>
    /// <summary>
    /// Abre el diálogo de archivo para poner un clip en el sector.
    ///
    /// Es la alternativa al drag &amp; drop, y no es un capricho: arrastrar obliga a tener el
    /// Explorer abierto y acomodado al lado de la app. Con un path largo, uno de red, o uno que
    /// copiaste de otro lado, el diálogo gana — y su campo "Nombre" acepta que PEGUES el path
    /// completo y le des Enter.
    ///
    /// Lo usan dos entradas: el botón de la cabecera y el "Buscar el archivo…" del estado de
    /// archivo ausente.
    /// </summary>
    private void BrowseForMedia()
    {
        if (_node is null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = _node.IsMissing ? "Buscar el archivo del sector" : "Elegir el media del sector",
            Filter = MediaKinds.DialogFilter,
            CheckFileExists = true,
        };

        // Arranca en la carpeta del clip que el sector tenía (o del que perdió): si el archivo se
        // movió dentro del mismo árbol, o si el próximo sale de la misma carpeta, ya estás cerca.
        var reference = _node.MediaPath ?? _node.MissingPath;
        if (reference is not null && Path.GetDirectoryName(reference) is { Length: > 0 } folder)
            dialog.InitialDirectory = folder;

        if (dialog.ShowDialog() != true) return;

        _node.Adopt(dialog.FileName);
        Board?.Select(_node);
    }

    /// <summary>
    /// Dispara la reproducción pendiente, pero SOLO cuando la superficie de video ya está viva.
    ///
    /// El diferido a prioridad Background no es paranoia: en este punto acabamos de poner el
    /// VideoView en Visible, y su ventana nativa se posiciona durante el layout. Arrancar el
    /// Play() en la misma vuelta lo agarraría con tamaño cero. Una vuelta después del layout,
    /// la superficie ya está donde tiene que estar.
    /// </summary>
    private void StartWhenSurfaceReady()
    {
        if (_node?.Player is null || !Video.IsLoaded) return;

        var node = _node;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            // El sector pudo haber cambiado de contenido mientras esperábamos la vuelta del
            // Dispatcher (otro drop encima, o un cierre). Si ya no es el mismo nodo, no tocamos nada.
            if (ReferenceEquals(_node, node)) node.StartPending();
        });
    }

    private void SyncTimeLabel()
    {
        if (_node is null || _node.DurationMs <= 0)
        {
            TimeLabel.Text = string.Empty;
            return;
        }

        TimeLabel.Text = $"{Fmt(_node.PositionMs)} / {Fmt(_node.DurationMs)}";
    }

    private static string Fmt(double ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        // Décimas y no milésimas: con milésimas el número cambia tan rápido que no se lee, y al
        // posicionar markers lo que necesitás ver es el orden de magnitud, no el dígito exacto.
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }

    /// <summary>
    /// Suelta la superficie de video sin tocar el modelo. Lo llama <c>BoardView</c> antes de
    /// tirar el árbol visual al reconstruir el layout: el <c>MediaPlayer</c> vive en el
    /// <see cref="SectorNode"/> y SOBREVIVE a la reconstrucción (por eso el clip sigue
    /// reproduciendo después de partir un sector), pero el VideoView que lo mostraba no —
    /// hostea una ventana nativa que hay que desenganchar a mano o queda colgada.
    /// </summary>
    public void Detach()
    {
        Video.MediaPlayer = null;
        if (Video is IDisposable disposable) disposable.Dispose();
        if (_node is not null) _node.PropertyChanged -= OnNodeChanged;
    }

    #region Drag & drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // Dos arrastres distintos caen acá: archivos desde Explorer, y media de OTRO sector.
        if (e.Data.GetDataPresent(SectorMediaFormat))
        {
            var moving = _dragSource is not null && !ReferenceEquals(_dragSource, _node);
            DragLogState($"DragOver sobre '{_node?.Title}' (video visible: {Video.Visibility == Visibility.Visible}) -> aceptado: {moving}");
            e.Effects = moving ? DragDropEffects.Move : DragDropEffects.None;
            DropVeil.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
            DropVeilText.Text = _node is { HasMedia: true } or { IsMissing: true }
                ? "Intercambiar"
                : "Mover acá";
            e.Handled = true;
            return;
        }

        var accepted = FirstSupported(e) is not null;

        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        DropVeil.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
        DropVeilText.Text = "Soltá acá";

        // Handled=true corta la burbuja hacia arriba: sin esto, el sector padre y la ventana
        // también procesarían el mismo arrastre y el archivo podría caer en el sector equivocado.
        e.Handled = true;
    }

    /// <summary>
    /// ⚠ DragLeave BURBUJEA DESDE LOS HIJOS. Cada vez que el cursor pasa de un elemento interno
    /// a otro (del hint al fondo, del fondo al header) se dispara un DragLeave que sube hasta
    /// acá — aunque el cursor NUNCA salió del sector. Apagar el velo a ciegas en ese evento es
    /// lo que producía el parpadeo.
    ///
    /// Por eso se verifica la geometría: el velo se apaga solo si el cursor está de verdad
    /// FUERA de los límites del sector.
    /// </summary>
    private void OnDragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(this);
        var outside = p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight;
        if (outside) DropVeil.Visibility = Visibility.Collapsed;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropVeil.Visibility = Visibility.Collapsed;

        // Movimiento de media entre sectores: intercambia contenidos. Ver BoardViewModel.SwapMedia.
        if (e.Data.GetDataPresent(SectorMediaFormat))
        {
            DragLog($"DROP sobre '{_node?.Title}' desde '{_dragSource?.Title}'");

            if (_dragSource is { } origin && _node is not null && !ReferenceEquals(origin, _node))
            {
                Board?.SwapMedia(origin, _node);
                DragLog("  -> intercambio EJECUTADO");
            }
            else
            {
                DragLog("  -> ignorado (mismo sector o sin origen)");
            }

            e.Handled = true;
            return;
        }

        var path = FirstSupported(e);
        if (path is null || _node is null) return;

        // Adopt() decide re-vincular o reemplazar según el estado del sector. Ver SectorNode.
        _node.Adopt(path);
        Board?.Select(_node);
        e.Handled = true;
    }

    /// <summary>
    /// Primer archivo SOPORTADO del arrastre. Se toma solo uno a propósito: un sector muestra un
    /// clip. Soltar una carpeta entera con 40 videos y que la app decida cuál va sería adivinar.
    /// </summary>
    private static string? FirstSupported(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;

        return files.FirstOrDefault(MediaKinds.IsSupported);
    }

    #endregion
}
