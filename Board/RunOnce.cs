using System.Windows.Threading;
using AmpzMediaBoard.Layout;
using AmpzMediaBoard.Media;

namespace AmpzMediaBoard.Board;

/// <summary>
/// "Correr todo una vez" (botón de la barra y Ctrl+R): cada sector con video corre su zona A→B
/// UNA vez y se queda pausado en B. Es la herramienta para comparar videos casi idénticos, así
/// que lo que importa es que ARRANQUEN JUNTOS.
///
/// ⚠ Por eso son DOS fases y no un RestartFrom por sector en fila: cada RestartFrom reabre el
/// archivo (cientos de ms), y en fila el último sector arrancaría medio segundo tarde.
///  1. Preparar: cada sector se pausa y busca A (<see cref="SectorNode.BeginRunOnce"/>). Lo caro
///     (abrir, buscar, decodificar el cuadro de A) se paga acá, en paralelo.
///  2. Largada: cuando TODOS dicen listo (o vence <see cref="PrepareTimeout"/>), se despausan
///     todos en la MISMA vuelta del Dispatcher (<see cref="SectorNode.GoRunOnce"/>), que es solo
///     un SetPause(false) por sector.
/// El seek de VLC no es frame-exact: la sincronía queda en decenas de ms, límite del motor.
/// </summary>
public sealed class RunOnce
{
    /// <summary>
    /// Techo de la fase de preparación. Un sector que no llega (disco lento, archivo trabado) no
    /// puede dejar a los demás esperando para siempre: se larga igual y ese sale desfasado.
    /// </summary>
    public static readonly TimeSpan PrepareTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly DispatcherTimer _poll;
    private List<SectorNode> _targets = [];
    private long _deadline;

    public RunOnce()
    {
        // ~15 ms: la mitad del latido del board. Lo que se mide es "¿ya están todos?", y cuanto
        // antes se vea, antes arrancan; no hay trabajo pesado por vuelta.
        _poll = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _poll.Tick += (_, _) => Poll();
    }

    /// <summary>
    /// ¿Este sector corre? Solo video con reproductor: imágenes, vacíos y ausentes no tienen nada
    /// que correr. Los CONGELADOS tampoco (tapados por una expansión, en pleno arrastre): no se
    /// ven, y arrancarlos al descongelar sería arrancarlos fuera de sincronía. Pura, para BoardProbe.
    /// </summary>
    public static bool IsTarget(MediaKind kind, bool hasPlayer, bool frozen) =>
        kind == MediaKind.Video && hasPlayer && !frozen;

    /// <summary>
    /// El alcance: la pestaña ACTIVA más el panel fijado — lo que está a la vista. Las pestañas de
    /// fondo están suspendidas (mismo alcance que el solo, ver PinnedDock.Solo).
    /// </summary>
    public static IEnumerable<SectorNode> Scope(BoardViewModel activeTab, BoardViewModel dock) =>
        activeTab.AllSectors.Concat(dock.AllSectors);

    public static List<SectorNode> Targets(IEnumerable<SectorNode> scope) =>
        scope.Where(s => IsTarget(s.Kind, s.Player is not null, s.IsFrozen)).ToList();

    /// <summary>
    /// Arranca (o REINICIA, si ya había una corrida) sobre el alcance dado. Apretar de nuevo
    /// mientras corre vuelve a llevar a todos a A y los larga juntos otra vez.
    /// </summary>
    public void Start(IEnumerable<SectorNode> scope)
    {
        _poll.Stop();
        _targets = Targets(scope);
        if (_targets.Count == 0) return;

        foreach (var sector in _targets) sector.BeginRunOnce();
        DiagLog.Write($"runonce prepare n={_targets.Count}");

        _deadline = Environment.TickCount64 + (long)PrepareTimeout.TotalMilliseconds;
        _poll.Start();
    }

    private void Poll()
    {
        // Se consulta a TODOS en cada vuelta (sin cortar en el primero que no está listo): el
        // poll también empuja el paso "llegó a Playing → pausar y buscar A" de cada uno.
        var ready = true;
        foreach (var sector in _targets) ready &= sector.PollRunOnceReady();
        var timedOut = Environment.TickCount64 >= _deadline;
        if (!ready && !timedOut) return;

        _poll.Stop();
        // La largada: un solo bucle, en esta misma vuelta del Dispatcher.
        foreach (var sector in _targets) sector.GoRunOnce();
        DiagLog.Write($"runonce go n={_targets.Count} timeout={timedOut}");
        _targets = [];
    }

    public void Stop()
    {
        _poll.Stop();
        _targets = [];
    }
}
