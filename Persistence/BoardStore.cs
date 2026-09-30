using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AmpzMediaBoard.Layout;

namespace AmpzMediaBoard.Persistence;

/// <summary>Una pestaña tal como se guarda: su nombre, su árbol y su volumen general.</summary>
public sealed record TabData(string Name, LayoutNode Root, int MasterVolume = 100);

/// <summary>
/// El panel fijado tal como se guarda: sus sectores de arriba hacia abajo y su ancho como
/// PROPORCIÓN del área de boards (0..1), nunca en píxeles — el archivo se reabre en otra
/// pantalla o con la ventana de otro tamaño. Ver <see cref="AmpzMediaBoard.Board.PinnedDock"/>.
/// </summary>
public sealed record DockData(double Width, IReadOnlyList<SectorNode> Sectors);

/// <summary>
/// Un archivo `.mboard` leído: TODAS sus pestañas, cuál estaba activa al guardarlo y el panel
/// fijado (null si no había ninguno: los archivos anteriores al panel no lo tienen).
/// </summary>
public sealed record DocumentData(IReadOnlyList<TabData> Tabs, int ActiveTab, DockData? Dock = null);

/// <summary>
/// El archivo tal como estaba al abrirlo o guardarlo, NORMALIZADO (ver <see cref="BoardStore.ReadSnapshot"/>):
/// el documento entero (contra esto se decide si hay cambios sin guardar) y cada pestaña por
/// separado (pinta la "•" de la pestaña).
/// </summary>
public sealed record FileSnapshot(string Document, IReadOnlyList<string> Tabs);

