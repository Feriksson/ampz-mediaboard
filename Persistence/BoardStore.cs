using System.Text.Json;
using System.Text.Json.Serialization;
using AmpzMediaBoard.Layout;

namespace AmpzMediaBoard.Persistence;

/// <summary>
/// Serialización del board: el ÁRBOL de layout + qué archivo hay en cada sector + los markers de
/// loop de cada clip.
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
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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

        // --- board (SOLO en el nodo raíz del árbol de UN board) ---

        /// <summary>
        /// Volumen general del board. Es un dato del BOARD, no del archivo: vive en el nodo raíz
        /// del árbol de ese board, que HOY coincide con la raíz del `.mboard` porque el archivo
        /// guarda un solo board. Así, cuando el archivo pase a declarar varias pestañas (rediseño
        /// pendiente: un archivo para todas), cada entrada de pestaña se lleva su master adentro
        /// sin moverlo de lugar. Y no se agregó un sobre alrededor del árbol a propósito: el
        /// formato sigue siendo el de siempre y los archivos viejos se leen sin migración.
        ///
        /// ⚠ Nullable y se escribe SOLO si difiere de 100 (ver <see cref="MasterOrNull"/>). No es
        /// ahorro de bytes: si se escribiera siempre, un `.mboard` viejo —que no lo tiene—
        /// serializaría distinto que el mismo board en memoria, y abrirlo y cerrarlo sin tocar
        /// nada preguntaría "¿guardar los cambios?". Un aviso que miente es un aviso que el
        /// usuario aprende a ignorar.
        /// </summary>
        public int? MasterVolume { get; set; }
    }

    /// <summary>El master como se escribe: null (= ausente) cuando es el 100 por defecto.</summary>
    private static int? MasterOrNull(int? master) =>
        master is { } m && Math.Clamp(m, 0, 100) != 100 ? Math.Clamp(m, 0, 100) : null;

    #region Archivos .mboard

    /// <summary>
    /// El board serializado tal cual se escribiría a un archivo.
    ///
    /// Existe para poder detectar cambios sin guardar COMPARANDO CONTRA EL ARCHIVO, en vez de
    /// mantener un flag "dirty". Un flag obligaría a observar cada mutación posible (cargar un
    /// clip, mover un marker, partir un sector, arrastrar un splitter…) y alcanza con que se
    /// escape una para que el aviso mienta. Comparar el resultado no puede equivocarse.
    /// </summary>
    public static string Serialize(LayoutNode root, int masterVolume = 100)
    {
        var dto = ToDto(root);
        dto.MasterVolume = MasterOrNull(masterVolume);
        return JsonSerializer.Serialize(dto, Options);
    }

    /// <summary>
    /// ¿El board en memoria coincide con lo que hay guardado en el archivo? Lo usa el aviso de
    /// cambios sin guardar. Ante cualquier duda (archivo ilegible, corrupto) devuelve true: no
    /// vamos a trabarle el cierre al usuario por un problema de disco.
    ///
    /// ⚠ El contenido del archivo se NORMALIZA antes de comparar (se deserializa y se vuelve a
    /// serializar con las mismas opciones). Comparar el texto crudo sería sensible al FORMATO:
    /// un `.mboard` escrito compacto, o por una versión anterior con otro formateo, se leería
    /// como "modificado" sin que nadie haya tocado nada — y el usuario aprendería a ignorar el
    /// aviso, que es la peor forma de romper una advertencia.
    /// </summary>
    public static bool MatchesFile(string path, LayoutNode root, int masterVolume = 100) =>
        ReadNormalized(path) is not { } saved ||
        string.Equals(Serialize(root, masterVolume), saved, StringComparison.Ordinal);

    /// <summary>
    /// El contenido del archivo normalizado (deserializado y vuelto a serializar con las mismas
    /// opciones que <see cref="Serialize"/>), o null si no se pudo leer.
    ///
    /// Existe aparte de <see cref="MatchesFile"/> para que la marca "•" de cambios sin guardar
    /// de las pestañas pueda comparar contra una foto EN MEMORIA tomada al abrir/guardar, en vez
    /// de leer el disco cada medio segundo. Es la MISMA comparación: misma normalización, mismo
    /// resultado — solo cambia cuándo se lee el archivo.
    /// </summary>
    public static string? ReadNormalized(string path)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<NodeDto>(File.ReadAllText(path), Options);
            if (dto is null) return null;

            // La MISMA regla que al escribir: un "MasterVolume": 100 explícito (board editado a
            // mano) y un archivo viejo sin el campo describen el mismo board.
            dto.MasterVolume = MasterOrNull(dto.MasterVolume);
            return JsonSerializer.Serialize(dto, Options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Guarda el board en un archivo del usuario. Devuelve el error si falló, o null si salió bien.</summary>
    public static string? SaveTo(string path, LayoutNode root, int masterVolume = 100)
    {
        try
        {
            File.WriteAllText(path, Serialize(root, masterVolume));
            return null;
        }
        catch (Exception ex)
        {
            // Acá SÍ se devuelve el error, a diferencia de la sesión: el usuario pidió guardar
            // explícitamente y tiene que enterarse si no se pudo. Un "guardar" que falla en
            // silencio es la peor mentira que le podés decir.
            return ex.Message;
        }
    }

    /// <summary>Carga un board desde un archivo. Devuelve null si no se pudo leer o parsear.</summary>
    public static LayoutNode? LoadFrom(string path) => LoadFrom(path, out _);

    /// <summary>
    /// Carga un board y además su volumen general. Un archivo sin el campo (anterior a que
    /// existiera) carga con 100: el board suena exactamente como sonaba cuando se guardó.
    /// </summary>
    public static LayoutNode? LoadFrom(string path, out int masterVolume)
    {
        masterVolume = 100;
        try
        {
            var dto = JsonSerializer.Deserialize<NodeDto>(File.ReadAllText(path), Options);
            if (dto is null) return null;
            masterVolume = MasterOrNull(dto.MasterVolume) ?? 100;
            return FromDto(dto);
        }
        catch
        {
            return null;
        }
    }

    #endregion

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
