using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using IEGORandomizer.App;
using IEGORandomizer.App.Services;
using IEGORandomizer.Engine.Diagnostics;
using IEGORandomizer.Engine.Seeding;
using Lynx.Core.RomFs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Run CLI mode when --cli, --diagnose, or -i/--input is passed on the command line
if (args.Any(a => a.Equals("--cli", StringComparison.OrdinalIgnoreCase) ||
                  a.Equals("--diagnose", StringComparison.OrdinalIgnoreCase) ||
                  a.Equals("--input", StringComparison.OrdinalIgnoreCase) ||
                  a.Equals("-i", StringComparison.OrdinalIgnoreCase) ||
                  a.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                  a.Equals("-h", StringComparison.OrdinalIgnoreCase)))
{
    return CliRunner.Run(args);
}

int? explicitPort = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i].Equals("--port", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int parsedPort))
    {
        explicitPort = parsedPort;
    }
}

bool bindAny = args.Any(a => a.Equals("--bind-any", StringComparison.OrdinalIgnoreCase)) || !OperatingSystem.IsWindows();
int port = explicitPort ?? FindAvailablePort(5178);
string bindHost = bindAny ? "0.0.0.0" : "127.0.0.1";

string resolvedWwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (!Directory.Exists(resolvedWwwroot))
{
    string candidateProjectWwwroot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "IEGORandomizer.App", "wwwroot"));
    if (Directory.Exists(candidateProjectWwwroot))
    {
        resolvedWwwroot = candidateProjectWwwroot;
    }
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Directory.Exists(resolvedWwwroot) ? resolvedWwwroot : null
});

// Keep console clean and non-intimidating for end users on Windows
builder.Logging.ClearProviders();
if (!OperatingSystem.IsWindows())
{
    builder.Logging.AddSimpleConsole();
}

builder.WebHost.UseUrls($"http://{bindHost}:{port}");

builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSingleton<JobManager>();

var app = builder.Build();

if (Directory.Exists(resolvedWwwroot))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

// Embedded-resource fallback so single-file IEGORandomizer.exe works anywhere with zero external folders
app.MapGet("/", () => ServeEmbeddedOrPhysicalAsset(resolvedWwwroot, "index.html", "text/html; charset=utf-8"));
app.MapGet("/index.html", () => ServeEmbeddedOrPhysicalAsset(resolvedWwwroot, "index.html", "text/html; charset=utf-8"));
app.MapGet("/styles.css", () => ServeEmbeddedOrPhysicalAsset(resolvedWwwroot, "styles.css", "text/css; charset=utf-8"));
app.MapGet("/app.js", () => ServeEmbeddedOrPhysicalAsset(resolvedWwwroot, "app.js", "application/javascript; charset=utf-8"));

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    version = "1.0.2",
    app = "IEGO Randomizer (Inazuma Eleven GO: Luz y Sombra)",
    platform = Environment.OSVersion.Platform.ToString()
}));

app.MapGet("/api/citra/paths", () => Results.Ok(new
{
    defaultModsRoot = LocalDiagnosticService.ResolveDefaultCitraModsRoot(),
    discoveredRoots = LocalDiagnosticService.DiscoverCitraModsRoots()
}));

app.MapGet("/api/seed/random", () => Results.Ok(new
{
    seed = SeedEngine.GenerateRandomSeedString()
}));

app.MapGet("/api/fs/list", (string path, bool? dirsOnly) =>
{
    var listing = DesktopDialogHelper.BrowseDirectory(path, dirsOnly ?? false);
    return Results.Ok(listing);
});

app.MapPost("/api/fs/pick-native", (string mode) =>
{
    string picked = DesktopDialogHelper.TryPickNativeWindowsDialog(mode ?? "file");
    return Results.Ok(new
    {
        supported = OperatingSystem.IsWindows(),
        selectedPath = picked
    });
});

app.MapPost("/api/app/open-folder", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<OpenFolderRequest>();
    string folder = body?.FolderPath?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
    {
        return Results.BadRequest(new { error = "La carpeta indicada no existe." });
    }

    if (OperatingSystem.IsWindows())
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Path.GetFullPath(folder)}\"",
                UseShellExecute = true
            });
            return Results.Ok(new { opened = true });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    return Results.Ok(new { opened = false });
});