/// <summary>
/// Serialización de los boards: UN archivo `.mboard` con TODAS las pestañas de la ventana, cada
/// una con su árbol de layout + qué archivo hay en cada sector + los markers de loop.
///
/// Formato v2 (lo único que se escribe):
/// <code>{ "Version": 2, "ActiveTab": i, "Tabs": [ { "Name", "MasterVolume"?, "Root": {árbol} } ], "Dock"?: { "Width", "Sectors": [ {sector} ] } }</code>
/// "Dock" es del ARCHIVO y no de una pestaña (el panel fijado vive fuera de las pestañas), y se
/// escribe SOLO si el panel tiene algo: un v2 anterior al panel y uno con el panel vacío
/// describen el mismo documento, y tienen que normalizar igual.
/// El formato VIEJO (un solo board: el árbol pelado en la raíz del JSON) se sigue LEYENDO, como
/// un archivo de UNA pestaña con el nombre del archivo. Ver <see cref="ParseFile"/>.
///
/// Se serializa con DTOs propios y NO con el árbol de dominio directo. ¿Por qué el paso extra?
/// Porque <see cref="SectorNode"/> arrastra un MediaPlayer de VLC, un BitmapImage y un puntero al
/// padre — un ciclo y un montón de estado nativo que ningún serializador puede ni debe tocar.
/// Los DTOs son planos, tontos, y describen exactamente lo que queremos que sobreviva.
///
/// ⚠ Hay UN SOLO destino: el archivo `.mboard` del usuario. No existe estado de sesión ni
/// autoguardado escondido — la app **no** escribe nada en `%APPDATA%`. Ver "Arranque limpio" en
/// el CLAUDE.md antes de agregar algo parecido.
///
/// Patrón de la app: leer JAMÁS voltea la app. Si el JSON está corrupto, degradás y seguís
/// laburando. GUARDAR es la excepción: ahí el error se devuelve y se muestra, porque un
/// "guardar" que falla en silencio manda al usuario a dormir tranquilo con el trabajo perdido.
/// </summary>
public static class BoardStore
{
    public const int FormatVersion = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Solo afecta la LECTURA: un archivo tocado a mano con "tabs" en minúscula se lee igual.
        PropertyNameCaseInsensitive = true,
    };

    private sealed class FileDto
    {
        public int Version { get; set; } = FormatVersion;

        /// <summary>
        /// Pestaña visible al guardar. ⚠ Nullable a propósito: en la foto que se usa para
        /// detectar cambios sin guardar va en null (= ausente). Cambiar de pestaña es MIRAR, no
        /// editar; si contara, cada Ctrl+Tab haría preguntar "¿guardar los cambios?" al cerrar, y
        /// un aviso que salta sin motivo es un aviso que el usuario aprende a ignorar.
        /// </summary>
        public int? ActiveTab { get; set; }

        public List<TabDto> Tabs { get; set; } = [];

        /// <summary>El panel fijado. null (= ausente) cuando está vacío: ver <see cref="ToDockDto"/>.</summary>
        public DockDto? Dock { get; set; }
    }

    private sealed class DockDto
    {
        /// <summary>Ancho del panel como proporción del área de boards, redondeado (ver <see cref="NormalizeWidth"/>).</summary>
        public double Width { get; set; }

        /// <summary>Los sectores, de arriba hacia abajo. Siempre hojas: el panel es una pila, no un árbol.</summary>
        public List<NodeDto> Sectors { get; set; } = [];
    }

    private sealed class TabDto
    {
        public string? Name { get; set; }

        /// <summary>
        /// Volumen general del board. ⚠ Se escribe SOLO si difiere de 100 (ver
        /// <see cref="MasterOrNull"/>): un "100" explícito y la ausencia describen el mismo board,
        /// y tienen que normalizar igual para que la comparación de cambios no mienta.
        /// </summary>
        public int? MasterVolume { get; set; }

        public NodeDto? Root { get; set; }
    }

    private sealed class NodeDto
    {
        /// <summary>"split" o "sector". Discriminador explícito: barato de leer a ojo en el JSON.</summary>
        public string Type { get; set; } = "sector";

        // --- split ---
        public string? Orientation { get; set; }
        public double Ratio { get; set; } = 0.5;
        public NodeDto? First { get; set; }
        public NodeDto? Second { get; set; }

        // --- sector ---
        public string? Path { get; set; }
        public double LoopStart { get; set; }
        public double LoopEnd { get; set; }
        public bool LoopEnabled { get; set; } = true;
        public int Volume { get; set; } = 100;
        public bool Muted { get; set; }

        /// <summary>
        /// SOLO en archivos del formato VIEJO: ahí el volumen general vivía en el nodo raíz,
        /// porque el archivo era un único board. Al leerlos se muda a la pestaña
        /// (<see cref="TabDto.MasterVolume"/>) y acá queda en null: el v2 nunca lo escribe.
        /// </summary>
        public int? MasterVolume { get; set; }
    }

    /// <summary>El master como se escribe: null (= ausente) cuando es el 100 por defecto.</summary>
    private static int? MasterOrNull(int? master) =>
        master is { } m && Math.Clamp(m, 0, 100) != 100 ? Math.Clamp(m, 0, 100) : null;

    /// <summary>
    /// Nombre de la pestaña que resulta de abrir un archivo VIEJO: el del archivo. Es la MISMA
    /// función al cargar y al normalizar, y tiene que serlo: si divergieran, un archivo viejo
    /// recién abierto se leería como modificado.
    /// </summary>
    private static string LegacyTabName(string path) => System.IO.Path.GetFileNameWithoutExtension(path);

    /// <summary>Nombre para una pestaña guardada sin nombre (archivo tocado a mano).</summary>
    private static string DefaultName(int index) => $"Board {index + 1}";

    /// <summary>
    /// Ancho del panel como se escribe: acotado (un panel de ancho 0 o del 100% sería
    /// irrecuperable con el mouse) y REDONDEADO a 4 decimales. El redondeo es el mismo al escribir
    /// y al normalizar, así que el archivo y la memoria comparan igual; y un splitter soltado
    /// donde estaba no deja ruido de punto flotante que se lea como "modificado".
    /// </summary>
    public static double NormalizeWidth(double width) =>
        double.IsFinite(width) ? Math.Round(Math.Clamp(width, 0.1, 0.8), 4) : 0.25;

    #region Archivos .mboard

    /// <summary>
    /// El documento serializado tal cual se escribiría a un archivo. Con
    /// <paramref name="activeTab"/> en null es la forma de COMPARAR (ver <see cref="FileDto.ActiveTab"/>).
    ///
    /// Existe para poder detectar cambios sin guardar COMPARANDO CONTRA EL ARCHIVO, en vez de
    /// mantener un flag "dirty". Un flag obligaría a observar cada mutación posible (cargar un
    /// clip, mover un marker, partir un sector, renombrar o cerrar una pestaña…) y alcanza con
    /// que se escape una para que el aviso mienta. Comparar el resultado no puede equivocarse.
    /// </summary>
    public static string Serialize(IReadOnlyList<TabData> tabs, DockData? dock = null, int? activeTab = null) =>
        JsonSerializer.Serialize(ToFileDto(tabs, dock, activeTab), Options);

    /// <summary>Una pestaña sola, en la forma de comparar. Pinta la "•" de esa pestaña.</summary>
    public static string SerializeTab(TabData tab) => JsonSerializer.Serialize(ToTabDto(tab), Options);

    /// <summary>
    /// ¿Las pestañas en memoria coinciden con lo que hay guardado en el archivo? Lo usa el aviso
    /// de cambios sin guardar. Ante cualquier duda (archivo ilegible, corrupto) devuelve true: no
    /// vamos a trabarle el cierre al usuario por un problema de disco.
    /// </summary>
    public static bool MatchesFile(string path, IReadOnlyList<TabData> tabs, DockData? dock = null) =>
        ReadSnapshot(path) is not { } saved ||
        string.Equals(Serialize(tabs, dock), saved.Document, StringComparison.Ordinal);

    /// <summary>
    /// El contenido del archivo NORMALIZADO, en la forma de comparar, o null si no se pudo leer.
    ///
    /// Normalizar = leerlo a DTO (del formato que sea) y volver a serializarlo con las mismas
    /// opciones que <see cref="Serialize"/>. Comparar texto crudo sería sensible al FORMATO: un
    /// `.mboard` compacto o escrito por otra versión se leería "modificado" sin que nadie lo tocó.
    ///
    /// ⚠ Un archivo VIEJO se normaliza a v2 con la MISMA conversión que usa la carga (una
    /// pestaña con el nombre del archivo, el master mudado del nodo raíz a la pestaña). Por eso
    /// un archivo viejo recién abierto y sin tocar compara IGUAL — aunque guardarlo lo reescriba
    /// en otro formato. Si se comparara el texto v1 contra un v2 en memoria, TODO archivo viejo
    /// preguntaría "¿guardar?" al cerrarlo sin haberlo tocado.
    /// </summary>
    public static FileSnapshot? ReadSnapshot(string path)
    {
        try
        {
            if (ParseFile(path) is not { } file) return null;
            file.ActiveTab = null;
            return new FileSnapshot(
                JsonSerializer.Serialize(file, Options),
                file.Tabs.Select(t => JsonSerializer.Serialize(t, Options)).ToList());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Guarda TODAS las pestañas en un archivo del usuario, siempre en formato v2. Devuelve el
    /// error si falló, o null si salió bien.
    /// </summary>
    public static string? SaveTo(string path, IReadOnlyList<TabData> tabs, int activeTab, DockData? dock = null)
    {
        try
        {
            File.WriteAllText(path, Serialize(tabs, dock, activeTab));
            return null;
        }
        catch (Exception ex)
        {
            // Acá SÍ se devuelve el error: el usuario pidió guardar explícitamente y tiene que
            // enterarse si no se pudo. Un "guardar" que falla en silencio es la peor mentira.
            return ex.Message;
        }
    }

    /// <summary>
    /// Carga un archivo: sus pestañas y cuál estaba activa. Devuelve null si no se pudo leer o
    /// parsear. Un archivo viejo carga como UNA pestaña con el nombre del archivo.
    ///
    /// ⚠ Construir el árbol carga los clips (<see cref="SectorNode.Load"/>), pero Load NO
    /// reproduce: cada clip queda PENDIENTE hasta que su pestaña tiene una superficie visible.
    /// Así un archivo de ocho pestañas no pone a decodificar ocho boards al abrirlo.
    /// </summary>
    public static DocumentData? LoadFrom(string path)
    {
        try
        {
            if (ParseFile(path) is not { } file) return null;
            var tabs = file.Tabs
                .Select(t => new TabData(t.Name!, FromDto(t.Root!), t.MasterVolume ?? 100))
                .ToList();
            // Los sectores del panel se construyen igual que los de una pestaña: sus clips quedan
            // PENDIENTES hasta que el panel tiene una superficie visible (bug #1).
            var dock = file.Dock is { } d
                ? new DockData(d.Width, d.Sectors.Select(s => (SectorNode)FromDto(s)).ToList())
                : null;
            return new DocumentData(tabs, Math.Clamp(file.ActiveTab ?? 0, 0, tabs.Count - 1), dock);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lee el archivo a DTO v2, del formato que sea, YA normalizado (nombres, master). Tira si el
    /// JSON es inválido; devuelve null si es JSON pero no es un board.
    ///
    /// El formato se detecta MIRANDO el JSON (¿tiene "Tabs" o "Version" en la raíz?), no
    /// probando un parseo y cayendo al otro en el catch. Probar a ciegas confundiría "es del
    /// formato viejo" con "es un v2 roto", y un v2 roto leído como board viejo cargaría un
    /// board VACÍO en silencio — que después, al guardar, pisaría el archivo bueno.
    /// </summary>
    private static FileDto? ParseFile(string path)
    {
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json) return null;

        var isV2 = json.Any(p =>
            p.Key.Equals(nameof(FileDto.Tabs), StringComparison.OrdinalIgnoreCase) ||
            p.Key.Equals(nameof(FileDto.Version), StringComparison.OrdinalIgnoreCase));

        FileDto file;
        if (isV2)
        {
            file = json.Deserialize<FileDto>(Options) ?? new FileDto();
            // Un v2 sin pestañas no es un board que se pueda mostrar: se trata como corrupto en
            // vez de inventarle una pestaña vacía que al guardar pisaría lo que haya.
            if (file.Tabs.Count == 0 || file.Tabs.Any(t => t.Root is null)) return null;
        }
        else
        {
            var root = json.Deserialize<NodeDto>(Options);
            if (root is null) return null;
            file = new FileDto
            {
                Tabs = [new TabDto { Name = LegacyTabName(path), MasterVolume = root.MasterVolume, Root = root }],
            };
        }

        for (var i = 0; i < file.Tabs.Count; i++)
        {
            var tab = file.Tabs[i];
            if (string.IsNullOrWhiteSpace(tab.Name)) tab.Name = DefaultName(i);
            // La MISMA regla que al escribir: un "MasterVolume": 100 explícito y la ausencia
            // describen el mismo board.
            tab.MasterVolume = MasterOrNull(tab.MasterVolume);
            tab.Root!.MasterVolume = null;
        }

        // El panel: vacío = ausente (la misma regla que al escribir). Sus sectores son SIEMPRE
        // hojas: un "split" metido a mano se descarta en vez de reinterpretarse como otra cosa.
        if (file.Dock is { } dock)
        {
            dock.Sectors = (dock.Sectors ?? []).Where(s => s is not null && s.Type != "split").ToList();
            foreach (var sector in dock.Sectors) sector.MasterVolume = null;
            dock.Width = NormalizeWidth(dock.Width);
            if (dock.Sectors.Count == 0) file.Dock = null;
        }

        file.Version = FormatVersion;
        return file;
    }

    #endregion

    private static FileDto ToFileDto(IReadOnlyList<TabData> tabs, DockData? dock, int? activeTab) => new()
    {
        Version = FormatVersion,
        ActiveTab = activeTab,
        Tabs = tabs.Select(ToTabDto).ToList(),
        Dock = ToDockDto(dock),
    };

    /// <summary>
    /// ⚠ Un panel VACÍO se escribe como AUSENTE, no como <c>{ "Sectors": [] }</c>. Si no, todo
    /// archivo anterior al panel (que no tiene el campo) se leería como modificado al abrirlo:
    /// el documento en memoria diría "panel vacío" y el archivo "no hay panel", que son lo mismo.
    /// </summary>
    private static DockDto? ToDockDto(DockData? dock) =>
        dock is { Sectors.Count: > 0 }
            ? new DockDto { Width = NormalizeWidth(dock.Width), Sectors = dock.Sectors.Select(s => ToDto(s)).ToList() }
            : null;

    private static TabDto ToTabDto(TabData tab) => new()
    {
        Name = tab.Name,
        MasterVolume = MasterOrNull(tab.MasterVolume),
        Root = ToDto(tab.Root),
    };

    private static NodeDto ToDto(LayoutNode node) => node switch
    {
        SplitNode split => new NodeDto
        {
            Type = "split",
            Orientation = split.Orientation.ToString(),
            Ratio = split.Ratio,
            First = ToDto(split.First),
            Second = ToDto(split.Second),
        },
        SectorNode sector => new NodeDto
        {
            Type = "sector",
            // Si el archivo no está, igual se guarda su path: perder la referencia sería perder
            // para siempre qué clip iba ahí y con qué zona de loop. Ver SectorNode.MissingPath.
            Path = sector.MediaPath ?? sector.MissingPath,
            LoopStart = sector.LoopStartMs,
            LoopEnd = sector.LoopEndMs,
            LoopEnabled = sector.LoopEnabled,
            Volume = sector.Volume,
            Muted = sector.IsMuted,
        },
        _ => new NodeDto(),
    };

    private static LayoutNode FromDto(NodeDto dto)
    {
        if (dto.Type == "split" && dto.First is not null && dto.Second is not null)
        {
            var orientation = Enum.TryParse<SplitOrientation>(dto.Orientation, out var o)
                ? o
                : SplitOrientation.Horizontal;

            // Ratio saneado: un JSON tocado a mano con ratio 0 o 1 dejaría un sector de ancho
            // cero, invisible e imposible de recuperar con el mouse.
            var ratio = double.IsFinite(dto.Ratio) ? Math.Clamp(dto.Ratio, 0.05, 0.95) : 0.5;

            return new SplitNode(orientation, FromDto(dto.First), FromDto(dto.Second), ratio);
        }

        var sector = new SectorNode
        {
            LoopEnabled = dto.LoopEnabled,
            // Saneado: un board editado a mano con volumen 300 dejaría el sector fuera de rango.
            Volume = Math.Clamp(dto.Volume, 0, 100),
            IsMuted = dto.Muted,
        };

        if (dto.Path is not { Length: > 0 } path) return sector;

        // El archivo pudo haberse movido, borrado, o estar en un disco externo desconectado.
        // NO se descarta: el sector queda marcado como "falta este archivo", conservando el path
        // y los markers. Así se puede re-vincular sin volver a marcar la zona de loop.
        if (!File.Exists(path))
        {
            sector.MarkMissing(path);
            sector.LoopStartMs = dto.LoopStart;
            sector.LoopEndMs = dto.LoopEnd;
            return sector;
        }

        sector.Load(path);

        // Los markers se restauran DESPUÉS de Load(), que los resetea al clip entero.
        // LoopEnd puede quedar en 0 si el guardado se hizo antes de que VLC informara la
        // duración: en ese caso el primer Tick lo va a completar solo.
        sector.LoopStartMs = dto.LoopStart;
        sector.LoopEndMs = dto.LoopEnd;

        return sector;
    }
}
