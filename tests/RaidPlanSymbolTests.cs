using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.RaidPlanIo;
using Shikari.Services.Replay;
using Shikari.Services.WtfDig;

namespace Shikari.Tests;

public static class RaidPlanSymbolTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Near(float actual, float expected, string message) => Check(MathF.Abs(actual - expected) < .00015f, message + $": {actual} != {expected}");
    private static PlanDocument Import(JArray nodes, out RaidPlanIoReport report)
    {
        // Same explicit landmark frame as the area geometry tests: one normalized unit = 1000px.
        var cy = 470f / .83f;
        foreach (var pair in new[] { ("A",-100,-100), ("B",100,-100), ("C",100,100), ("D",-100,100), ("1",0,-100), ("2",100,0), ("3",0,100), ("4",-100,0) })
            nodes.Add(new JObject { ["type"] = "waypoint", ["attr"] = new JObject { ["wayId"] = pair.Item1 }, ["meta"] = new JObject { ["pos"] = new JObject { ["x"] = 500 + pair.Item2, ["y"] = cy + pair.Item3 } } });
        Check(RaidPlanIoImporter.TryImport(new JObject { ["nodes"] = nodes }.ToString(), out var doc, out report, out var error), error);
        return doc!;
    }
    private static JObject Sprite(string type, string value) => new()
    {
        ["type"] = type,
        ["attr"] = new JObject { [type == "emoji" ? "emoji" : "asset"] = value },
        ["meta"] = new JObject { ["pos"] = new JObject { ["x"] = 500, ["y"] = 400 },
            ["size"] = new JObject { ["w"] = 48, ["h"] = 64 }, ["scale"] = new JObject { ["x"] = .5, ["y"] = .5 } }
    };
    private static CanvasItem Symbol(PlanDocument doc) => doc.Slides.SelectMany(s => s.Items).Single(i => i.Kind == CanvasItemKind.Symbol);
    public static void Run()
    {
        const string json = """
            {"nodes":[
              {"type":"emoji","attr":{"emoji":"🐲"},"meta":{"pos":{"x":100,"y":200},"size":{"w":36,"h":36},"scale":{"x":3.05,"y":3.05}}},
              {"type":"marker","attr":{"text":"","asset":"ffxiv/legacy/icon_hd/214336.png"},"meta":{"pos":{"x":200,"y":200},"size":{"w":48,"h":64},"scale":{"x":0.5,"y":0.5}}}
            ]}
            """;
        Check(RaidPlanIoImporter.TryImport(json, out var doc, out _, out var error), error);
        Check(doc!.Slides.SelectMany(s => s.Items).Count(i => (int)i.Kind == 8) == 2,
            "Positioned dragon emoji and status artwork must import as two editable Symbols, not notes or anonymous players.");
        Check(RaidPlanIoImporter.TryImport(new JObject { ["nodes"] = new JArray(Sprite("emoji", "🐲")) }.ToString(), out var symbolOnly, out _, out error), error);
        var lone = Symbol(symbolOnly!);
        Check(lone.Position.X > 0 && lone.Position.X < 1 && lone.Position.Y > 0 && lone.Position.Y < 1 && lone.Extent.X < .5f && lone.Extent.Y < .5f,
            "A symbol-only source board must fit its positioned artwork when there are no player or waymark anchors");

        var iconNode = Sprite("marker", "ffxiv/legacy/icon_hd/214336.png");
        iconNode["attr"]!["text"] = "MT";
        iconNode["attr"]!["opacity"] = .5;
        iconNode["meta"]!["angle"] = 90;
        iconNode["meta"]!["origin"] = new JObject { ["x"] = "left", ["y"] = "top" };
        iconNode["meta"]!["flip"] = new JObject { ["x"] = true, ["y"] = true };
        doc = Import(new JArray(iconNode), out var report);
        var icon = Symbol(doc);
        Check(icon.IconId == 214336 && icon.Emoji.Length == 0 && icon.Text == "MT" && icon.SlotIndex == -1,
            "Game artwork IDs and authored captions survive without interpreting either as a player or status ID");
        Near(icon.Extent.X, .012f, "24px status half width"); Near(icon.Extent.Y, .016f, "32px status half height");
        Near(icon.Position.X, .484f, "Rotated left/top origin moves x by half height");
        Near(icon.Position.Y, .34573494f, "Rotated left/top origin moves y by half width");
        Check(icon.Rotation == 90 && icon.FlipX && icon.FlipY && icon.Color == 0x80FFFFFF, "Artwork transform and opacity survive");
        Check(report.GameIconSymbols == 1 && report.Notes.Any(n => n.Contains("not status IDs")), "Coverage identifies retained game artwork, not a detected status condition");
        Check(doc.FormatVersion == 5, "Symbol plans must explicitly require format 5");
        iconNode["meta"]!["scale"]!["x"] = -.5;
        Check(!Symbol(Import(new JArray(iconNode), out _)).FlipX, "Negative source scale and explicit flip cancel exactly once");

        var emojiNode = Sprite("emoji", "6️⃣");
        emojiNode["meta"]!["size"] = new JObject { ["w"] = 36, ["h"] = 36 };
        emojiNode["meta"]!["scale"] = new JObject { ["x"] = 3.05, ["y"] = 3.05 };
        var emojiPlan = Import(new JArray(emojiNode), out report);
        var emoji = Symbol(emojiPlan);
        Check(emoji.Emoji == "6\ufe0f\u20e3" && emoji.IconId == 0 && emoji.SlotIndex == -1, "Whole keycap grapheme stays one positioned symbol");
        Near(emoji.Extent.X, .0549f, "Emoji source scale is retained");
        Check(report.EmojiSymbols == 1 && report.NotesMoved == 0 && !report.Skipped.ContainsKey("emoji") && !emojiPlan.Slides[0].Notes.Contains("6️⃣"), "Spatial emoji are no longer relocated to notes");
        Check(SymbolValidation.IsSingleGrapheme("👩🏽‍🚀") && !SymbolValidation.IsSingleGrapheme("🐲🐲") &&
            !SymbolValidation.IsSingleGrapheme("\ud800") && !SymbolValidation.IsSingleGrapheme("x\n"), "Emoji validation accepts complete joined sequences and rejects malformed/multiple ones");
        Check(SymbolValidation.Caption("👩🏽‍🚀") == "👩🏽‍🚀" &&
            SymbolValidation.Caption("🏴\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F") == "🏴\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F",
            "Authored captions retain complete joined and tag emoji, not separated or incomplete pictographs");
        Check(!SymbolValidation.Caption("a\u202eb\n").Contains('\u202e'), "Caption cleanup strips bidi control formatting");

        var role = Sprite("marker", "game/ffxiv/job/role_healer.png"); role["attr"]!["text"] = "H1";
        var job = Sprite("marker", "game/ffxiv/job/whm.png"); job["attr"]!["text"] = "H2";
        var unknown = Sprite("marker", "https://untrusted.invalid/whm.png"); unknown["attr"]!["text"] = "H1";
        var mixed = Import(new JArray(role, job, unknown, Sprite("marker", "game/ffxiv/mark/mark_link1.png"),
            Sprite("marker", "game/ffxiv/mark/mark_stop1.png"), Sprite("marker", "game/ffxiv/cut/4.svg")), out report);
        var players = mixed.Slides[0].Items.Where(i => i.Kind == CanvasItemKind.PlayerToken).ToArray();
        Check(players.Length == 2 && players.All(i => i.SlotIndex >= 0) && mixed.Roster[3].JobId == 24,
            "Recognized role and job artwork keep their seats; unrelated artwork cannot impersonate a job by filename");
        Check(RaidPlanSymbolAssets.IsJobOrRole("game/ffxiv/job/bard.png") && JobAssets.Read("game/ffxiv/job/bard.png").JobId == 23,
            "The source catalog's bard filename remains a genuine job token rather than a symbol fallback");
        var fallbacks = mixed.Slides[0].Items.Where(i => i.Kind == CanvasItemKind.Symbol).ToArray();
        Check(fallbacks.Length == 4 && fallbacks.All(i => i.SlotIndex == -1), "Unknown art and target marks remain visible unbound symbols");
        Check(fallbacks.Any(i => i.IconId == 61211) && fallbacks.Any(i => i.IconId == 61221),
            "Verified native Link1 and Stop1 artwork remains pictures rather than generic labels");
        Check(fallbacks.Any(i => (int?)JObject.FromObject(i)["SymbolAsset"] == 1), "Four-person source diagram remains one positioned symbol");
        Check(report.SymbolFallbacks.ContainsKey("https://untrusted.invalid/whm.png") && report.FallbackSymbols == 1,
            "Fallback diagnostics retain the precise source asset rather than claiming complete artwork conversion");
        Check(!RaidPlanSymbolAssets.TryGameIcon("https://untrusted.invalid/ffxiv/legacy/icon_hd/214336.png", out _) &&
            !RaidPlanSymbolAssets.TryGameIcon("ffxiv/legacy/icon_hd/000000.png", out _) &&
            RaidPlanSymbolAssets.TryGameIcon("https://cdn.raidplan.io/ffxiv/legacy/icon_hd/214337.png", out var canonical) && canonical == 214337,
            "Only bounded known paths or the exact official CDN yield a native artwork ID");
        Check(!RaidPlanSymbolAssets.TryGameIcon("game/ffxiv/mark/mark_link9.png", out _) &&
            !RaidPlanSymbolAssets.TryDiagram("https://untrusted.invalid/game/ffxiv/cut/4.svg", out _), "Unverified target IDs and external artwork paths remain unsupported");
        var diagramNode = Sprite("marker", "game/ffxiv/cut/4.svg");
        diagramNode["meta"]!["size"] = new JObject { ["w"] = 88, ["h"] = 44 };
        diagramNode["meta"]!["scale"] = new JObject { ["x"] = 2, ["y"] = .5 };
        diagramNode["meta"]!["angle"] = 37;
        diagramNode["meta"]!["flip"] = new JObject { ["x"] = true, ["y"] = true };
        var diagramPlan = Import(new JArray(diagramNode), out report);
        var diagram = Symbol(diagramPlan);
        Check(diagram.SymbolAsset == SymbolAsset.Cut4 && diagram.Emoji.Length == 0 && diagram.IconId == 0 &&
            diagram.Rotation == 37 && diagram.FlipX && diagram.FlipY && report.DiagramSymbols == 1 && report.FallbackSymbols == 0,
            "The known four-person diagram preserves its own identity and all transforms");
        Near(diagram.Extent.X, .088f, "Cut4 source half width"); Near(diagram.Extent.Y, .011f, "Cut4 source half height");
        Check(diagram.Clone().SymbolAsset == SymbolAsset.Cut4 && diagram.Clone().Extent == diagram.Extent && diagram.Clone().FlipY, "Cloning retains a bounded built-in diagram");

        var clone = icon.Clone();
        Check(clone.Id != icon.Id && clone.IconId == icon.IconId && clone.Extent == icon.Extent && clone.FlipX && clone.FlipY, "Cloning retains symbol artwork and transforms with a new object identity");
        Check(emoji.Clone().Emoji == emoji.Emoji, "Clone preserves an entire emoji sequence");
        foreach (var settings in new[] { PlanJson.Compact(), PlanJson.Readable() })
        {
            var roundtrip = JsonConvert.DeserializeObject<PlanDocument>(JsonConvert.SerializeObject(doc, settings), settings)!;
            var restored = Symbol(roundtrip);
            Check(restored.IconId == 214336 && restored.FlipX && restored.FlipY && restored.SlotIndex == -1, "Disk/compact icon metadata survives round trip");
            Near(restored.Extent.X, icon.Extent.X, "Disk/compact half width"); Near(restored.Extent.Y, icon.Extent.Y, "Disk/compact half height");
            var payload = JObject.Parse(JsonConvert.SerializeObject(emojiPlan, settings));
            Check((int)payload["FormatVersion"]! == 5 && (int)payload["FormatVersion"]! > 4,
                "The header makes an old format-4 client's existing version guard reject positioned symbols");
            Check(Symbol(JsonConvert.DeserializeObject<PlanDocument>(payload.ToString(), settings)!).Emoji == emoji.Emoji, "Disk/compact preserves keycap codepoints");
            var diagramRestored = Symbol(JsonConvert.DeserializeObject<PlanDocument>(JsonConvert.SerializeObject(diagramPlan, settings), settings)!);
            Check(diagramRestored.SymbolAsset == SymbolAsset.Cut4 && diagramRestored.Extent == diagram.Extent &&
                diagramRestored.FlipX && diagramRestored.FlipY && diagramRestored.Rotation == 37, "Disk/compact preserves a diagram's identity and transformed bounds");
        }
        Check(ShareCode.TryDecode(ShareCode.Encode(doc), out var shared, out error), error);
        Check(Symbol(shared!).IconId == icon.IconId && Symbol(shared!).FlipX, "Share decoding retains native artwork and flips");
        Check(ShareCode.TryDecode(ShareCode.Encode(emojiPlan), out shared, out error) && Symbol(shared!).Emoji == emoji.Emoji, "Share code preserves a whole keycap sequence");
        Check(ShareCode.TryDecode(ShareCode.Encode(diagramPlan), out shared, out error) && Symbol(shared!).SymbolAsset == SymbolAsset.Cut4 &&
            Symbol(shared!).Rotation == 37 && Symbol(shared!).FlipX, "Share code preserves the bounded diagram identifier and transform");
        diagram.SymbolAsset = (SymbolAsset)999;
        Check(!SymbolValidation.IsValid(diagram) && !ShareCode.TryDecode(ShareCode.Encode(diagramPlan), out _, out _), "Unknown diagram identifiers are rejected before rendering");
        PlanNormaliser.Normalise(diagramPlan);
        Check(diagram.SymbolAsset == SymbolAsset.None && SymbolValidation.IsValid(diagram) && diagram.Text.Length > 0, "Malformed disk diagram identifiers normalize to a visible caption");
        diagram.SymbolAsset = SymbolAsset.Cut4; diagram.IconId = 61211;
        Check(!SymbolValidation.IsValid(diagram), "A diagram cannot ambiguously identify native artwork at the same time");
        diagram.IconId = 0;
        var older = PlanDocument.CreateDefault();
        Check(older.FormatVersion == 1 && ShareCode.TryDecode(ShareCode.Encode(older), out _, out error), "Legacy symbol-free plans remain compatible");
        var future = PlanDocument.CreateDefault(); future.FormatVersion = PlanDocument.CurrentFormatVersion + 1;
        Check(!ShareCode.TryDecode(ShareCode.Encode(future), out _, out error) && error.Contains("newer version"), "Share guard rejects a future plan version");
        var rejected = false; try { PlanNormaliser.Normalise(future); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Disk normalization must not silently rewrite an unsupported future format");

        var recording = new ReplayBuffer(emojiPlan, 0, DateTime.UtcNow).Attempt;
        Check(ReplayValidation.IsValid(recording) && Symbol(recording.Plan).Emoji == emoji.Emoji && recording.Plan.FormatVersion == 5,
            "Replay snapshot carries positioned symbols and is valid for the current client");
        emoji.IconId = 214336;
        Check(!SymbolValidation.IsValid(emoji) && !ShareCode.TryDecode(ShareCode.Encode(emojiPlan), out _, out _), "Ambiguous emoji plus native icon payload is rejected on share import");
        emoji.IconId = 0; emoji.Extent = new Vector2(float.NaN, .1f);
        Check(!SymbolValidation.IsValid(emoji), "Nonfinite symbol bounds cannot reach the renderer");
        Symbol(recording.Plan).Extent = emoji.Extent;
        Check(!ReplayValidation.IsValid(recording), "Replay validates symbol bounds before drawing");
        emoji.Emoji = new string('x', 1024); emoji.SlotIndex = 0; emoji.Position = new Vector2(float.PositiveInfinity);
        PlanNormaliser.Normalise(emojiPlan);
        Check(SymbolValidation.IsValid(emoji) && emoji.SlotIndex == -1 && emoji.Emoji.Length == 0 && emoji.Text.Length <= 256,
            "Malformed persisted symbol becomes a bounded caption without a player binding");
        var extremePlan = PlanDocument.CreateDefault(); extremePlan.Slides[0].Items.Clear();
        var extremeSymbol = new CanvasItem { Kind = CanvasItemKind.Symbol, Emoji = "🐲", Position = new(float.MaxValue, -float.MaxValue) };
        extremePlan.Slides[0].Items.Add(extremeSymbol);
        Check(!SymbolValidation.IsValid(extremeSymbol) && !ShareCode.TryDecode(ShareCode.Encode(extremePlan), out _, out _) &&
            !ReplayValidation.IsValid(new ReplayBuffer(extremePlan, 0, DateTime.UtcNow).Attempt),
            "Finite but extreme symbol positions must be rejected before normalized coordinates overflow screen-space rendering");
        PlanNormaliser.Normalise(extremePlan);
        Check(SymbolValidation.IsValid(extremeSymbol) && extremeSymbol.Position == new Vector2(16,-16),
            "Invalid saved/imported symbol coordinates are clamped to bounded off-board positions");
        extremeSymbol.Position = new Vector2(-2,3);
        Check(SymbolValidation.IsValid(extremeSymbol), "Ordinary authored off-board symbols remain accepted");
        Console.WriteLine("PASS: editable emoji/game-art symbols, source transforms, role separation, fallbacks, format guards, clone/share/disk/replay");
    }

    public static void ActOne(string json)
    {
        Check(RaidPlanIoImporter.TryImport(json, out var doc, out var report, out var error), error);
        var symbols = doc!.Slides.SelectMany(s => s.Items).Where(i => i.Kind == CanvasItemKind.Symbol).ToArray();
        Check(symbols.Length == 25 && symbols.Count(i => i.Emoji == "🐲") == 3 && symbols.Count(i => i.IconId == 214336) == 17 && symbols.Count(i => i.IconId == 214337) == 5,
            "Exact Caro Act1 retains all three dragon positions and22 native artwork markers");
        Check(doc.Slides.Where(s => s.SourceStep is 6 or 7 or 8).All(s => s.Items.Any(i => i.Emoji == "🐲")), "Dragons stay on the exact source steps");
        Check(!doc.Slides.SelectMany(s => s.Items).Any(i => i.Kind == CanvasItemKind.PlayerToken && i.SlotIndex < 0 && i.Text.Length == 0), "Act1 has no status pictures masquerading as anonymous players");
        Check(report.EmojiSymbols == 3 && report.GameIconSymbols == 22 && report.FallbackSymbols == 0, "Act1 import coverage is complete for its symbol types");
        var preview = new WtfDigPreview(); preview.Plan.Slides.Clear();
        preview.Links.Add(new WtfDigBoardLink("Act 1", "https://raidplan.io/plan/44JJjqZ6Mcgaxnnn", "44JJjqZ6Mcgaxnnn"));
        foreach (var source in doc.Slides)
        {
            var slide = new Slide { Title = "Guide: Act 1 / " + source.SourceStep, Notes = "Keep guide instructions" };
            preview.Plan.Slides.Add(slide);
            preview.BoardReferences.Add(new WtfDigBoardReference(slide.Id, "https://raidplan.io/plan/44JJjqZ6Mcgaxnnn#" + (source.SourceStep + 1), "44JJjqZ6Mcgaxnnn"));
        }
        using var prepared = new PreparedWtfDig { Plan = JsonConvert.DeserializeObject<PlanDocument>(JsonConvert.SerializeObject(preview.Plan))! };
        WtfDigBoardComposer.Merge(preview, prepared, new Dictionary<string, PlanDocument> { ["44JJjqZ6Mcgaxnnn"] = doc });
        var composed = PlanNormaliser.Normalise(prepared.Plan);
        Check(composed.Slides.Count == 5 && composed.Slides.SelectMany(s => s.Items).Count(i => i.Kind == CanvasItemKind.Symbol) == 25,
            "Automatic WTFDIG composition preserves all25 Act1 symbols without additional import actions");
        Check(composed.Slides.All(s => s.Notes.Contains("Keep guide instructions")) &&
            composed.Slides.Where(s => s.SourceStep is 6 or 7 or 8).All(s => s.Items.Any(i => i.Emoji == "🐲")), "Source-step mapping preserves the correct dragon and guide explanation together");
        Check(preview.Plan.Slides.All(s => s.Items.Count == 0), "Composition does not mutate the guide preview");
        Console.WriteLine("PASS: exact Caro Act1 fixture:3 positioned dragons,22 native icon symbols, no anonymous status players");
    }

    public static void Corpus(string directory)
    {
        var emojis = 0; var native = 0; var diagrams = 0; var fallback = 0;
        foreach (var code in new[] { "44JJjqZ6Mcgaxnnn", "4P_QGHnBZ-nW8yH4", "SFa6J6wDrU9PlCJ4", "tr2jrddp4hkebxc9", "9zpa6vu5kxgtuwqc", "OnhUS061LkI3xlmg" })
        {
            var json = File.ReadAllText(Path.Combine(directory, code + ".json"));
            var nodes = (JArray)JObject.Parse(json)["nodes"]!;
            Check(RaidPlanIoImporter.TryImport(json, out var doc, out var report, out var error), error);
            Check(report.EmojiSymbols == nodes.Count(n => (string?)n["type"] == "emoji"), "Every source emoji retains a positioned object: " + code);
            Check(report.GameIconSymbols + report.DiagramSymbols + report.FallbackSymbols == nodes.Count(n => (string?)n["type"] == "marker" && !RaidPlanSymbolAssets.IsJobOrRole((string?)n["attr"]?["asset"])), "Every nonjob marker retains native artwork, a known diagram or an explicit fallback: " + code);
            Check(doc!.Slides.SelectMany(s => s.Items).Where(i => i.Kind == CanvasItemKind.Symbol).All(i => SymbolValidation.IsValid(i)), "All corpus symbols pass persisted validation: " + code);
            emojis += report.EmojiSymbols; native += report.GameIconSymbols; diagrams += report.DiagramSymbols; fallback += report.FallbackSymbols;
        }
        Check(emojis == 195 && native == 344 && diagrams == 2 && fallback == 0, $"Fresh six-board Caro symbol census remains covered: emoji={emojis},native={native},diagrams={diagrams},fallback={fallback}");
        Console.WriteLine($"PASS: six-board Caro corpus retains {emojis} positioned emojis,{native} native game icons,{diagrams} known diagrams,{fallback} labelled artwork fallbacks");
    }
}
