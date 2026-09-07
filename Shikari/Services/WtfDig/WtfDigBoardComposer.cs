using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Shikari.Model;

namespace Shikari.Services.WtfDig;

/// <summary>Joins explicit source-step references without guessing diagram meaning from positions.</summary>
public static class WtfDigBoardComposer
{
    public static void Merge(WtfDigPreview preview, PreparedWtfDig prepared, IReadOnlyDictionary<string, PlanDocument> boards)
    {
        var plan = prepared.Plan;
        var used = new HashSet<(string Code, int Step)>();
        var maps = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        var linked = new Dictionary<(string Code, string Id), string>();
        foreach (var pair in boards)
        {
            var map = new Dictionary<int, int>();
            for (var index = 0; index < pair.Value.Roster.Count; index++)
            {
                var source = pair.Value.Roster[index];
                var matches = plan.Roster.Select((slot, seat) => (slot, seat)).Where(p =>
                    !string.IsNullOrEmpty(source.Placeholder) && p.slot.Placeholder.Equals(source.Placeholder, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) continue;
                var target = matches[0]; map[index] = target.seat;
                if (target.slot.JobId == 0) target.slot.JobId = source.JobId;
            }
            maps[pair.Key] = map;
        }
        foreach (var slide in plan.Slides.ToArray())
        {
            var refs = preview.BoardReferences.Where(r => r.SlideId == slide.Id).ToArray();
            if (refs.Length != 1) continue; // Multiple alternatives need to stay explicit.
            var reference = refs[0];
            if (!boards.TryGetValue(reference.Code, out var board)) continue;
            var step = Step(reference.Url);
            var source = board.Slides.SingleOrDefault(s => s.SourceStep == step);
            if (source == null) { prepared.Warnings.Add(slide.Title + ": linked step was not present; full available board retained below."); continue; }
            Fill(slide, source, board, reference.Code);
            slide.SourceUrl = reference.Url; slide.SourceLabel = slide.Title;
            used.Add((reference.Code, step)); linked.TryAdd((reference.Code, source.Id), slide.Id);
        }
        foreach (var link in preview.Links)
        {
            if (!boards.TryGetValue(link.Code, out var board)) continue;
            foreach (var source in board.Slides)
            {
                if (!used.Add((link.Code, source.SourceStep))) continue;
                if (plan.Slides.Count >= 256) { WarnLimit(); break; }
                var slide = new Slide { Title = link.Label + " / " + source.Title,
                    SourceUrl = "https://raidplan.io/plan/" + link.Code + "#" + (source.SourceStep + 1), SourceLabel = link.Label };
                Fill(slide, source, board, link.Code);
                plan.Slides.Add(slide); linked[(link.Code, source.Id)] = slide.Id;
            }
            // Keep imported board timing links, but never activate unverified source timings.
            foreach (var entry in board.Timeline)
            {
                if (plan.Timeline.Count >= 4096 || !linked.TryGetValue((link.Code, entry.SlideId), out var id)) continue;
                var copy = JsonConvert.DeserializeObject<TimelineEntry>(JsonConvert.SerializeObject(entry))!;
                copy.Id = Guid.NewGuid().ToString("N"); copy.SlideId = id; copy.Enabled = false;
                foreach (var assignment in copy.Assignments)
                    assignment.SlotIndex = maps[link.Code].GetValueOrDefault(assignment.SlotIndex, -1);
                copy.Assignments.RemoveAll(a => a.SlotIndex < 0);
                plan.Timeline.Add(copy);
            }
        }
        plan.Notes += "\nEditable boards: " + boards.Count + ".\n" + string.Join("\n", preview.Links.Select(l => l.Label + ": " + l.Url));
        return;

        void Fill(Slide target, Slide source, PlanDocument board, string code)
        {
            target.Items = source.Items.Select(i => i.Clone()).ToList();
            foreach (var item in target.Items)
                if (item.SlotIndex >= 0) item.SlotIndex = maps[code].GetValueOrDefault(item.SlotIndex, -1);
            target.SourceStep = source.SourceStep;
            target.ArenaOverride = JsonConvert.DeserializeObject<ArenaSettings>(JsonConvert.SerializeObject(source.ArenaOverride ?? board.Arena))!;
            // A guide image would cover the editable diagram; its source remains in the notes.
            target.BackdropId = source.BackdropId; target.BackdropOpacity = source.BackdropOpacity;
            if (!string.IsNullOrWhiteSpace(source.Notes)) target.Notes += (target.Notes.Length > 0 ? "\n\n" : "") + source.Notes;
            target.Notes += "\nBoard: https://raidplan.io/plan/" + code + "#" + (source.SourceStep + 1);
        }
        void WarnLimit()
        {
            const string warning = "The combined guide reached 256 slides. Remaining board links are retained in plan notes.";
            if (!prepared.Warnings.Contains(warning)) prepared.Warnings.Add(warning);
        }
    }

    private static int Step(string url)
    {
        var hash = new Uri(url).Fragment.TrimStart('#');
        if (hash.StartsWith("step=", StringComparison.Ordinal)) hash = hash[5..].Split('&')[0];
        return int.TryParse(hash, out var oneBased) && oneBased > 0 ? oneBased - 1 : 0;
    }
}
