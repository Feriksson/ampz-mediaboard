using System.Diagnostics;
using AmpzMediaBoard.Persistence;

namespace AmpzMediaBoard.Media;

/// <summary>
/// Genera (con ffmpeg) y administra los archivos ida y vuelta. La REGLA de qué se genera vive en
/// <see cref="PingPongMath"/>; acá está lo impuro: el proceso, el disco y el caché.
///
/// ⚠ Los archivos van a <c>%TEMP%\AmpzMediaBoard\pingpong\&lt;pid&gt;\</c> y eso NO contradice el
/// "arranque limpio" del CLAUDE.md (nada en %APPDATA%, ningún estado propio). Esto no es ESTADO:
/// es un caché DESCARTABLE que se reconstruye solo a partir del .mboard (que guarda únicamente
/// "PingPong": true). Borrar la carpeta entera en cualquier momento no pierde nada del usuario;
/// a lo sumo cuesta volver a generar. %TEMP% es justamente el lugar que Windows tiene para eso.
///
/// ⚠ Una subcarpeta POR PROCESO, porque la app es multi-instancia a propósito (ver "MULTI-INSTANCIA
/// ES INTENCIONAL"). Con una carpeta compartida, la limpieza de arranque de una segunda ventana
/// borraría los archivos que la primera está reproduciendo o generando. Al arrancar se borran
/// solo las carpetas de procesos que ya no existen (un cierre abrupto no deja basura para siempre).
/// </summary>
public static class PingPongRenderer
{
    /// <summary>
    /// ffmpeg viaja JUNTO A LA APP (<c>ffmpeg\ffmpeg.exe</c>, lo copia el .csproj desde
    /// <c>third_party/ffmpeg/</c>, que baja <c>tools/fetch-ffmpeg.ps1</c>). NUNCA se usa uno del
    /// PATH: el de la máquina puede ser un build GPL, de otra versión o sin el filtro que usamos,
    /// y la feature se comportaría distinto según quién la corra.
    /// </summary>
    public static string BundledFfmpeg { get; } = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");

    /// <summary>
    /// ¿Hay ffmpeg? Se mira UNA vez: el build sin <c>third_party/ffmpeg</c> compila igual y la
    /// feature queda deshabilitada con su explicación (ver SectorView.SyncPingPong).
    /// </summary>
    public static bool IsAvailable { get; } = File.Exists(BundledFfmpeg);

    public static string CacheRoot { get; } = Path.Combine(Path.GetTempPath(), "AmpzMediaBoard", "pingpong");

    /// <summary>La carpeta de ESTE proceso. Ver el resumen sobre multi-instancia.</summary>
    public static string CacheDir { get; } = Path.Combine(CacheRoot, Environment.ProcessId.ToString());

