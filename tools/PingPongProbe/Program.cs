// ⚠ System.IO explícito: los ImplicitUsings de WPF NO lo incluyen (mismo gotcha que el .csproj de la app).
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using AmpzMediaBoard.Media;

// Prueba del loop IDA Y VUELTA con el ffmpeg que viaja con la app (third_party/ffmpeg, build LGPL).
// No simula nada: genera un clip, le pide el archivo a PingPongRenderer (el MISMO código que usa
// el sector) y lo mira cuadro por cuadro.
//
// Cómo se decide que "la segunda mitad está invertida" sin mirar a ojo: la fuente es testsrc2,
// que se mueve en TODO el cuadro, así que dos cuadros separados por un segundo son muy distintos
// y el mismo cuadro repetido es casi idéntico. Se compara el cuadro del generado en t con el de
// 2d − t (tiene que ser el mismo), y como CONTROL el de t con el de t + 1 s (tiene que diferir).
// Sin el control la prueba no distinguiría "invertido" de "todo negro".
//
//   dotnet run --project tools/PingPongProbe            (usa third_party/ffmpeg/ffmpeg.exe)
//   dotnet run --project tools/PingPongProbe -- <ffmpeg.exe>

internal static class Program
{
    private static int _fallas;
    private static string _ffmpeg = "";

    private static int Main(string[] args)
    {
        _ffmpeg = args.Length > 0 ? args[0] : FindBundledFfmpeg() ?? "";
        if (!File.Exists(_ffmpeg))
        {
            Console.WriteLine("FALTA ffmpeg. Corré primero: powershell -File tools/fetch-ffmpeg.ps1");
            return 1;
        }
        Console.WriteLine($"ffmpeg: {_ffmpeg}");

        var work = Path.Combine(Path.GetTempPath(), "ampz-pingpong-probe");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var cache = Path.Combine(work, "cache");

        // Fuente con GOP de 2 s (keyframes en 0, 2, 4…) y con audio: A = 7,3 s cae LEJOS de un
        // keyframe, así que un corte "al keyframe" quedaría 1,3 s corrido y se vería en la
        // comparación de cuadros. El audio está para probar que el generado sale SIN audio.
        var src720 = Path.Combine(work, "src720.mp4");
        Ffmpeg($"-v error -y -f lavfi -i testsrc2=size=1280x720:rate=30 -f lavfi -i sine=f=440 -t 20 -c:v mpeg4 -q:v 3 -g 60 -c:a aac -shortest \"{src720}\"");
        var src1080 = Path.Combine(work, "src1080.mp4");
        Ffmpeg($"-v error -y -f lavfi -i testsrc2=size=1920x1080:rate=30 -t 45 -c:v mpeg4 -q:v 4 -g 60 \"{src1080}\"");

        ZonaDe5Segundos(src720, cache);
        Zona30sYMemoria(src1080, cache);
        CacheDevuelveElMismoSinGenerar(src720, cache);
        CancelarMataFfmpegYNoDejaBasura(src1080, cache);
        FallaDeFfmpegNoDejaBasura(work, cache);

        Console.WriteLine();
        Console.WriteLine(_fallas == 0 ? "=== TODO OK ===" : $"=== {_fallas} FALLA(S) ===");
        return _fallas == 0 ? 0 : 1;
    }