app.MapPost("/api/app/shutdown", (IHostApplicationLifetime lifetime) =>
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(250);
        lifetime.StopApplication();
    });
    return Results.Ok(new { stopping = true });
});

app.MapPost("/api/inspect", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<InspectPathRequest>();
    string inputPath = body?.InputPath?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(inputPath))
    {
        return Results.BadRequest(new { error = "Introduce o selecciona una ruta de entrada." });
    }

    try
    {
        string full = Path.GetFullPath(inputPath);
        if (Directory.Exists(full))
        {
            string direct = Path.Combine(full, "ie_a.fa");
            string nested = Path.Combine(full, "romfs", "ie_a.fa");
            string found = File.Exists(direct) ? direct : (File.Exists(nested) ? nested : null);
            string bannerFile = Path.Combine(full, "ctr", "exsave", "banner.icn");
            string bannerTitle = File.Exists(bannerFile)
                ? LocalDiagnosticService.TryReadSmdhBannerTitle(bannerFile)
                : string.Empty;

            if (found == null)
            {
                bool hasFixXr = File.Exists(Path.Combine(full, "fix.xr"));
                string detail = hasFixXr
                    ? "Se detectó 'fix.xr' de RomFS pero falta 'ie_a.fa' (~855 MB) dentro de esta carpeta."
                    : "La carpeta seleccionada no contiene 'ie_a.fa' ni 'romfs/ie_a.fa'.";
                return Results.BadRequest(new { error = detail });
            }

            var hintedEdition = TitleIdCatalog.DetectEditionFromPathHint(full);
            string resolvedTid = hintedEdition == GameEdition.Auto
                ? string.Empty
                : TitleIdCatalog.GetDefaultTitleId(hintedEdition);
            var fi = new FileInfo(found);
            string bannerSuffix = string.IsNullOrWhiteSpace(bannerTitle) ? string.Empty : $" [{bannerTitle}]";
            return Results.Ok(new
            {
                valid = true,
                kind = GameInputKind.RomFsDirectory.ToString(),
                detectedEdition = hintedEdition.ToString(),
                titleId = resolvedTid,
                productCode = string.Empty,
                isEncrypted = false,
                sizeBytes = fi.Length,
                summary = $"Carpeta RomFS válida ({fi.Length / (1024.0 * 1024.0):F1} MB en ie_a.fa){bannerSuffix} — Recuerda instalar el mod en Citra (load/mods)"
            });
        }

        if (!File.Exists(full))
        {
            return Results.BadRequest(new { error = $"No se encontró el archivo o carpeta: {full}" });
        }

        var meta = Nintendo3dsRomReader.Inspect(full);
        var info = new FileInfo(full);
        if (meta.IsEncrypted)
        {
            return Results.BadRequest(new
            {
                error = $"El archivo .3ds ({meta.TitleIdHex} / {meta.ProductCode}) está cifrado con criptografía NCCH. Usa una copia desencriptada (.3ds para Citra), una carpeta RomFS extraída o ie_a.fa."
            });
        }

        string kindLabel = meta.Kind switch
        {
            GameInputKind.Arc0File => "Archivo ARC0 directo (ie_a.fa)",
            GameInputKind.Nintendo3dsRom => $"ROM Nintendo 3DS desencriptada ({meta.ProductCode})",
            GameInputKind.NcchContainer => $"Contenedor NCCH ({meta.ProductCode})",
            _ => "Imagen RomFS"
        };

        return Results.Ok(new
        {
            valid = true,
            kind = meta.Kind.ToString(),
            detectedEdition = meta.DetectedEdition.ToString(),
            titleId = meta.TitleIdHex,
            productCode = meta.ProductCode,
            isEncrypted = meta.IsEncrypted,
            sizeBytes = info.Length,
            summary = $"{kindLabel} — {(info.Length / (1024.0 * 1024.0)):F1} MB — Original protegido (solo lectura)"
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/diagnose", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<DiagnoseRequest>();
    if (body == null || string.IsNullOrWhiteSpace(body.InputPath))
        return Results.BadRequest(new { error = "Selecciona primero la ruta de entrada del juego para diagnosticar." });

    try
    {
        Enum.TryParse<GameEdition>(body.Edition ?? "Auto", ignoreCase: true, out var ed);
        var report = LocalDiagnosticService.RunDiagnostics(body.InputPath.Trim(), ed, body.TargetOutputOrCitraDir);
        return Results.Ok(report);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/citra/install", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<CitraInstallRequest>();
    if (body == null || string.IsNullOrWhiteSpace(body.SourcePath))
        return Results.BadRequest(new { error = "Indica la carpeta de salida o archivo ie_a.fa generado." });

    try
    {
        Enum.TryParse<GameEdition>(body.Edition ?? "Light", ignoreCase: true, out var ed);
        var result = LocalDiagnosticService.InstallIeAFaToCitraMods(
            body.SourcePath.Trim(),
            ed,
            body.CustomTitleIdOverride,
            body.CitraModsRoot);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/jobs", (GenerateRunRequest request, JobManager jobs) =>
{
    if (request == null || string.IsNullOrWhiteSpace(request.InputPath))
        return Results.BadRequest(new { error = "Debe seleccionar un archivo o carpeta de entrada." });
    if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        return Results.BadRequest(new { error = "Debe seleccionar una carpeta de salida separada." });

    var state = jobs.StartJob(request);
    return Results.Ok(state);
});

app.MapGet("/api/jobs/{jobId}", (string jobId, JobManager jobs) =>
{
    var state = jobs.GetJob(jobId);
    if (state == null)
        return Results.NotFound(new { error = "Trabajo no encontrado." });
    return Results.Ok(state);
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    string localUrl = $"http://127.0.0.1:{port}";
    Console.WriteLine("================================================================");
    Console.WriteLine("  IEGO RANDOMIZER v1.0.2 — Inazuma Eleven GO: Luz y Sombra");
    Console.WriteLine("================================================================");
    Console.WriteLine($"  Interfaz activa en : {localUrl}");
    Console.WriteLine("  Estado             : Listo (Mantén esta ventana abierta mientras usas el programa)");
    Console.WriteLine("  Para salir         : Pulsa 'Cerrar programa' en la interfaz o cierra esta ventana.");
    Console.WriteLine("================================================================");

    if (!args.Any(a => a.Equals("--no-browser", StringComparison.OrdinalIgnoreCase)))
    {
        DesktopDialogHelper.TryOpenDesktopAppWindow(localUrl);
    }
});

app.Run();
return 0;

static int FindAvailablePort(int preferredPort)
{
    try
    {
        using var check = new TcpListener(IPAddress.Loopback, preferredPort);
        check.Start();
        check.Stop();
        return preferredPort;
    }
    catch
    {
        using var fallback = new TcpListener(IPAddress.Loopback, 0);
        fallback.Start();
        int assigned = ((IPEndPoint)fallback.LocalEndpoint).Port;
        fallback.Stop();
        return assigned;
    }
}

static IResult ServeEmbeddedOrPhysicalAsset(string wwwrootDir, string fileName, string contentType)
{
    if (!string.IsNullOrEmpty(wwwrootDir))
    {
        string diskFile = Path.Combine(wwwrootDir, fileName);
        if (File.Exists(diskFile))
        {
            return Results.Bytes(File.ReadAllBytes(diskFile), contentType);
        }
    }

    string resourceName = $"IEGORandomizer.App.wwwroot.{fileName}";
    using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
    if (stream == null)
    {
        return Results.NotFound();
    }

    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Results.Bytes(ms.ToArray(), contentType);
}

internal sealed class InspectPathRequest
{
    public string InputPath { get; set; }
}

internal sealed class OpenFolderRequest
{
    public string FolderPath { get; set; }
}

internal sealed class DiagnoseRequest
{
    public string InputPath { get; set; }
    public string Edition { get; set; }
    public string TargetOutputOrCitraDir { get; set; }
}

internal sealed class CitraInstallRequest
{
    public string SourcePath { get; set; }
    public string Edition { get; set; }
    public string CustomTitleIdOverride { get; set; }
    public string CitraModsRoot { get; set; }
}
