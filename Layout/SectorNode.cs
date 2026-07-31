using System.Globalization;
using System.Windows.Media.Imaging;
using AmpzMediaBoard.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LibVLCSharp.Shared;

namespace AmpzMediaBoard.Layout;

/// <summary>
/// HOJA del árbol de layout: un sector de la pantalla. Es a la vez el nodo de layout y el
/// ViewModel del media que contiene — decisión deliberada de NO partirlo en dos objetos
/// espejados que habría que mantener sincronizados a mano por cero beneficio real.
///
/// Un sector vacío es un sector válido: el board arranca con uno solo, vacío.
/// </summary>
public sealed partial class SectorNode : LayoutNode, IDisposable
{
    /// <summary>
    /// Margen de seguridad del loop, en ms. El polling corre cada ~30ms, así que si esperáramos
    /// a que Time supere EXACTO el marker B nos pasaríamos de largo hasta 30ms. Disparamos un
    /// poco antes para que el corte se sienta en el punto marcado.
    /// </summary>
    private const double LoopGuardMs = 40;

    /// <summary>
    /// Enfriamiento entre saltos de loop. Sin esto, una zona de loop más corta que la latencia
    /// del seek de VLC entra en un bucle de saltos y el video queda congelado tartamudeando.
    /// </summary>
    private const long ReseekCooldownMs = 150;

    private long _lastSeekTick;

    /// <summary>
    /// El media abierto pero TODAVÍA NO reproducido. Ver <see cref="StartPending"/>: la
    /// reproducción se arranca recién cuando la vista enganchó el reproductor a su superficie.
    /// </summary>
    private LibVLCSharp.Shared.Media? _pending;

    [ObservableProperty] private string? _mediaPath;
    [ObservableProperty] private MediaKind _kind = MediaKind.None;
    [ObservableProperty] private string _title = "Sector vacío";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isSelected;

    /// <summary>Duración total del clip en ms. 0 hasta que VLC termina de abrir el archivo.</summary>
    [ObservableProperty] private double _durationMs;

    /// <summary>Posición actual del playhead en ms. La refresca el tick del board.</summary>
    [ObservableProperty] private double _positionMs;

    [ObservableProperty] private double _loopStartMs;
    [ObservableProperty] private double _loopEndMs;

    /// <summary>Si está en false, el clip corre de punta a punta ignorando los markers.</summary>
    [ObservableProperty] private bool _loopEnabled = true;

    /// <summary>El MediaPlayer de VLC. Uno por sector con video; null si el sector está vacío o tiene imagen.</summary>
    [ObservableProperty] private MediaPlayer? _player;

    /// <summary>Fuente para los sectores de imagen. null si el sector no es una imagen.</summary>
    [ObservableProperty] private BitmapImage? _imageSource;

    /// <summary>
    /// Path de un archivo que el board recuerda pero que YA NO ESTÁ en el disco.
    ///
    /// Esto NO es un detalle cosmético: sin este campo, un clip movido o borrado dejaba el sector
    /// vacío y el path se perdía. Como el board se autoguarda al cerrar, bastaba abrir la app UNA
    /// vez con el archivo ausente para borrar para siempre la referencia Y los markers de loop
    /// que te había costado ajustar. El board se degradaba solo, en silencio.
    /// Ahora la referencia SOBREVIVE: el sector muestra qué falta y te deja re-vincularlo.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMissing))]
    private string? _missingPath;

    public bool IsMissing => MissingPath is not null;

    public bool HasMedia => Kind != MediaKind.None;