    private static void ZonaDe5Segundos(string src, string cache)
    {
        Console.WriteLine();
        Console.WriteLine("=== 1. ZONA DE 5 s (A=7,3 s lejos de un keyframe) ===");
        const double a = 7300, b = 12300, d = b - a;

        var reloj = Stopwatch.StartNew();
        var file = PingPongRenderer.GetOrCreateAsync(src, a, b, CancellationToken.None, _ffmpeg, cache).GetAwaiter().GetResult();
        Console.WriteLine($"  generado en {reloj.ElapsedMilliseconds} ms ({new FileInfo(file).Length / 1e6:0.0} MB)");

        var info = Probe(file);
        var esperado = (2 * d + PingPongMath.PadFor(d)) / 1000.0;
        Console.WriteLine($"  duración {info.Duration:0.000} s (esperado ≈ 2d + colchón = {esperado:0.000} s), {info.Height}p, audio: {info.HasAudio}");
        Check("la duración es 2 x zona + colchón (±0,1 s)", Math.Abs(info.Duration - esperado) < 0.1);
        Check("sin pista de audio", !info.HasAudio);
        Check("achicado a 720p como máximo", info.Height is > 0 and <= 720);
        Check("códec intra-only (mjpeg)", info.Codec == "mjpeg");

        // Corte exacto: el cuadro t del generado es el cuadro A + t de la fuente.
        foreach (var t in new[] { 500.0, 2000, 4000 })
        {
            var mismo = Diff(Frame(file, t), Frame(src, a + t));
            var control = Diff(Frame(file, t), Frame(src, a + t + 1000));
            Console.WriteLine($"  t={t,5}: vs fuente A+t = {mismo,6:0.00}   control (A+t+1s) = {control,6:0.00}");
            Check($"corte exacto: generado({t}) == fuente(A+{t})", mismo < 8 && mismo * 4 < control);
        }

        // Invertido: el cuadro k de la ida es el cuadro 2N − 1 − k de la vuelta (N = cuadros de
        // la zona). Es el espejo exacto en cuadros; en tiempo es t ↔ 2d − t − 1 cuadro.
        // ⚠ Acá se elige el cuadro por ÍNDICE y no con -ss: el -ss preciso devuelve el primer
        // cuadro con pts ≥ t, y el MKV guarda pts redondeados al ms — los dos lados del espejo
        // caían en cuadros vecinos distintos y la comparación medía 2 cuadros de movimiento
        // (visto: 3,98 contra un control de 3,92, ambiguo). Por índice es exacto.
        const int n = (int)(d * 30 / 1000);   // 150 cuadros a 30 fps
        var total = FrameCount(file);
        Console.WriteLine($"  cuadros: {total} (esperado 2N + colchón = {2 * n} + {(int)(PingPongMath.PadFor(d) * 30 / 1000)})");
        Check("cuadros = ida + vuelta + colchón", total == 2 * n + (int)(PingPongMath.PadFor(d) * 30 / 1000));
        foreach (var k in new[] { 15, 45, 105 })
        {
            var espejo = Diff(FrameN(file, k), FrameN(file, 2 * n - 1 - k));
            var control = Diff(FrameN(file, k), FrameN(file, k + 30));
            Console.WriteLine($"  cuadro {k,3}: vs {2 * n - 1 - k} = {espejo,6:0.00}   control (+1 s) = {control,6:0.00}");
            Check($"la vuelta es la ida al revés: cuadro {k} == cuadro {2 * n - 1 - k}", espejo < 1 && espejo * 4 < control);
        }

        // El colchón repite el principio: el cuadro 2N + x es el cuadro x.
        var colchon = Diff(FrameN(file, 2 * n + 5), FrameN(file, 5));
        Check($"el colchón es la ida otra vez (diff {colchon:0.00})", colchon < 1);
    }

    private static void Zona30sYMemoria(string src, string cache)
    {
        Console.WriteLine();
        Console.WriteLine("=== 2. ZONA DE 30 s (fuente 1080p): TIEMPO Y PICO DE MEMORIA ===");
        long pico = 0;
        using var fin = new CancellationTokenSource();
        var medidor = Task.Run(async () =>
        {
            while (!fin.IsCancellationRequested)
            {
                foreach (var p in Process.GetProcessesByName("ffmpeg"))
                {
                    try { if (string.Equals(p.MainModule?.FileName, _ffmpeg, StringComparison.OrdinalIgnoreCase)) pico = Math.Max(pico, p.PeakWorkingSet64); }
                    catch { /* terminó mientras lo mirábamos */ }
                    finally { p.Dispose(); }
                }
                await Task.Delay(50);
            }
        });

        var reloj = Stopwatch.StartNew();
        var file = PingPongRenderer.GetOrCreateAsync(src, 5000, 35000, CancellationToken.None, _ffmpeg, cache).GetAwaiter().GetResult();
        var ms = reloj.ElapsedMilliseconds;
        fin.Cancel();
        medidor.Wait();

        var info = Probe(file);
        Console.WriteLine($"  generado en {ms} ms, {new FileInfo(file).Length / 1e6:0.0} MB, {info.Height}p, pico de RAM de ffmpeg ≈ {pico / 1e6:0} MB");
        Check("30 s de zona -> ~60,4 s de archivo", Math.Abs(info.Duration - 60.4) < 0.15);
        Check("1080p se achica a 720p", info.Height == 720);
        Check("tarda menos de 20 s (generación de fondo razonable)", ms < 20000);
    }

