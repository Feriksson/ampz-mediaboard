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

    /// <summary>
    /// Enfriamiento tras RELANZAR el clip (final de loop). Es más largo que el del seek porque
    /// acá VLC tiene que volver a ABRIR el archivo: durante ese rato `Time` devuelve 0 y, sin un
    /// margen más generoso, el caso "estoy antes del marker A" leería ese 0 como que el salto no
    /// funcionó y relanzaría de nuevo — el clip nunca terminaría de abrir.
    /// </summary>
    private const long RelaunchCooldownMs = 500;

    private long _lastSeekTick;

    /// <summary>Cuánto dura el enfriamiento en curso. Lo fija quien haya movido el playhead.</summary>
    private long _seekCooldownMs = ReseekCooldownMs;

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
    /// Volumen del sector, 0..100. Es POR SECTOR y no global: en un board con varios clips
    /// corriendo, lo normal es querer escuchar uno solo y tener el resto de fondo o en silencio.
    /// </summary>
    [ObservableProperty] private int _volume = 100;

    /// <summary>
    /// Silencio del sector. Va aparte del volumen a propósito: mutear y volver NO te hace
    /// perder el nivel que habías ajustado, que es lo que pasaría si mutear fuera "volumen a 0".
    /// </summary>
    [ObservableProperty] private bool _isMuted;

    partial void OnVolumeChanged(int value) => ApplyAudio();

    partial void OnIsMutedChanged(bool value) => ApplyAudio();

    /// <summary>
    /// Empuja volumen y mute al reproductor.
    ///
    /// ⚠ VLC no acepta el volumen hasta que la salida de audio existe, y esa salida se crea
    /// recién cuando el input abrió. Por eso esto se llama en TRES momentos: al cambiar la
    /// propiedad, al arrancar la reproducción, y en el tick donde la duración se conoce por
    /// primera vez (que es la señal de que el media ya abrió de verdad). Llamarlo una sola vez
    /// al cargar deja el nivel sin aplicar y el sector suena siempre a 100.
    /// </summary>
    private void ApplyAudio()
    {
        if (Player is null) return;
        Player.Volume = Math.Clamp(Volume, 0, 100);
        Player.Mute = IsMuted;
    }

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
        ApplyAudio();
    }

    /// <summary>
    /// Foto de lo que hace único a este sector: el media y todos sus ajustes. Se usa para
    /// MOVER contenido de un sector a otro arrastrando.
    ///
    /// Se mueve la DESCRIPCIÓN del media, no el MediaPlayer: un reproductor de VLC está atado a
    /// la ventana de salida donde arrancó, así que pasarlo de un sector a otro lo dejaría
    /// dibujando en el HWND equivocado (mismo motivo por el que existe Remount). Recargar el
    /// clip en destino cuesta un re-decode, y es una acción deliberada del usuario: se banca.
    /// </summary>
    public readonly record struct MediaSnapshot(
        string? Path,
        string? MissingPath,
        double PositionMs,
        double LoopStartMs,
        double LoopEndMs,
        bool LoopEnabled,
        int Volume,
        bool IsMuted);

    public MediaSnapshot TakeSnapshot() => new(
        MediaPath, MissingPath, PositionMs, LoopStartMs, LoopEndMs, LoopEnabled, Volume, IsMuted);

    /// <summary>Aplica una foto tomada con <see cref="TakeSnapshot"/>. Vacía el sector si la foto está vacía.</summary>
    public void Restore(MediaSnapshot snapshot)
    {
        Unload();

        Volume = snapshot.Volume;
        IsMuted = snapshot.IsMuted;

        if (snapshot.MissingPath is { Length: > 0 } missing)
        {
            MarkMissing(missing);
        }
        else if (snapshot.Path is { Length: > 0 } path)
        {
            // Se retoma desde donde iba: mover un clip de celda no debería costarte el punto
            // en el que estabas mirando.
            Load(path, snapshot.PositionMs);
        }
        else
        {
            return; // La foto estaba vacía: el sector queda vacío y listo.
        }

        LoopStartMs = snapshot.LoopStartMs;
        LoopEndMs = snapshot.LoopEndMs;
        LoopEnabled = snapshot.LoopEnabled;
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
    /// Pone un archivo en el sector haciendo lo correcto según su estado.
    ///
    /// Es EL punto de entrada para "cargar este path acá", venga de donde venga: soltar un
    /// archivo, el botón de elegir, o pegar con Ctrl+V. Centraliza la única regla que hay que
    /// respetar siempre — si el sector estaba huérfano se RE-VINCULA (conservando los markers
    /// que te costó ajustar), y si tenía otro clip se REEMPLAZA (y ahí los markers sí se
    /// resetean: son de otro video, mantenerlos marcaría una zona sin sentido).
    ///
    /// Antes esta decisión estaba repetida en cada punto de entrada, que es exactamente como se
    /// termina con un camino que se olvidó de conservar los markers.
    /// </summary>
    public void Adopt(string path)
    {
        if (IsMissing) Relink(path);
        else Load(path);
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
            // relanzarlo. Va por RestartFrom y no por Stop+Play a secas por el mismo motivo que
            // el loop: un Play() sin media nuevo re-aplica el `:start-time` viejo.
            if (Player.State is VLCState.Ended or VLCState.Stopped)
            {
                RestartFrom(LoopEnabled ? LoopStartMs : 0);
            }
            else
            {
                Player.SetPause(false);
            }
            IsPlaying = true;
        }
    }

    /// <summary>
    /// Relanza el clip desde <paramref name="ms"/> construyendo un Media NUEVO.
    ///
    /// ⚠ Esto NO es lo mismo que `Player.Stop(); Player.Play();`, y confundirlos fue un bug
    /// reportado ("después de mover el clip de celda, al terminar vuelve a arrancar en cualquier
    /// lado y no respeta el marker"). Motivo: la opción `:start-time` que usan Restore/Remount
    /// para retomar el clip donde iba vive en el OBJETO MEDIA, no en la llamada a Play(). Un
    /// Play() sin argumentos relanza ESE MISMO media → VLC vuelve a aplicar el `:start-time` y
    /// el clip reinicia en la posición vieja en vez de en el marker A. Con A en 0 no había ni
    /// seek correctivo que lo salvara, así que el clip quedaba loopeando desde un punto
    /// arbitrario para siempre.
    ///
    /// Con un Media nuevo el punto de arranque lo decidimos NOSOTROS en cada vuelta, y además
    /// lo resuelve VLC al abrir el archivo — no depende de que un seek post-Play prenda (que es
    /// justo lo que no se puede garantizar recién relanzado).
    /// </summary>
    private void RestartFrom(double ms)
    {
        if (Player is null || MediaPath is not { } path) return;

        var media = new LibVLCSharp.Shared.Media(VlcEngine.Instance, new Uri(path));
        if (ms > 0)
        {
            // InvariantCulture obligatorio: ver Load(). ":start-time=12,4" para VLC es basura.
            var seconds = (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
            media.AddOption($":start-time={seconds}");
        }

        // Stop() antes de Play() es obligatorio: desde el estado Ended, Play() solo no rearranca.
        Player.Stop();
        Player.Play(media);
        media.Dispose(); // libvlc se queda con su propia referencia.

        PositionMs = ms;
        _lastSeekTick = Environment.TickCount64;
        _seekCooldownMs = RelaunchCooldownMs;
        IsPlaying = true;
        ApplyAudio();
    }

    public void SeekTo(double ms)
    {
        if (Player is null) return;
        var clamped = Math.Clamp(ms, 0, DurationMs > 0 ? DurationMs : ms);
        Player.Time = (long)clamped;
        PositionMs = clamped;
        _lastSeekTick = Environment.TickCount64;
        _seekCooldownMs = ReseekCooldownMs;
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

            // El media acaba de abrir de verdad: recién ahora VLC tiene salida de audio y
            // acepta el volumen. Ver ApplyAudio.
            ApplyAudio();
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

        if (Environment.TickCount64 - _lastSeekTick < _seekCooldownMs) return;

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
            // relanzarlo, y SIEMPRE con un media nuevo apuntando al marker A — ver RestartFrom
            // para por qué reusar el media viejo reiniciaba el clip en una posición arbitraria.
            RestartFrom(start);
            return;
        }

        SeekTo(start);
    }

    public void Dispose() => Unload();
}
