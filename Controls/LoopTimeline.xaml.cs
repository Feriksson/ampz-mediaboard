using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AmpzMediaBoard.Controls;

/// <summary>
/// La timeline del clip con la ZONA DE LOOP: riel completo, región marcada, playhead y dos
/// markers arrastrables (A = inicio, B = fin).
///
/// Está escrito con posicionamiento manual sobre un Canvas y NO con un Slider tuneado ni con
/// bindings de layout. La razón: acá hay cuatro elementos cuya posición depende de la MISMA
/// conversión ms→píxel, que además cambia con cada resize del sector. Centralizar esa conversión
/// en un solo método (<see cref="Relayout"/>) es lo que evita que los markers y el playhead
/// terminen desfasados entre sí — el bug clásico de este control.
/// </summary>
public partial class LoopTimeline : UserControl
{
    /// <summary>
    /// Separación mínima entre markers, en ms. Sin un mínimo, el usuario puede colapsar la zona
    /// a cero de un arrastre y el loop deja de tener sentido (además de meter a EnforceLoop en
    /// una ráfaga de seeks). 120ms ≈ 3 frames a 25fps: chico pero siempre reproducible.
    /// </summary>
    private const double MinSpanMs = 120;

    /// <summary>Se dispara cuando el usuario pide moverse a una posición (click en el riel).</summary>
    public event EventHandler<double>? SeekRequested;

    public LoopTimeline()
    {
        InitializeComponent();

        SizeChanged += (_, _) => Relayout();
        ThumbA.DragDelta += (_, e) => DragMarker(isStart: true, e.HorizontalChange);
        ThumbB.DragDelta += (_, e) => DragMarker(isStart: false, e.HorizontalChange);
        Root.MouseLeftButtonDown += OnTrackClick;
    }

    #region Dependency properties

    public static readonly DependencyProperty DurationMsProperty = DependencyProperty.Register(
        nameof(DurationMs), typeof(double), typeof(LoopTimeline),
        new FrameworkPropertyMetadata(0d, OnVisualPropertyChanged));

    /// <summary>
    /// ⚠ Esta propiedad es de SOLO LECTURA para el control: la timeline NO es dueña del tiempo,
    /// el reproductor lo es. Por eso —a diferencia de LoopStart/LoopEnd— NO lleva
    /// <c>BindsTwoWayByDefault</c>: un binding sin Mode explícito queda OneWay, que es lo correcto.
    ///
    /// NUNCA le asignes un valor desde adentro del control. En WPF, asignarle un valor LOCAL a
    /// una DependencyProperty con binding OneWay **destruye la BindingExpression** (el valor
    /// local tiene más precedencia y reemplaza la expresión). El binding no se recupera nunca
    /// más: la marca del playhead queda clavada para siempre. Bug real, ya pasó una vez.
    /// Para moverse, se dispara <see cref="SeekRequested"/> y el valor vuelve por el binding.
    /// </summary>
    public static readonly DependencyProperty PositionMsProperty = DependencyProperty.Register(
        nameof(PositionMs), typeof(double), typeof(LoopTimeline),
        new FrameworkPropertyMetadata(0d, OnVisualPropertyChanged));

