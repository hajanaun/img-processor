using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentFTP;
using ImgApp;

// ---------------------------------------------------------------------------
// Sestavení aplikace
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("proxy", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; ImageProcessor/1.0)");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient("downloader", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; ImageProcessor/1.0)");
    client.Timeout = TimeSpan.FromSeconds(20);
});

var app = builder.Build();

app.UseDefaultFiles();   // GET / → wwwroot/index.html
app.UseStaticFiles();

// ---------------------------------------------------------------------------
// GET /proxy?url=...  — stáhne obrázek pro náhled (obejití CORS)
// ---------------------------------------------------------------------------
app.MapGet("/proxy", async (string url, IHttpClientFactory hcf, CancellationToken ct) =>
{
    if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
        !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest("Neplatná URL");

    try
    {
        var client = hcf.CreateClient("proxy");
        var resp = await client.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return Results.Bytes(bytes, contentType);
    }
    catch (Exception e)
    {
        return Results.Problem($"Chyba stahování: {e.Message}", statusCode: 502);
    }
});

// ---------------------------------------------------------------------------
// POST /parse  — z textu vytáhne URL obrázků
// ---------------------------------------------------------------------------
app.MapPost("/parse", async (HttpRequest req, CancellationToken ct) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
    var text = J.Str(doc.RootElement, "text");
    var urls = ExtractImageUrls(text);
    return Results.Json(new { count = urls.Count, urls });
});

// ---------------------------------------------------------------------------
// POST /process  — zpracuje obrázky, vrátí ZIP / base64 / FTP upload
// ---------------------------------------------------------------------------
app.MapPost("/process", async (HttpRequest req, IHttpClientFactory hcf, CancellationToken ct) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
    var root = doc.RootElement;

    if (!root.TryGetProperty("urls", out var urlsEl) || urlsEl.GetArrayLength() == 0)
        return Results.Json(new { error = "Žádné obrázky k zpracování" }, statusCode: 400);
    if (urlsEl.GetArrayLength() > 20)
        return Results.Json(new { error = "Maximálně 20 obrázků najednou" }, statusCode: 400);

    var settingsEl = root.TryGetProperty("settings", out var s) ? s : default;
    var requestedFormat = J.Str(settingsEl, "format", "JPEG");
    // base64 výstup musí být lossless – JPEG artefakty by rozbily nulovou toleranci trimu
    if (saveMode == "base64" && requestedFormat.ToUpperInvariant() is "JPG" or "JPEG")
        requestedFormat = "PNG";

    var opts = new ProcessingOptions(
        TrimContent:   J.Bool(settingsEl, "trim_content", true),
        AddBorder:     J.Bool(settingsEl, "add_border"),
        BorderPx:      J.Int(settingsEl,  "border_px", 20),
        Brightness:    (float)J.Dbl(settingsEl, "brightness", 1.0),
        MaxPx:         J.Int(settingsEl,  "max_px", 0),
        OutputFormat:  requestedFormat,
        JpegQuality:   J.Int(settingsEl,  "quality", 90)
    );

    var saveMode = J.Str(root, "save_mode", "zip");
    var ext = opts.OutputFormat.ToUpperInvariant() is "JPG" or "JPEG" ? "jpg" : opts.OutputFormat.ToLower();

    var client = hcf.CreateClient("downloader");
    var processed = new List<(string Name, byte[] Data)>();
    var errors = new List<object>();
    int i = 0;

    foreach (var urlEl in urlsEl.EnumerateArray())
    {
        var url = urlEl.GetString() ?? "";
        i++;
        try
        {
            var resp = await client.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var raw = await resp.Content.ReadAsByteArrayAsync(ct);
            var outBytes = ImageProcessor.ProcessImage(raw, opts);
            processed.Add(($"img_{i:D3}.{ext}", outBytes));
        }
        catch (Exception e)
        {
            errors.Add(new { url, error = e.Message });
        }
    }

    if (processed.Count == 0)
        return Results.Json(new { error = "Žádný obrázek se nepodařilo zpracovat", details = errors }, statusCode: 500);

    // --- base64 ---
    if (saveMode == "base64")
    {
        var items = processed.Select(p =>
        {
            var mime = p.Name.EndsWith("jpg") ? "image/jpeg" : $"image/{p.Name.Split('.').Last()}";
            return new
            {
                name = p.Name,
                data_uri = $"data:{mime};base64," + Convert.ToBase64String(p.Data),
            };
        }).ToList();
        return Results.Json(new { mode = "base64", items, errors });
    }

    // --- přímý FTP upload (rychlý režim bez FTP správce) ---
    if (saveMode == "ftp")
    {
        var ftpCfg = root.TryGetProperty("ftp", out var fc) ? fc : default;
        try
        {
            await using var ftp = CreateFtpClient(ftpCfg);
            await ftp.Connect(ct);
            var remoteDir = J.Str(ftpCfg, "dir", "/").TrimEnd('/');
            var uploaded = new List<string>();
            foreach (var (name, data) in processed)
            {
                await ftp.UploadBytes(data, $"{remoteDir}/{name}", FtpRemoteExists.Overwrite, false, null, ct);
                uploaded.Add(name);
            }
            await ftp.Disconnect(ct);
            return Results.Json(new { mode = "ftp", uploaded, errors });
        }
        catch (Exception e)
        {
            return Results.Json(new { error = $"FTP chyba: {e.Message}" }, statusCode: 500);
        }
    }

    // --- ZIP (výchozí) ---
    var zipBuf = new MemoryStream();
    using (var zip = new ZipArchive(zipBuf, ZipArchiveMode.Create, leaveOpen: true))
        foreach (var (name, data) in processed)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            await using var es = entry.Open();
            await es.WriteAsync(data, ct);
        }
    zipBuf.Seek(0, SeekOrigin.Begin);
    return Results.File(zipBuf, "application/zip", "zpracovane_obrazky.zip");
});

