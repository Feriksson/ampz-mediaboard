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

    /// <summary>Lo inyecta <c>BoardView</c> al crear la vista. Es quien sabe partir y cerrar sectores.</summary>
    public BoardViewModel? Board { get; set; }

    public SectorView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        SplitVerticalButton.Click += (_, _) => Split(SplitOrientation.Horizontal);
        SplitHorizontalButton.Click += (_, _) => Split(SplitOrientation.Vertical);
        ClearButton.Click += (_, _) => _node?.Unload();
        CloseButton.Click += (_, _) => { if (_node is not null) Board?.Close(_node); };
        PlayButton.Click += (_, _) => _node?.TogglePlay();
        RelinkButton.Click += (_, _) => BrowseAndRelink();

        Timeline.SeekRequested += (_, ms) => _node?.SeekTo(ms);

        // PreviewMouseDown (no MouseDown): el evento tiene que llegarnos ANTES de que un botón
        // de la cabecera lo consuma, así hacer click en "partir" también selecciona el sector.
        PreviewMouseDown += (_, _) => { if (_node is not null) Board?.Select(_node); };

        // El VideoView crea su ventana nativa al cargarse. Recién ahí el MediaPlayer tiene a
        // dónde dibujar, así que este es el momento correcto para arrancar un clip pendiente
        // (el caso del board restaurado desde disco: el clip se preparó antes de que la vista
        // existiera).
        Video.Loaded += (_, _) => StartWhenSurfaceReady();

        // La cabecera es el asa para mover el media a otro sector.
        Header.PreviewMouseLeftButtonDown += OnHeaderMouseDown;
        Header.PreviewMouseMove += OnHeaderMouseMove;
        Header.PreviewMouseLeftButtonUp += (_, _) => _dragArmed = false;

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
        if (e.OriginalSource is DependencyObject source && FindAncestor<ButtonBase>(source) is not null) return;

        _dragOrigin = e.GetPosition(this);
        _dragArmed = true;
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
            DragDrop.DoDragDrop(this, new DataObject(SectorMediaFormat, string.Empty), DragDropEffects.Move);
        }
        finally
        {
            // DoDragDrop es BLOQUEANTE: recién vuelve cuando el usuario soltó o canceló con Esc.
            // El finally garantiza que el origen se limpie incluso si el drop tiró una excepción,
            // porque un _dragSource colgado haría que el próximo arrastre mueva el clip equivocado.
            _dragSource = null;
        }
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

        Video.Visibility = kind == MediaKind.Video ? Visibility.Visible : Visibility.Collapsed;
        Still.Visibility = kind == MediaKind.Image ? Visibility.Visible : Visibility.Collapsed;

        // "Vacío" y "falta el archivo" son estados DISTINTOS y se ven distinto: uno te invita a
        // soltar algo, el otro te dice qué se perdió y cómo recuperarlo.
        MissingHint.Visibility = missing is not null ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>Abre el diálogo de archivo para re-vincular un sector huérfano.</summary>
    private void BrowseAndRelink()
    {
        if (_node is null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Buscar el archivo del sector",
            Filter = "Media|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv;*.flv;*.mpg;*.mpeg;*.ts;*.m2ts;*.gif;"
                   + "*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff|Todos los archivos|*.*",
            CheckFileExists = true,
        };

        // Arranca en la carpeta donde VIVÍA el archivo: si el clip se movió dentro del mismo
        // árbol, ya estás cerca. Si esa carpeta tampoco existe, el diálogo la ignora solo.
        if (_node.MissingPath is { } previous && Path.GetDirectoryName(previous) is { } folder)
            dialog.InitialDirectory = folder;

        if (dialog.ShowDialog() == true) _node.Relink(dialog.FileName);
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
            if (_dragSource is { } origin && _node is not null && !ReferenceEquals(origin, _node))
                Board?.SwapMedia(origin, _node);

            e.Handled = true;
            return;
        }

        var path = FirstSupported(e);
        if (path is null || _node is null) return;

        // Soltar sobre un sector huérfano es RE-VINCULAR: se conservan sus markers de loop.
        // Soltar sobre cualquier otro sector es reemplazar, y ahí los markers sí se resetean
        // (son de otro clip: mantenerlos sería marcar una zona que no tiene nada que ver).
        if (_node.IsMissing) _node.Relink(path);
        else _node.Load(path);
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