    /// <summary>
    /// UNA generación a la vez en toda la app. El <c>reverse</c> tiene un pico de RAM de hasta
    /// ~1,2 GB (ver <see cref="PingPongMath.MaxZoneMs"/>): abrir un board con ocho sectores en
    /// ping-pong no puede lanzar ocho a la vez. Encolados, cada uno tarda lo suyo (~0,5–2 s).
    /// </summary>
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>
    /// Devuelve el archivo ida y vuelta para (fuente, A, B): del caché si ya existe, o
    /// generándolo. Tira <see cref="OperationCanceledException"/> si se cancela (un marker que se
    /// movió de nuevo, el toggle apagado, el sector descargado): en ese caso el ffmpeg en curso se
    /// MATA y no queda ningún archivo a medias.
    /// </summary>
    public static async Task<string> GetOrCreateAsync(
        string sourcePath, double startMs, double endMs, CancellationToken ct,
        string? ffmpegPath = null, string? cacheDir = null)
    {
        var ffmpeg = ffmpegPath ?? BundledFfmpeg;
        var dir = cacheDir ?? CacheDir;
        var key = PingPongMath.CacheKey(sourcePath, File.GetLastWriteTimeUtc(sourcePath), startMs, endMs);
        var output = Path.Combine(dir, key + ".mkv");

        // Atajo sin cola: si ya está, no hay que esperar a que termine la generación de OTRO sector.
        if (File.Exists(output)) return output;

        await OneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Mientras esperábamos, otro sector pudo haber pedido la MISMA zona del MISMO clip.
            if (File.Exists(output)) return output;

            Directory.CreateDirectory(dir);

            // Se escribe a un nombre temporal y se renombra al final: un ffmpeg matado a mitad de
            // camino (cancelación, cierre de la app) nunca deja un .mkv truncado con el nombre
            // definitivo, que el atajo de arriba tomaría por bueno para siempre.
            var partial = Path.Combine(dir, key + ".partial.mkv");
            await RunFfmpegAsync(ffmpeg, PingPongMath.BuildArguments(sourcePath, startMs, endMs, partial), partial, ct)
                .ConfigureAwait(false);

            File.Move(partial, output, overwrite: true);
            return output;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static async Task RunFfmpegAsync(string ffmpeg, IReadOnlyList<string> args, string partial, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            // Proceso OCULTO: sin esto, cada generación haría aparecer una consola negra encima
            // del board durante un segundo.
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };

        // Últimas líneas de stderr, para el log si falla. Se leen ASINCRÓNICAMENTE: con la salida
        // redirigida y sin leerla, un ffmpeg que escribe mucho se bloquea con el pipe lleno y la
        // generación no termina nunca.
        var tail = new Queue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (tail)
            {
                tail.Enqueue(e.Data);
                if (tail.Count > 12) tail.Dequeue();
            }
        };
        process.OutputDataReceived += (_, _) => { };

        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        // Por debajo de lo normal: generar es trabajo de fondo y no puede robarle CPU a los
        // sectores que están reproduciendo (el tirón se vería en TODO el board, no solo acá).
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* ya terminó */ }

        // Cancelar = MATAR. Esperar a que un ffmpeg obsoleto termine sería gastar RAM y CPU en un
        // archivo que nadie va a mirar (el marker ya se movió de nuevo).
        using (ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { /* ya terminó */ } }))
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (ct.IsCancellationRequested)
        {
            TryDelete(partial);
            throw new OperationCanceledException(ct);
        }

        if (process.ExitCode != 0 || !File.Exists(partial))
        {
            TryDelete(partial);
            string detail;
            lock (tail) detail = string.Join(Environment.NewLine, tail);
            throw new InvalidOperationException($"ffmpeg salió con código {process.ExitCode}.{Environment.NewLine}{detail}");
        }
    }

    #region Referencias y limpieza

    private static readonly Dictionary<string, int> Refs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Un sector empezó a reproducir <paramref name="file"/>. Ver <see cref="Release"/>.</summary>
    public static void Acquire(string file)
    {
        lock (Refs) Refs[file] = Refs.GetValueOrDefault(file) + 1;
    }

    /// <summary>
    /// Un sector dejó de usar <paramref name="file"/>. Si ya nadie lo usa, se BORRA: un temporal
    /// de ~9 MB por segundo de zona que nadie referencia es basura, y con markers que se mueven
    /// seguido se acumularían cientos de MB en una tarde.
    ///
    /// ⚠ Se borra DESPUÉS de que terminen las liberaciones de VLC en curso: el player que lo
    /// reproducía se detiene en otro hilo (<see cref="VlcEngine.Release"/>) y mientras tanto
    /// tiene el archivo abierto — Windows no deja borrarlo. Y se vuelve a mirar el contador justo
    /// antes: otro sector pudo haberlo tomado en el medio (dos sectores con el mismo clip y zona).
    /// </summary>
    public static void Release(string file)
    {
        lock (Refs)
        {
            var n = Refs.GetValueOrDefault(file) - 1;
            if (n > 0) { Refs[file] = n; return; }
            Refs.Remove(file);
        }
        DeleteWhenUnused(file);
    }

    /// <summary>Borra el archivo si nadie lo referencia (p. ej. se generó pero ya no se quería).</summary>
    public static void DeleteWhenUnused(string file)
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                try { await VlcEngine.WhenReleased().WaitAsync(VlcEngine.ReleaseTimeout).ConfigureAwait(false); }
                catch { /* techo vencido: se intenta igual */ }

                lock (Refs)
                {
                    if (Refs.ContainsKey(file)) return;
                    try
                    {
                        if (File.Exists(file)) File.Delete(file);
                        return;
                    }
                    catch
                    {
                        // Todavía abierto por algún player. Se reintenta en un rato; si nunca se
                        // suelta, lo barre la limpieza del cierre o la del próximo arranque.
                    }
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Al arrancar: borra las carpetas de procesos que ya no existen (un cierre abrupto, un
    /// crash). Se deja la de cualquier proceso vivo — otra ventana de la app la está usando.
    /// Corre en otro hilo: tocar el disco no puede demorar la primera pintada.
    /// </summary>
    public static void CleanupStale()
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(CacheRoot)) return;
                foreach (var folder in Directory.GetDirectories(CacheRoot))
                {
                    if (int.TryParse(Path.GetFileName(folder), out var pid) && pid != Environment.ProcessId && IsAlive(pid))
                        continue;
                    if (pid == Environment.ProcessId) continue;
                    try { Directory.Delete(folder, recursive: true); } catch { /* en uso: la próxima */ }
                }
            }
            catch
            {
                // Una limpieza de temporales jamás puede voltear la app.
            }
        });
    }

    /// <summary>Al cerrar: borra la carpeta de ESTE proceso (después de soltar VLC, ver App.OnExit).</summary>
    public static void DeleteOwnFolder()
    {
        try { if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, recursive: true); }
        catch { /* algo quedó abierto: lo barre el próximo arranque */ }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* lo barre la limpieza */ }
    }

    #endregion

    /// <summary>
    /// Deja asentado por qué falló una generación: en el log de crashes (junto al exe, el mismo
    /// de siempre) y en el DiagLog de las pruebas. Una falla de ffmpeg NUNCA voltea la app: el
    /// sector muestra "Error" y sigue loopeando el original, pero el porqué tiene que quedar en
    /// algún lado o es imposible de diagnosticar después.
    /// </summary>
    public static void LogFailure(string sourcePath, Exception ex)
    {
        DiagLog.Write($"pingpong error {sourcePath}: {ex.Message.Replace(Environment.NewLine, " | ")}");
        try
        {
            File.AppendAllText(AppPaths.CrashLog,
                $"""

                ===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [pingpong] =====
                {sourcePath}
                {ex}

                """);
        }
        catch
        {
            // Si ni el log se puede escribir, no hay nada más que hacer.
        }
    }
}