// ---------------------------------------------------------------------------
// FTP správce – bezstavové endpointy (přihlašovací údaje posílá prohlížeč)
// ---------------------------------------------------------------------------

app.MapPost("/ftp/list", async (HttpRequest req, CancellationToken ct) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
    var body = doc.RootElement;
    var path = J.Str(body, "path");

    AsyncFtpClient ftp;
    try { ftp = CreateFtpClient(body); await ftp.Connect(ct); }
    catch (Exception e) { return Results.Json(new { error = $"Připojení selhalo: {e.Message}" }, statusCode: 502); }

    await using (ftp)
    {
        try
        {
            if (!string.IsNullOrEmpty(path))
                await ftp.SetWorkingDirectory(path, ct);
            var cwd = await ftp.GetWorkingDirectory(ct);
            var listing = await ftp.GetListing(cwd, ct);
            var entries = listing
                .Where(e => e.Name is not "." and not "..")
                .Select(e => new
                {
                    name = e.Name,
                    type = e.Type == FtpObjectType.Directory ? "dir" : "file",
                    size = e.Size,
                })
                .OrderBy(e => e.type != "dir")
                .ThenBy(e => e.name.ToLowerInvariant())
                .ToList();
            return Results.Json(new { path = cwd, entries });
        }
        catch (Exception e)
        {
            return Results.Json(new { error = $"Chyba výpisu: {e.Message}" }, statusCode: 500);
        }
        finally { try { await ftp.Disconnect(ct); } catch { /* ignore */ } }
    }
});

app.MapPost("/ftp/upload", async (HttpRequest req, CancellationToken ct) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
    var body = doc.RootElement;
    var path = J.Str(body, "path");
    var name = SafeName(J.Str(body, "name"));
    if (string.IsNullOrEmpty(name))
        return Results.Json(new { error = "Chybí název souboru" }, statusCode: 400);

    byte[] raw;
    try { raw = DecodeBase64(J.Str(body, "data_b64")); }
    catch (Exception e) { return Results.Json(new { error = $"Neplatná data souboru: {e.Message}" }, statusCode: 400); }

    AsyncFtpClient ftp;
    try { ftp = CreateFtpClient(body); await ftp.Connect(ct); }
    catch (Exception e) { return Results.Json(new { error = $"Připojení selhalo: {e.Message}" }, statusCode: 502); }

    await using (ftp)
    {
        try
        {
            if (!string.IsNullOrEmpty(path))
                await ftp.SetWorkingDirectory(path, ct);
            var cwd = await ftp.GetWorkingDirectory(ct);
            var remotePath = $"{cwd.TrimEnd('/')}/{name}";
            await ftp.UploadBytes(raw, remotePath, FtpRemoteExists.Overwrite, false, null, ct);
            return Results.Json(new { ok = true, name, path = cwd });
        }
        catch (Exception e)
        {
            return Results.Json(new { error = $"Nahrání selhalo: {e.Message}" }, statusCode: 500);
        }
        finally { try { await ftp.Disconnect(ct); } catch { /* ignore */ } }
    }
});

app.MapPost("/ftp/mkdir", async (HttpRequest req, CancellationToken ct) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
    var body = doc.RootElement;
    var path = J.Str(body, "path");
    var name = SafeName(J.Str(body, "name"));
    if (string.IsNullOrEmpty(name))
        return Results.Json(new { error = "Chybí název složky" }, statusCode: 400);

    AsyncFtpClient ftp;
    try { ftp = CreateFtpClient(body); await ftp.Connect(ct); }
    catch (Exception e) { return Results.Json(new { error = $"Připojení selhalo: {e.Message}" }, statusCode: 502); }

    await using (ftp)
    {
        try
        {
            if (!string.IsNullOrEmpty(path))
                await ftp.SetWorkingDirectory(path, ct);
            var cwd = await ftp.GetWorkingDirectory(ct);
            await ftp.CreateDirectory($"{cwd.TrimEnd('/')}/{name}", ct);
            return Results.Json(new { ok = true });
        }
        catch (Exception e)
        {
            return Results.Json(new { error = $"Nelze vytvořit složku: {e.Message}" }, statusCode: 500);
        }
        finally { try { await ftp.Disconnect(ct); } catch { /* ignore */ } }
    }
});

