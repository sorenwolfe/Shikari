using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Shikari.Model;
namespace Shikari.Services.WtfDig;

/// <summary>Explicit user-initiated requests only. Redirects and arbitrary source hosts are not followed.</summary>
public sealed class WtfDigClient : IDisposable
{
    private readonly HttpClient http;
    public WtfDigClient() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }
    public WtfDigClient(HttpMessageHandler handler)
    {
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Shikari-Dalamud/1.0 (+https://github.com/sorenwolfe/Shikari)");
    }
    public async Task<WtfDigGuide> LoadAsync(WtfDigLink link, CancellationToken cancel)
    {
        var bytes = await ReadAsync(new Uri(link.DataUrl), LiteralData.MaxSourceCharacters, cancel).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        return WtfDigGuide.Read(link, System.Text.Encoding.UTF8.GetString(bytes));
    }
    private async Task<byte[]> ReadAsync(Uri uri, int maxBytes, CancellationToken cancel)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Source download exceeds its size limit.");
        using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maxBytes) throw new InvalidDataException("Source download exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    public async Task<PreparedWtfDig> PrepareAsync(WtfDigPreview preview, string backdropDirectory, bool images, CancellationToken cancel)
    {
        // Prepared plans and assets are isolated from both the preview and the live plan.
        var prepared = new PreparedWtfDig { Plan = JsonConvert.DeserializeObject<PlanDocument>(JsonConvert.SerializeObject(preview.Plan))! };
        prepared.Warnings.AddRange(preview.Warnings);
        if (!images) return prepared;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        var downloaded = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        var requests = 0;
        try
        {
            foreach (var image in preview.ImageReferences)
            {
                cancel.ThrowIfCancellationRequested();
                var slide = prepared.Plan.FindSlide(image.Key);
                if (slide == null) continue;
                if (downloaded.TryGetValue(image.Value, out var known)) { slide.BackdropId = known; slide.BackdropOpacity = 1; continue; }
                if (++requests > 24 || total >= 48 * 1024 * 1024) { prepared.Warnings.Add("Reference image download limit reached; remaining image links are retained in notes."); break; }
                try
                {
                    var uri = new Uri(image.Value);
                    if (uri.Scheme != "https" || uri.Host != "wtfdig.info" || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
                        throw new InvalidDataException("External image retained as a link only.");
                    var bytes = await ReadAsync(uri, 4 * 1024 * 1024, budget.Token).ConfigureAwait(false);
                    total += bytes.Length;
                    var extension = ImageExtension(bytes);
                    Directory.CreateDirectory(backdropDirectory);
                    var id = Guid.NewGuid().ToString("N") + extension;
                    var path = Path.Combine(backdropDirectory, id);
                    prepared.CreatedFiles.Add(path);
                    await File.WriteAllBytesAsync(path, bytes, cancel).ConfigureAwait(false);
                    downloaded[image.Value] = id;
                    slide.BackdropId = id; slide.BackdropOpacity = 1;
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                { prepared.Warnings.Add("Image download timed out; remaining references are retained as links."); break; }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UriFormatException)
                { prepared.Warnings.Add(slide.Title + ": reference image unavailable; source link retained. " + ex.Message); }
            }
            cancel.ThrowIfCancellationRequested();
            if (prepared.Warnings.Count > preview.Warnings.Count)
                prepared.Plan.Notes += "\nImage download notes:\n" + string.Join("\n", prepared.Warnings.GetRange(preview.Warnings.Count, prepared.Warnings.Count - preview.Warnings.Count));
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }
    private static string ImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 12 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        if (bytes.Length >= 12 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" &&
            System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP") return ".webp";
        throw new InvalidDataException("Reference is not a supported PNG, JPEG or WEBP image.");
    }
    public void Dispose() => http.Dispose();
}

public sealed class PreparedWtfDig : IDisposable
{
    public PlanDocument Plan { get; init; } = new();
    public List<string> Warnings { get; } = new();
    internal List<string> CreatedFiles { get; } = new();
    public bool Retained { get; set; }
    public void Dispose()
    {
        if (Retained) return;
        foreach (var path in CreatedFiles) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
