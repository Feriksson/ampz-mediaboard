using System.Text.Json;
using System.Text.Json.Serialization;
using AmpzMediaBoard.Layout;

namespace AmpzMediaBoard.Persistence;

/// <summary>
/// Persistencia del board: el ÁRBOL de layout + qué archivo hay en cada sector + los markers de
/// loop de cada clip.
///
/// Se serializa con DTOs propios y NO con el árbol de dominio directo. ¿Por qué el paso extra?
/// Porque <see cref="SectorNode"/> arrastra un MediaPlayer de VLC, un BitmapImage y un puntero al
/// padre — un ciclo y un montón de estado nativo que ningún serializador puede ni debe tocar.
/// Los DTOs son planos, tontos, y describen exactamente lo que queremos que sobreviva.
///
/// Patrón de la app: Load() y Save() JAMÁS voltean la app. Si el JSON está corrupto o el disco
/// falla, degradás a un board vacío y seguís laburando.
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

    public static void Save(LayoutNode root)
    {
        try
        {
            AppPaths.EnsureDataDir();
            var json = JsonSerializer.Serialize(ToDto(root), Options);
            File.WriteAllText(AppPaths.BoardFile, json);
        }
        catch
        {
            // Si no se puede guardar, el board sigue vivo en memoria. Perder el guardado es
            // molesto; tumbar la app con el trabajo del usuario adentro es inaceptable.
        }
    }

    /// <summary>Devuelve null si no hay board guardado o si no se pudo leer.</summary>
    public static LayoutNode? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.BoardFile)) return null;
            var dto = JsonSerializer.Deserialize<NodeDto>(File.ReadAllText(AppPaths.BoardFile), Options);
            return dto is null ? null : FromDto(dto);
        }
        catch
        {
            return null;
        }
    }

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
        // y los markers. Así se puede re-vincular sin volver a marcar la zona de loop, y el
        // autoguardado al cerrar NO borra la referencia.
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