    /// <summary>
    /// Carga un archivo en el sector, reemplazando lo que hubiera.
    /// <paramref name="startAtMs"/> arranca el clip desde ese punto (lo usa el re-montaje al
    /// reconstruir el layout).
    /// </summary>
    public void Load(string path, double startAtMs = 0)
    {
        Unload();

        var kind = MediaKinds.FromPath(path);
        if (kind == MediaKind.None) return;

        MediaPath = path;
        Kind = kind;
        Title = Path.GetFileName(path);

        if (kind == MediaKind.Image)
        {
            LoadImage(path);
            return;
        }

        // ⚠ ACÁ NO SE LLAMA A Play(). Se prepara el reproductor y el media, y se deja PENDIENTE.
        // Motivo, y es de los que se pagan caro: si le pedís Play() a libvlc sin haberle
        // asignado un HWND de salida, VLC no falla — ABRE SU PROPIA VENTANA y el video termina
        // flotando FUERA de la app. Eso es exactamente lo que pasaba al restaurar un board
        // guardado, porque ahí el Load() corre antes de que exista el VideoView.
        // El Play() lo dispara la vista vía StartPending(), ya con la superficie enganchada.
        var player = new MediaPlayer(VlcEngine.Instance) { EnableMouseInput = false, EnableKeyInput = false };
        _pending = new LibVLCSharp.Shared.Media(VlcEngine.Instance, new Uri(path));

        if (startAtMs > 0)
        {
            // El punto de arranque se pasa como OPCIÓN DEL MEDIA y no como un seek después del
            // Play(). Un seek inmediato al Play todavía encuentra el input sin abrir del todo y
            // se ignora la mitad de las veces; :start-time lo resuelve VLC al abrir el archivo.
            //
            // ⚠ InvariantCulture NO es opcional: en un Windows en español el separador decimal
            // es la coma, y ":start-time=12,4" para VLC es basura. Perderías la posición sin
            // ningún error visible.
            var seconds = (startAtMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
            _pending.AddOption($":start-time={seconds}");
        }

        Player = player;
        IsPlaying = false;

        // La duración todavía no se conoce (VLC recién está abriendo el archivo). Los markers
        // se inicializan a "clip entero" en el primer tick que ya tenga Length válido.
        DurationMs = 0;
        LoopStartMs = 0;
        LoopEndMs = 0;
        OnPropertyChanged(nameof(HasMedia));
    }

    /// <summary>
    /// Arranca la reproducción que <see cref="Load"/> dejó pendiente. La llama la VISTA, y solo
    /// después de haber enganchado el MediaPlayer a su superficie de video — que es la única
    /// forma de garantizar que VLC dibuje ADENTRO de la app y no en una ventana propia.
    /// Es idempotente: llamarla dos veces no reinicia nada.
    /// </summary>
    public void StartPending()
    {
        if (_pending is null || Player is null) return;

        var media = _pending;
        _pending = null;

        Player.Play(media);
        // Después del Play, libvlc se queda con su propia referencia al media: soltar la nuestra
        // acá es correcto y evita filtrar un objeto nativo por cada clip cargado.
        media.Dispose();

        IsPlaying = true;
    }

    private void LoadImage(string path)
    {
        // CacheOption.OnLoad es OBLIGATORIO acá: sin él, WPF deja el archivo ABIERTO mientras la
        // imagen viva, y el usuario no puede mover ni borrar el archivo mientras el board esté
        // abierto. Con OnLoad se lee entero a memoria y se suelta el handle al instante.
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze(); // Freeze = compartible entre threads y más barato de renderizar.

        ImageSource = bmp;
        OnPropertyChanged(nameof(HasMedia));
    }

    /// <summary>
    /// Marca el sector como "el archivo que tenía ya no está". Conserva el path y el nombre para
    /// que puedas ver QUÉ falta, y los markers de loop quedan intactos: si volvés a vincular el
    /// mismo clip, la zona que marcaste sigue ahí.
    /// </summary>
    public void MarkMissing(string path)
    {
        Unload();
        MissingPath = path;
        Title = Path.GetFileName(path);
    }

    /// <summary>
    /// Re-vincula un sector huérfano a un archivo, conservando sus markers de loop. Es la razón
    /// de ser de todo el mecanismo de "missing": moviste la carpeta, apuntás de nuevo al clip y
    /// no perdiste el trabajo de marcar la zona.
    /// </summary>
    public void Relink(string path)
    {
        var loopStart = LoopStartMs;
        var loopEnd = LoopEndMs;
        var loopEnabled = LoopEnabled;

        Load(path);

        LoopStartMs = loopStart;
        LoopEndMs = loopEnd;
        LoopEnabled = loopEnabled;
    }

    /// <summary>
    /// Re-monta el clip conservando el punto donde iba y los markers de loop.
    ///
    /// ⚠ Esto existe por una limitación REAL del VideoView, no por capricho: libvlc fija su
    /// ventana de salida en el momento del Play(). Cambiarle el HWND a un reproductor que YA
    /// está corriendo NO mueve el video — sigue dibujando en la ventana vieja. Y al reconstruir
    /// el layout (partir o cerrar un sector) los VideoView se destruyen y se crean de nuevo.
    /// Sin este re-montaje, partir un sector deja su clip dibujando en una ventana muerta.
    ///
    /// El día que migremos a custom rendering (WriteableBitmap) esto SE BORRA: sin HWND de por
    /// medio, re-parentar la superficie es gratis y el clip ni se entera.
    ///
    /// El clip vuelve REPRODUCIENDO aunque estuviera pausado. Es deliberado: re-montar pausado
    /// obliga a VLC a decodificar un frame para mostrarlo, y la mitad de las veces te deja el
    /// sector en negro — peor que la pequeña sorpresa de que arranque.
    /// </summary>
    public void Remount()
    {
        if (Kind != MediaKind.Video || MediaPath is not { } path) return;

        var position = PositionMs;
        var duration = DurationMs;
        var loopStart = LoopStartMs;
        var loopEnd = LoopEndMs;
        var loopEnabled = LoopEnabled;

        Load(path, position);

        // Load() resetea el estado del clip; los markers son del USUARIO y tienen que sobrevivir
        // a un cambio de layout. La duración se restaura para que la timeline no parpadee a cero
        // mientras VLC vuelve a abrir el archivo.
        DurationMs = duration;
        LoopStartMs = loopStart;
        LoopEndMs = loopEnd;
        LoopEnabled = loopEnabled;
    }

    /// <summary>Vacía el sector y libera el reproductor.</summary>
    public void Unload()
    {
        _pending?.Dispose();
        _pending = null;

        var player = Player;
        Player = null;

        if (player is not null)
        {
            // Stop() antes de Dispose(): soltar el player mientras decodifica deja el hilo de
            // VLC trabajando sobre memoria liberada.
            player.Stop();
            player.Dispose();
        }

        ImageSource = null;
        MediaPath = null;
        MissingPath = null;
        Kind = MediaKind.None;
        Title = "Sector vacío";
        IsPlaying = false;
        DurationMs = 0;
        PositionMs = 0;
        LoopStartMs = 0;
        LoopEndMs = 0;
        OnPropertyChanged(nameof(HasMedia));
    }

    public void TogglePlay()
    {
        if (Player is null) return;

        // Clip cargado pero nunca arrancado (la vista todavía no lo disparó): el primer play
        // es el arranque, no un "reanudar".
        if (_pending is not null)
        {
            StartPending();
            return;
        }

        if (Player.IsPlaying)
        {
            Player.SetPause(true);
            IsPlaying = false;
        }
        else
        {
            // Si el clip terminó, VLC queda en estado Ended y Play() no reanuda: hay que
            // reposicionarlo primero. Arrancamos desde el inicio de la zona de loop.
            if (Player.State is VLCState.Ended or VLCState.Stopped)
            {
                Player.Stop();
                Player.Play();
                SeekTo(LoopEnabled ? LoopStartMs : 0);
            }
            else
            {
                Player.SetPause(false);
            }
            IsPlaying = true;
        }
    }

    public void SeekTo(double ms)
    {
        if (Player is null) return;
        var clamped = Math.Clamp(ms, 0, DurationMs > 0 ? DurationMs : ms);
        Player.Time = (long)clamped;
        PositionMs = clamped;
        _lastSeekTick = Environment.TickCount64;
    }

    /// <summary>
    /// Latido del sector: lo llama el ÚNICO timer del board (no uno por celda — 8 DispatcherTimer
    /// compitiendo por la cola del Dispatcher es peor que uno que itera 8 sectores).
    /// Acá pasan dos cosas: refrescar la posición para la timeline, y hacer cumplir la zona de loop.
    /// </summary>
    public void Tick()
    {
        var player = Player;
        if (player is null || Kind != MediaKind.Video) return;

        // Length llega en -1 hasta que VLC terminó de abrir el archivo. En cuanto es válido,
        // fijamos la duración y, si el usuario todavía no marcó nada, la zona de loop arranca
        // cubriendo el CLIP ENTERO — un clip recién soltado tiene que loopear de punta a punta
        // sin que toques un solo marker.
        var length = player.Length;
        if (length > 0 && Math.Abs(DurationMs - length) > 1)
        {
            DurationMs = length;
            if (LoopEndMs <= LoopStartMs) LoopEndMs = length;
        }

        var ended = player.State is VLCState.Ended or VLCState.Stopped;

        // ⚠ Time devuelve -1 cuando el clip TERMINÓ. Colapsarlo a 0 (como se hacía antes) era un
        // bug feo: con la posición en cero, "¿pasé el marker B?" da falso y "¿estoy antes del
        // marker A?" también (A suele estar en 0) → NINGUNA condición del loop se cumplía y el
        // clip se quedaba muerto al final. Se manifestaba justo con la zona por defecto (todo el
        // clip), y desaparecía al mover B hacia adentro, porque ahí el playhead cruza B ANTES de
        // que VLC llegue a Ended.
        var time = player.Time;
        if (time >= 0) PositionMs = time;
        else if (ended && DurationMs > 0) PositionMs = DurationMs;

        IsPlaying = player.IsPlaying;

        EnforceLoop(ended);
    }

    private void EnforceLoop(bool ended)
    {
        if (!LoopEnabled || Player is null || DurationMs <= 0) return;

        // La zona se sanea en cada vuelta en vez de confiar en los campos crudos: si LoopEnd
        // todavía no se inicializó, o quedó fuera de rango por un board editado a mano, el loop
        // igual funciona sobre el clip entero en vez de no hacer nada.
        var start = Math.Clamp(LoopStartMs, 0, DurationMs);
        var end = LoopEndMs > start ? Math.Min(LoopEndMs, DurationMs) : DurationMs;

        if (Environment.TickCount64 - _lastSeekTick < ReseekCooldownMs) return;

        // TRES casos disparan la vuelta al marker A:
        //  1) el clip TERMINÓ — el caso de la zona por defecto, donde el playhead nunca llega a
        //     "cruzar" B porque VLC corta el stream primero;
        //  2) el playhead llegó (o está por llegar) al marker B — el loop normal, a mitad de clip;
        //  3) el playhead quedó ANTES del marker A — pasa al arrastrar A hacia adelante mientras
        //     reproduce. Sin este caso el video seguiría corriendo fuera de la zona marcada.
        var pastEnd = PositionMs >= end - LoopGuardMs;
        var beforeStart = PositionMs < start - LoopGuardMs;
        if (!ended && !pastEnd && !beforeStart) return;

        if (ended)
        {
            // Con el clip terminado, VLC NO acepta un seek: el input está cerrado. Hay que
            // relanzarlo. Stop() antes de Play() es obligatorio — desde el estado Ended, Play()
            // solo no rearranca.
            Player.Stop();
            Player.Play();
            _lastSeekTick = Environment.TickCount64;
            IsPlaying = true;

            // Play() ya arranca desde cero, así que con el marker A en 0 (el caso por defecto)
            // no hace falta seek y el loop queda limpio. Si A está más adelante, el seek recién
            // relanzado puede ignorarse porque el input todavía no abrió — no importa: el caso 3
            // (beforeStart) lo vuelve a intentar en el tick siguiente hasta que prende.
            if (start > 0) SeekTo(start);
            return;
        }

        SeekTo(start);
    }

    public void Dispose() => Unload();
}
