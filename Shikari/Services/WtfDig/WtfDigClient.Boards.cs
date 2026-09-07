using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shikari.Model;
using Shikari.Services.RaidPlanIo;

namespace Shikari.Services.WtfDig;

public sealed partial class WtfDigClient
{
    public async Task<PreparedWtfDig> PrepareWithBoardsAsync(WtfDigPreview preview, string directory, bool images,
        Func<string, CancellationToken, Task<string>> fetchBoard, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var prepared = await PrepareAsync(preview, directory, images, cancel).ConfigureAwait(false);
        try
        {
            var boards = new Dictionary<string, PlanDocument>(StringComparer.Ordinal);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            budget.CancelAfter(TimeSpan.FromSeconds(120));
            long downloaded = 0;
            var links = preview.Links.DistinctBy(l => l.Code).Take(24).ToArray();
            foreach (var link in links)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    var json = await fetchBoard(link.Code, budget.Token).ConfigureAwait(false);
                    budget.Token.ThrowIfCancellationRequested();
                    downloaded += System.Text.Encoding.UTF8.GetByteCount(json);
                    if (downloaded > 48 * 1024 * 1024) { prepared.Warnings.Add("Editable board download limit reached; remaining source links are retained."); break; }
                    if (!RaidPlanIoImporter.TryImport(json, out var board, out var report, out var error) || board == null)
                        throw new InvalidDataException(error);
                    boards.Add(link.Code, board);
                    foreach (var warning in report.Notes.Distinct().Take(6)) prepared.Warnings.Add(link.Label + ": " + warning);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                { prepared.Warnings.Add("Editable board downloads timed out; available boards and guide notes are retained."); break; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or PlanFetchException or
                    Newtonsoft.Json.JsonException or FormatException or InvalidCastException or ArgumentException or OverflowException)
                { prepared.Warnings.Add(link.Label + ": board unavailable — " + ex.Message); }
            }
            if (preview.Links.Count > links.Length) prepared.Warnings.Add("Only the first 24 linked boards were downloaded.");
            cancel.ThrowIfCancellationRequested();
            WtfDigBoardComposer.Merge(preview, prepared, boards);
            PlanNormaliser.Normalise(prepared.Plan);
            // Editable geometry replaces some guide images. Retain only assets still used by the combined plan.
            var backdrops = prepared.Plan.Slides.Select(s => s.BackdropId).ToHashSet(StringComparer.Ordinal);
            foreach (var path in prepared.CreatedFiles.ToArray())
                if (!backdrops.Contains(Path.GetFileName(path)))
                {
                    File.Delete(path);
                    prepared.CreatedFiles.Remove(path);
                }
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }
}