    public static readonly DependencyProperty LoopStartMsProperty = DependencyProperty.Register(
        nameof(LoopStartMs), typeof(double), typeof(LoopTimeline),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnVisualPropertyChanged));

    public static readonly DependencyProperty LoopEndMsProperty = DependencyProperty.Register(
        nameof(LoopEndMs), typeof(double), typeof(LoopTimeline),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnVisualPropertyChanged));

    public double DurationMs
    {
        get => (double)GetValue(DurationMsProperty);
        set => SetValue(DurationMsProperty, value);
    }

    public double PositionMs
    {
        get => (double)GetValue(PositionMsProperty);
        set => SetValue(PositionMsProperty, value);
    }

    public double LoopStartMs
    {
        get => (double)GetValue(LoopStartMsProperty);
        set => SetValue(LoopStartMsProperty, value);
    }

    public double LoopEndMs
    {
        get => (double)GetValue(LoopEndMsProperty);
        set => SetValue(LoopEndMsProperty, value);
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LoopTimeline)d).Relayout();

    #endregion

    /// <summary>Ancho útil en píxeles. Nunca cero: se usa como divisor.</summary>
    private double Usable => Math.Max(1, ActualWidth);

    private double MsToX(double ms)
    {
        if (DurationMs <= 0) return 0;
        return Math.Clamp(ms / DurationMs, 0, 1) * Usable;
    }

    private double XToMs(double x)
    {
        if (DurationMs <= 0) return 0;
        return Math.Clamp(x / Usable, 0, 1) * DurationMs;
    }

    /// <summary>
    /// Reposiciona TODO desde la misma conversión. Se llama en cada cambio de propiedad y en
    /// cada resize; es barato (cuatro asignaciones de Canvas.Left y dos anchos).
    /// </summary>
    private void Relayout()
    {
        if (!IsLoaded && ActualWidth <= 0) return;

        Track.Width = Usable;

        var xa = MsToX(LoopStartMs);
        var xb = MsToX(LoopEndMs);

        Canvas.SetLeft(LoopRegion, xa);
        LoopRegion.Width = Math.Max(0, xb - xa);

        // Los markers se posicionan por su CENTRO: si los pusiéramos por el borde izquierdo, el
        // marker B quedaría medio ancho más allá del fin real de la zona.
        Canvas.SetLeft(ThumbA, xa - ThumbA.Width / 2);
        Canvas.SetLeft(ThumbB, xb - ThumbB.Width / 2);

        Canvas.SetLeft(Playhead, MsToX(PositionMs) - 1);

        // Sin clip cargado no hay nada que marcar: escondemos los markers en vez de dejarlos
        // amontonados en el cero, que se lee como un control roto.
        var visible = DurationMs > 0 ? Visibility.Visible : Visibility.Collapsed;
        ThumbA.Visibility = visible;
        ThumbB.Visibility = visible;
        LoopRegion.Visibility = visible;
        Playhead.Visibility = visible;
    }

    private void DragMarker(bool isStart, double deltaX)
    {
        if (DurationMs <= 0) return;

        // El delta viene en píxeles: se convierte a ms y se SUMA al valor actual. Convertir la
        // posición absoluta del mouse sería equivalente pero acumularía el offset del punto
        // donde agarraste el marker, y el marker "saltaría" al empezar a arrastrar.
        var deltaMs = deltaX / Usable * DurationMs;

        if (isStart)
        {
            LoopStartMs = Math.Clamp(LoopStartMs + deltaMs, 0, Math.Max(0, LoopEndMs - MinSpanMs));
        }
        else
        {
            LoopEndMs = Math.Clamp(LoopEndMs + deltaMs, LoopStartMs + MinSpanMs, DurationMs);
        }
    }

    private void OnTrackClick(object sender, MouseButtonEventArgs e)
    {
        // Si el click cayó sobre un marker, es el arranque de un arrastre, no una búsqueda.
        if (e.OriginalSource is Thumb || DurationMs <= 0) return;

        // Se pide el seek y NADA MÁS. Acá había un `PositionMs = ms;` que parecía inofensivo
        // ("muevo la marca al instante") y era el bug: al ser una asignación LOCAL sobre una DP
        // con binding OneWay, mataba el binding y el playhead no se movía nunca más.
        // No hace falta ningún atajo visual: el dueño del sector hace el seek, el reproductor
        // actualiza su posición y el valor vuelve por el binding en la misma vuelta del Dispatcher.
        SeekRequested?.Invoke(this, XToMs(e.GetPosition(Root).X));
        e.Handled = true;
    }
}
