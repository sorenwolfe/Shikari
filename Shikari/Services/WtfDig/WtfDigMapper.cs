using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Shikari.Model;
using Shikari.Services.RaidPlanIo;
namespace Shikari.Services.WtfDig;

public sealed class WtfDigPreview
{
    public PlanDocument Plan { get; } = PlanDocument.CreateDefault();
    public List<string> Warnings { get; } = new();
    public List<WtfDigBoardLink> Links { get; } = new();
    public List<WtfDigBoardReference> BoardReferences { get; } = new();
    public List<WtfDigVariant> MissingVariants { get; } = new();
    public List<WtfDigVariant> VariantChoices { get; } = new();
    public Dictionary<string, string> ImageReferences { get; } = new();
}

public static class WtfDigMapper
{
    public static WtfDigPreview Convert(WtfDigGuide guide, WtfDigSelection selection)
    {
        if (selection.Role is not ("Tank" or "Healer" or "Melee" or "Ranged") || selection.Party is not (1 or 2))
            throw new InvalidDataException("Choose a role and group.");
        var strat = guide.Strategies.SingleOrDefault(s => (string?)s["stratName"] == selection.Strategy)
            ?? throw new InvalidDataException("Choose a strategy from this guide.");
        var preview = new WtfDigPreview();
        var plan = preview.Plan;
        plan.Slides.Clear();
        plan.Name = guide.Title + " / " + selection.Strategy;
        plan.Encounter = guide.Title;
        plan.Author = "WTFDIG contributors (source attribution in notes)";
        plan.Notes = $"Source: {guide.Link.Url}\nStrategy: {selection.Strategy}; role: {selection.Role}; group: {selection.Party}\nRetrieved: {guide.RetrievedUtc:O}\nSource SHA256: {guide.SourceHash}\nGuide notes and references. Timings need verification; no automatic status rules were inferred.";
        AddLinks(strat["stratUrl"], "Strategy resources", preview);
        var phases = (JArray)strat["strats"]!;
        for (var p = 0; p < phases.Count; p++)
        {
            if (phases[p] is not JObject phase || LiteralData.IsUnsupported(phase)) { Warn("A phase uses unsupported dynamic data."); continue; }
            var phaseName = (string?)phase["phaseName"] ?? $"Phase {p + 1}";
            AddLinks(phase["url"], phaseName, preview);
            var mechs = Choose(phase["mechs"], phaseName + "/mechanics") as JArray;
            if (mechs == null || mechs.Count == 0) AddSlide(phase, phaseName, phaseName, "");
            else for (var m = 0; m < mechs.Count; m++)
            {
                if (mechs[m] is not JObject mech || LiteralData.IsUnsupported(mech)) { Warn($"{phaseName}: a mechanic uses unsupported dynamic data."); continue; }
                var name = (string?)mech["mechanic"] ?? $"Step {m + 1}";
                AddSlide(mech, phaseName + " / " + name, phaseName + "/" + m, Text(phase["description"], phaseName + "/description"));
            }
            if (phase["boardCode"] != null) Warn("Native strategy-board codes are preserved as source references; use a linked raidplan for editable geometry.");
        }
        if (plan.Slides.Count > 256) throw new InvalidDataException("This guide exceeds 256 slides; import a linked raidplan instead.");
        if (guide.Config["timeline"] is JArray timeline)
        {
            foreach (var item in timeline.OfType<JObject>().Take(4096))
            {
                if (item["startTimeMs"]?.Type is not (JTokenType.Integer or JTokenType.Float)) continue;
                var time = (float)item["startTimeMs"]! / 1000f;
                if (!float.IsFinite(time) || time < 0 || time > 3600) continue;
                var label = (string?)item["mechName"] ?? "Imported timing";
                // Timing names are not a reliable link to diagram steps; leave that association explicit.
                plan.Timeline.Add(new TimelineEntry { Label = label, Trigger = TriggerKind.CombatTime, TimeSeconds = time, SortTime = time, Enabled = false });
            }
        }
        if (preview.Links.Count == 0) Warn("This guide has no linked editable boards; its notes and reference images are retained.");
        if (plan.Timeline.Count > 0) Warn("Imported timeline entries are disabled and unlinked until their timing and slide associations are verified.");
        plan.Notes += "\nVariants: " + string.Join(", ", selection.Variants.Select(v => v.Key + "=" + v.Value));
        plan.Notes += "\nConversion notes:\n" + string.Join("\n", preview.Warnings);
        return preview;

        void Warn(string message) { if (!preview.Warnings.Contains(message)) preview.Warnings.Add(message); }
        JToken? Choose(JToken? value, string key)
        {
            if (LiteralData.IsUnsupported(value)) { Warn(key + ": unsupported source expression; check the original guide."); return null; }
            if (value is not JObject variants) return value;
            if (variants.Properties().Any(p => p.Name.StartsWith('$'))) { Warn(key + ": unsupported variant data."); return null; }
            if (variants.Count > 1 && !preview.VariantChoices.Any(v => v.Key == key))
                preview.VariantChoices.Add(new WtfDigVariant(key, variants.Properties().Select(p => p.Name).ToArray()));
            if (variants.Count == 0) return null;
            if (selection.Variants.TryGetValue(key, out var selected) && variants[selected] != null) return Checked(variants[selected]);
            if (variants.Count == 1) return Checked(variants.Properties().First().Value);
            if (variants[selection.Strategy] != null) return Checked(variants[selection.Strategy]);
            // The site's strategy-specific defaults override its general toggle defaults.
            var phaseKey = Regex.Replace(key.Split('/')[0].ToLowerInvariant(), "[^a-z0-9]", "");
            foreach (var toggle in (guide.Config["toggles"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var toggleKey = (string?)toggle["key"] ?? "";
                if (toggleKey.Length == 0 || !phaseKey.StartsWith(toggleKey, StringComparison.OrdinalIgnoreCase)) continue;
                var choice = selection.Variants.GetValueOrDefault("toggle/" + toggleKey,
                    (string?)guide.Config["strats"]?[selection.Strategy]?["defaults"]?[toggleKey] ?? (string?)toggle["defaultValue"] ?? "");
                if (variants[choice] != null) return Checked(variants[choice]);
            }
            preview.MissingVariants.Add(new WtfDigVariant(key, variants.Properties().Select(p => p.Name).ToArray()));
            return null;

            JToken? Checked(JToken? chosen)
            {
                if (!LiteralData.IsUnsupported(chosen)) return chosen;
                Warn(key + ": unsupported source expression; check the original guide.");
                return null;
            }
        }
        string Text(JToken? value, string key)
        {
            var chosen = Choose(value, key);
            return chosen?.Type == JTokenType.String ? Plain(chosen.ToString()) : "";
        }
        void AddSlide(JObject node, string title, string key, string phaseNotes)
        {
            var slide = new Slide { Title = title, SourceUrl = guide.Link.Url, GuideUrl = guide.Link.Url, SourceLabel = title };
            var notes = new List<string> { phaseNotes };
            foreach (var field in new[] { "description", "action", "notes" }) notes.Add(Text(node[field], key + "/" + field));
            var image = Text(node["imageUrl"], key + "/image");
            AddLinks(node["url"], title, preview, slide.Id);
            if (node["strats"] is JArray roleStrats)
            {
                foreach (var role in roleStrats.OfType<JObject>())
                {
                    if ((role["role"] != null && (string?)role["role"] != selection.Role) ||
                        (role["party"] != null && (int?)role["party"] != selection.Party)) continue;
                    if (role["toggleKey"]?.Type == JTokenType.String)
                    {
                        var toggle = role["toggleKey"]!.ToString();
                        var choices = roleStrats.OfType<JObject>().Where(r => (string?)r["toggleKey"] == toggle &&
                            (r["role"] == null || (string?)r["role"] == selection.Role) &&
                            (r["party"] == null || (int?)r["party"] == selection.Party))
                            .Select(r => (string?)r["toggleValue"]).OfType<string>().Distinct().ToArray();
                        if (!preview.VariantChoices.Any(v => v.Key == "toggle/" + toggle)) preview.VariantChoices.Add(new WtfDigVariant("toggle/" + toggle, choices));
                        if (!selection.Variants.TryGetValue("toggle/" + toggle, out var chosen) || !choices.Contains(chosen))
                        { if (!preview.MissingVariants.Any(v => v.Key == "toggle/" + toggle)) preview.MissingVariants.Add(new WtfDigVariant("toggle/" + toggle, choices)); continue; }
                        if ((string?)role["toggleValue"] != chosen) continue;
                    }
                    notes.Add(Text(role["description"], key + "/role-description"));
                    var roleImage = Text(role["imageUrl"], key + "/role-image");
                    if (roleImage.Length > 0) image = roleImage;
                    if (role["mask"] != null || role["transform"] != null || role["alignmentTransforms"] != null)
                        Warn(title + ": spotlight masks and rotations are not converted; refer to the source diagram.");
                }
            }
            else if (node["strats"] != null) Warn(title + ": role instructions require an unsupported source expression.");
            if (node["arenaData"] != null) Warn(title + ": native arena geometry is not converted by this importer.");
            if (node["transform"] != null || node["alignmentTransforms"] != null) Warn(title + ": source diagram transforms are not applied.");
            if (image.Length > 0)
            {
                // Source image paths are relative to the fight route's containing directory.
                if (Uri.TryCreate(new Uri("https://wtfdig.info/" + guide.Link.Route), image, out var imageUri) && imageUri.Scheme == "https")
                { preview.ImageReferences[slide.Id] = imageUri.AbsoluteUri; notes.Add("Reference image: " + imageUri.AbsoluteUri); }
            }
            notes.Add("Source: " + guide.Link.Url);
            slide.Notes = string.Join("\n\n", notes.Where(n => !string.IsNullOrWhiteSpace(n)));
            plan.Slides.Add(slide);
        }
    }

    private static void AddLinks(JToken? value, string label, WtfDigPreview preview, string slideId = "")
    {
        if (value == null || LiteralData.IsUnsupported(value)) return;
        if (value is JObject obj) { foreach (var p in obj.Properties()) AddLinks(p.Value, label + " / " + p.Name, preview, slideId); return; }
        if (value is JArray array) { foreach (var item in array) AddLinks(item, label, preview, slideId); return; }
        if (value.Type != JTokenType.String || !Uri.TryCreate(value.ToString(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != "raidplan.io" || !uri.IsDefaultPort || uri.UserInfo.Length > 0) return;
        var parsed = PlanUrlParser.Parse(uri.AbsoluteUri);
        if (parsed.IsValid && !preview.Links.Any(l => l.Code == parsed.Code))
            preview.Links.Add(new WtfDigBoardLink(label, uri.AbsoluteUri, parsed.Code));
        if (parsed.IsValid && slideId.Length > 0)
            preview.BoardReferences.Add(new WtfDigBoardReference(slideId, uri.AbsoluteUri, parsed.Code));
    }
    private static string Plain(string text) => WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]{0,1000}>", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
}
