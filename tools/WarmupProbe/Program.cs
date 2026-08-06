using System.Diagnostics;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

// Mide el arranque en frio de VLC, etapa por etapa. Ver el .csproj para el porque.
//
//   dotnet run --project tools/WarmupProbe -- <ruta a un video>
//
// El archivo es opcional: sin el se miden igual las dos primeras etapas, que son las que
// pesan. Con archivo se ve ademas cuanto cuesta abrir el primer clip contra el segundo.

var clip = args.Length > 0 ? args[0] : null;

var sw = Stopwatch.StartNew();
Core.Initialize();
var initMs = sw.ElapsedMilliseconds;

// Este es el sospechoso: aca libvlc abre y consulta los ~323 DLL de plugins uno por uno.
sw.Restart();
using var vlc = new LibVLC("--no-video-title-show", "--no-osd", "--no-snapshot-preview",
    "--no-loop", "--no-repeat", "--no-sub-autodetect-file", "--no-input-fast-seek", "--quiet");
var libVlcMs = sw.ElapsedMilliseconds;

Console.WriteLine($"Core.Initialize()  : {initMs,6} ms   (carga libvlc.dll + libvlccore.dll)");
Console.WriteLine($"new LibVLC(...)    : {libVlcMs,6} ms   (escaneo de plugins)");

// Segundo experimento: si el proceso YA tiene cargados los DLL nativos del demuxer y del
// codec, la primera apertura deberia costar lo mismo que la segunda. LoadLibrary sobre un
// modulo ya cargado es un incremento de refcount, asi que precargarlos nosotros no le
// estorba a VLC cuando los pida por su cuenta.
if (Environment.GetEnvironmentVariable("PRELOAD") == "1")
{
    sw.Restart();
    var dir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64", "plugins");
    var loaded = 0;
    foreach (var name in new[]
             {
                 @"codec\libavcodec_plugin.dll",
                 @"demux\libavformat_plugin.dll",
                 @"demux\libes_plugin.dll",
                 @"demux\libmp4_plugin.dll",
                 @"video_output\libdirect3d11_plugin.dll",
                 @"video_chroma\libswscale_plugin.dll",
                 @"audio_output\libmmdevice_plugin.dll",
             })
    {
        if (NativeLibrary.TryLoad(Path.Combine(dir, name), out _)) loaded++;
    }
    Console.WriteLine($"preload {loaded} DLL     : {sw.ElapsedMilliseconds,6} ms");
}

if (clip is { } path && File.Exists(path))
{
    // Primera apertura: ademas del archivo, se cargan bajo demanda los plugins del demuxer y
    // del codec. Es la parte que un precalentamiento de LibVLC solo NO se lleva.
    sw.Restart();
    using (var player = new MediaPlayer(vlc))
    using (var media = new Media(vlc, new Uri(path)))
    {
        media.Parse(MediaParseOptions.ParseLocal).Wait();
        player.Media = media;
    }
    var firstMs = sw.ElapsedMilliseconds;

    sw.Restart();
    using (var player = new MediaPlayer(vlc))
    using (var media = new Media(vlc, new Uri(path)))
    {
        media.Parse(MediaParseOptions.ParseLocal).Wait();
        player.Media = media;
    }
    var secondMs = sw.ElapsedMilliseconds;

    Console.WriteLine($"1er archivo        : {firstMs,6} ms   (demuxer + codec bajo demanda)");
    Console.WriteLine($"2do archivo        : {secondMs,6} ms   (ya calientes)");
    Console.WriteLine();
    Console.WriteLine($"TOTAL primer drop  : {initMs + libVlcMs + firstMs,6} ms");
    Console.WriteLine($"TOTAL drops siguientes: {secondMs,3} ms");
}

return 0;