    private static void CacheDevuelveElMismoSinGenerar(string src, string cache)
    {
        Console.WriteLine();
        Console.WriteLine("=== 3. CACHÉ: LA MISMA ZONA NO SE VUELVE A GENERAR ===");
        var reloj = Stopwatch.StartNew();
        var file = PingPongRenderer.GetOrCreateAsync(src, 7300.3, 12299.8, CancellationToken.None, _ffmpeg, cache).GetAwaiter().GetResult();
        Console.WriteLine($"  {reloj.ElapsedMilliseconds} ms");
        Check("sale del caché en menos de 50 ms", reloj.ElapsedMilliseconds < 50);
        Check("es el mismo archivo (ruido de punto flotante no regenera)", Path.GetFileName(file) ==
            PingPongMath.CacheKey(src, File.GetLastWriteTimeUtc(src), 7300, 12300) + ".mkv");
    }

    private static void CancelarMataFfmpegYNoDejaBasura(string src, string cache)
    {
        Console.WriteLine();
        Console.WriteLine("=== 4. CANCELAR: FFMPEG MUERE Y NO QUEDA NADA A MEDIAS ===");
        using var cts = new CancellationTokenSource();
        var tarea = PingPongRenderer.GetOrCreateAsync(src, 10000, 39000, cts.Token, _ffmpeg, cache);
        Thread.Sleep(400);
        var vivoAntes = FfmpegVivos();
        var reloj = Stopwatch.StartNew();
        cts.Cancel();
        var cancelada = false;
        try { tarea.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancelada = true; }
        var tardo = reloj.ElapsedMilliseconds;
        Thread.Sleep(300);
        var key = PingPongMath.CacheKey(src, File.GetLastWriteTimeUtc(src), 10000, 39000);
        Console.WriteLine($"  ffmpeg vivos antes de cancelar: {vivoAntes}, después: {FfmpegVivos()}");
        Check("la generación estaba corriendo al cancelar", vivoAntes > 0);
        Check("cancelar tira OperationCanceledException", cancelada);
        // Sin esto la prueba pasaría aunque cancelar NO matara a ffmpeg: esperaría a que termine
        // solo (~2 s) y recién ahí lo vería muerto. Matarlo es volver en el acto.
        Check($"cancelar vuelve en el acto ({tardo} ms < 500), no espera a que ffmpeg termine", tardo < 500);
        Check("no queda ningún ffmpeg nuestro vivo", FfmpegVivos() == 0);
        Check("no queda ni el .partial ni un .mkv truncado",
            !File.Exists(Path.Combine(cache, key + ".mkv")) && !File.Exists(Path.Combine(cache, key + ".partial.mkv")));
    }

    private static void FallaDeFfmpegNoDejaBasura(string work, string cache)
    {
        Console.WriteLine();
        Console.WriteLine("=== 5. FFMPEG FALLA: EXCEPCIÓN CONTROLADA, SIN BASURA ===");
        var trucho = Path.Combine(work, "no-es-video.mp4");
        File.WriteAllText(trucho, "esto no es un video");
        string? error = null;
        try { PingPongRenderer.GetOrCreateAsync(trucho, 0, 3000, CancellationToken.None, _ffmpeg, cache).GetAwaiter().GetResult(); }
        catch (InvalidOperationException ex) { error = ex.Message; }
        Console.WriteLine($"  error: {error?.Split('\n')[0]}");
        Check("falla con InvalidOperationException (no cuelga, no voltea nada)", error is not null);
        var key = PingPongMath.CacheKey(trucho, File.GetLastWriteTimeUtc(trucho), 0, 3000);
        Check("no queda archivo", !File.Exists(Path.Combine(cache, key + ".mkv")) && !File.Exists(Path.Combine(cache, key + ".partial.mkv")));
    }

