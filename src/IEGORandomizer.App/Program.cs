using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using IEGORandomizer.App;
using IEGORandomizer.App.Services;
using IEGORandomizer.Engine.Seeding;
using Lynx.Core.RomFs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Run CLI mode when --cli or -i/--input is passed on the command line
if (args.Any(a => a.Equals("--cli", StringComparison.OrdinalIgnoreCase) ||
                  a.Equals("--input", StringComparison.OrdinalIgnoreCase) ||
                  a.Equals("-i", StringComparison.OrdinalIgnoreCase) ||
                  a.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                  a.Equals("-h", StringComparison.OrdinalIgnoreCase)))
{
    return CliRunner.Run(args);
}

int port = 5178;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i].Equals("--port", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int parsedPort))
    {
        port = parsedPort;
    }
}

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
    WebRootPath = resolvedWwwroot
});

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSingleton<JobManager>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    app = "IEGO Randomizer (Inazuma Eleven GO: Luz y Sombra)",
    platform = Environment.OSVersion.Platform.ToString()
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
            if (found == null)
            {
                return Results.BadRequest(new { error = "La carpeta seleccionada no contiene 'ie_a.fa' ni 'romfs/ie_a.fa'." });
            }

            var fi = new FileInfo(found);
            return Results.Ok(new
            {
                valid = true,
                kind = GameInputKind.RomFsDirectory.ToString(),
                detectedEdition = GameEdition.Auto.ToString(),
                titleId = string.Empty,
                productCode = string.Empty,
                isEncrypted = false,
                sizeBytes = fi.Length,
                summary = $"Carpeta RomFS válida ({fi.Length / (1024.0 * 1024.0):F1} MB en ie_a.fa) — Lectura segura"
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
    Console.WriteLine($"IEGO Randomizer UI escuchando en {localUrl} (0.0.0.0:{port})");
    if (!args.Any(a => a.Equals("--no-browser", StringComparison.OrdinalIgnoreCase)))
    {
        DesktopDialogHelper.TryOpenDesktopAppWindow(localUrl);
    }
});

app.Run();
return 0;

internal sealed class InspectPathRequest
{
    public string InputPath { get; set; }
}