app.MapPost("/ftp/delete", async (HttpRequest req, CancellationToken ct) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
    var body = doc.RootElement;
    var path = J.Str(body, "path");
    var name = SafeName(J.Str(body, "name"));
    var isDir = J.Bool(body, "is_dir");
    if (string.IsNullOrEmpty(name))
        return Results.Json(new { error = "Chybí název" }, statusCode: 400);

    AsyncFtpClient ftp;
    try { ftp = CreateFtpClient(body); await ftp.Connect(ct); }
    catch (Exception e) { return Results.Json(new { error = $"Připojení selhalo: {e.Message}" }, statusCode: 502); }

    await using (ftp)
    {
        try
        {
            if (!string.IsNullOrEmpty(path))
                await ftp.SetWorkingDirectory(path, ct);
            var cwd = await ftp.GetWorkingDirectory(ct);
            var target = $"{cwd.TrimEnd('/')}/{name}";
            if (isDir)
                await ftp.DeleteDirectory(target, ct);
            else
                await ftp.DeleteFile(target, ct);
            return Results.Json(new { ok = true });
        }
        catch (Exception e)
        {
            return Results.Json(new { error = $"Smazání selhalo: {e.Message}" }, statusCode: 500);
        }
        finally { try { await ftp.Disconnect(ct); } catch { /* ignore */ } }
    }
});

app.Run();

// ---------------------------------------------------------------------------
// Pomocné metody
// ---------------------------------------------------------------------------

static List<string> ExtractImageUrls(string text)
{
    const string imgExt = @"(?:jpg|jpeg|png|gif|webp|bmp|tiff?)";
    var urlRx = new Regex(@"https?://[^\s""'<>\\)]+", RegexOptions.IgnoreCase);
    var imgRx = new Regex($@"\.{imgExt}(?:\?[^\s]*)?$", RegexOptions.IgnoreCase);
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var result = new List<string>();
    foreach (Match m in urlRx.Matches(text))
    {
        var url = m.Value.TrimEnd('"', '\'', ')', '.', ',', ';');
        if (imgRx.IsMatch(url) && seen.Add(url))
            result.Add(url);
    }
    return result;
}

static AsyncFtpClient CreateFtpClient(JsonElement cfg)
{
    var host = J.Str(cfg, "host").Trim();
    if (string.IsNullOrEmpty(host)) throw new ArgumentException("Chybí FTP host");
    var ftpClient = new AsyncFtpClient(
        host,
        J.Str(cfg, "user", "anonymous"),
        J.Str(cfg, "password"),
        J.Int(cfg, "port", 21),
        new FtpConfig
        {
            EncryptionMode = J.Bool(cfg, "tls") ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
            DataConnectionType = FtpDataConnectionType.AutoPassive,
        });
    ftpClient.Encoding = Encoding.UTF8;
    return ftpClient;
}

static string SafeName(string? name) =>
    (name ?? "").Replace('\\', '/').Split('/').Last().Trim();

static byte[] DecodeBase64(string data)
{
    var s = (data ?? "").Trim();
    if (s.StartsWith("data:") && s.Contains(','))
        s = s[(s.IndexOf(',') + 1)..];
    return Convert.FromBase64String(s);
}

// ---------------------------------------------------------------------------
// JSON pomocné metody
// ---------------------------------------------------------------------------

static class J
{
    public static string Str(JsonElement el, string key, string def = "") =>
        el.ValueKind != JsonValueKind.Undefined && el.TryGetProperty(key, out var v)
            ? v.GetString() ?? def
            : def;

    public static int Int(JsonElement el, string key, int def = 0) =>
        el.ValueKind != JsonValueKind.Undefined && el.TryGetProperty(key, out var v) && v.TryGetInt32(out var i)
            ? i
            : def;

    public static bool Bool(JsonElement el, string key, bool def = false)
    {
        if (el.ValueKind == JsonValueKind.Undefined || !el.TryGetProperty(key, out var v)) return def;
        return v.ValueKind == JsonValueKind.True;
    }

    public static double Dbl(JsonElement el, string key, double def = 0.0) =>
        el.ValueKind != JsonValueKind.Undefined && el.TryGetProperty(key, out var v) && v.TryGetDouble(out var d)
            ? d
            : def;
}
