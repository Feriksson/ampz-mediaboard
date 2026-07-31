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
/// Hay DOS destinos, y no son lo mismo:
/// · <b>Sesión</b> (`%APPDATA%\board.json`) — se escribe sola al cerrar. Su única función es que
///   no pierdas el trabajo. Recuerda además QUÉ archivo tenías abierto.
/// · <b>Archivo `.mboard`</b> — un documento del usuario, con nombre y ubicación propios. Solo se
///   escribe cuando el usuario lo pide explícitamente.
///
/// Patrón de la app: Load y Save JAMÁS voltean la app. Si el JSON está corrupto o el disco falla,
/// degradás a un board vacío y seguís laburando.
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
    }

    /// <summary>Envoltorio del estado de sesión: el board MÁS el archivo que estaba abierto.</summary>
    private sealed class SessionDto
    {
        public NodeDto? Board { get; set; }
        public string? CurrentFile { get; set; }
    }

    /// <summary>Lo que devuelve la restauración de sesión.</summary>
    public readonly record struct Session(LayoutNode? Root, string? CurrentFile);

    #region Sesión (%APPDATA%)

    public static void SaveSession(LayoutNode root, string? currentFile)
    {
        try
        {
            AppPaths.EnsureDataDir();
            var dto = new SessionDto { Board = ToDto(root), CurrentFile = currentFile };
            File.WriteAllText(AppPaths.BoardFile, JsonSerializer.Serialize(dto, Options));
        }
        catch
        {
            // Perder el guardado es molesto; tumbar la app con el trabajo del usuario adentro
            // es inaceptable.
        }
    }

    public static Session LoadSession()
    {
        try
        {
            if (!File.Exists(AppPaths.BoardFile)) return default;
            var json = File.ReadAllText(AppPaths.BoardFile);

            var session = JsonSerializer.Deserialize<SessionDto>(json, Options);
            if (session?.Board is not null)
                return new Session(FromDto(session.Board), session.CurrentFile);

            // Compatibilidad con el formato viejo, donde el archivo de sesión era un NodeDto
            // pelado sin envoltorio. Son cinco líneas que evitan que quien venía usando la app
            // pierda su board la primera vez que abre esta versión.
            var legacy = JsonSerializer.Deserialize<NodeDto>(json, Options);
            return legacy is null ? default : new Session(FromDto(legacy), null);
        }
        catch
        {
            return default;
        }
    }

    #endregion

    #region Archivos .mboard

    /// <summary>
    /// El board serializado tal cual se escribiría a un archivo.
    ///
    /// Existe para poder detectar cambios sin guardar COMPARANDO CONTRA EL ARCHIVO, en vez de
    /// mantener un flag "dirty". Un flag obligaría a observar cada mutación posible (cargar un
    /// clip, mover un marker, partir un sector, arrastrar un splitter…) y alcanza con que se
    /// escape una para que el aviso mienta. Comparar el resultado no puede equivocarse.
    /// </summary>
    public static string Serialize(LayoutNode root) => JsonSerializer.Serialize(ToDto(root), Options);

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
    public static bool MatchesFile(string path, LayoutNode root)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<NodeDto>(File.ReadAllText(path), Options);
            if (dto is null) return true;

            return string.Equals(Serialize(root), JsonSerializer.Serialize(dto, Options), StringComparison.Ordinal);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Guarda el board en un archivo del usuario. Devuelve el error si falló, o null si salió bien.</summary>
    public static string? SaveTo(string path, LayoutNode root)
    {
        try
        {
            File.WriteAllText(path, Serialize(root));
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
    public static LayoutNode? LoadFrom(string path)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<NodeDto>(File.ReadAllText(path), Options);
            return dto is null ? null : FromDto(dto);
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

        var sector = new SectorNode { LoopEnabled = dto.LoopEnabled };

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