    #region Ayudas

    private static void Check(string que, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "OK " : "MAL")}] {que}");
        if (!ok) _fallas++;
    }

    private static string? FindBundledFfmpeg()
    {
        // Junto al probe (el .csproj de la app lo copia a los proyectos que la referencian) o en
        // third_party subiendo desde acá.
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(local)) return local;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "third_party", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static int FfmpegVivos()
    {
        var n = 0;
        foreach (var p in Process.GetProcessesByName("ffmpeg"))
        {
            try { if (string.Equals(p.MainModule?.FileName, _ffmpeg, StringComparison.OrdinalIgnoreCase)) n++; }
            catch { }
            finally { p.Dispose(); }
        }
        return n;
    }

    private static (string Stdout, string Stderr, int Code) Run(string arguments)
    {
        var psi = new ProcessStartInfo(_ffmpeg, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        return (Convert.ToBase64String(ms.ToArray()), errTask.Result, p.ExitCode);
    }

    private static void Ffmpeg(string arguments)
    {
        var r = Run(arguments);
        if (r.Code != 0) throw new InvalidOperationException($"ffmpeg {arguments}\n{r.Stderr}");
    }

    private sealed record Info(double Duration, int Height, bool HasAudio, string Codec);

    /// <summary>Sin ffprobe (no viaja con la app): se lee el encabezado que imprime "ffmpeg -i".</summary>
    private static Info Probe(string file)
    {
        var err = Run($"-hide_banner -i \"{file}\"").Stderr;
        var dur = Regex.Match(err, @"Duration: (\d+):(\d+):(\d+\.\d+)");
        var seconds = dur.Success
            ? int.Parse(dur.Groups[1].Value) * 3600 + int.Parse(dur.Groups[2].Value) * 60 + double.Parse(dur.Groups[3].Value, CultureInfo.InvariantCulture)
            : -1;
        var video = Regex.Match(err, @"Video: (\w+).*?, (\d+)x(\d+)");
        return new Info(seconds,
            video.Success ? int.Parse(video.Groups[3].Value) : 0,
            err.Contains("Audio:"),
            video.Success ? video.Groups[1].Value : "");
    }

    /// <summary>
    /// Un cuadro en <paramref name="ms"/>, achicado a 160x90 en grises. -ss ANTES de -i con
    /// accurate_seek (el default): decodifica hasta el cuadro exacto, también en la fuente con GOP.
    /// </summary>
    private static byte[] Frame(string file, double ms)
    {
        var s = (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);
        var r = Run($"-v error -ss {s} -i \"{file}\" -frames:v 1 -vf scale=160:90,format=gray -f rawvideo -");
        return Convert.FromBase64String(r.Stdout);
    }

    /// <summary>El cuadro número <paramref name="k"/> (desde 0), elegido por índice. Ver ZonaDe5Segundos.</summary>
    private static byte[] FrameN(string file, int k)
    {
        var r = Run($"-v error -i \"{file}\" -vf \"select=eq(n\\,{k}),scale=160:90,format=gray\" -frames:v 1 -f rawvideo -");
        return Convert.FromBase64String(r.Stdout);
    }

    private static int FrameCount(string file)
    {
        var err = Run($"-hide_banner -i \"{file}\" -map 0:v -f null -").Stderr;
        var m = Regex.Matches(err, @"frame=\s*(\d+)");
        return m.Count > 0 ? int.Parse(m[^1].Groups[1].Value) : -1;
    }

    /// <summary>Diferencia media absoluta por píxel (0–255). Cuadros de distinto tamaño = máxima.</summary>
    private static double Diff(byte[] x, byte[] y)
    {
        if (x.Length == 0 || x.Length != y.Length) return 255;
        long sum = 0;
        for (var i = 0; i < x.Length; i++) sum += Math.Abs(x[i] - y[i]);
        return (double)sum / x.Length;
    }

    #endregion
}
