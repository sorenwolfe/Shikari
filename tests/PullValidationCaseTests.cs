using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shikari.Model;
using Shikari.Services;
using Shikari.Services.Replay;
using Shikari.Services.Storage;

namespace Shikari.Tests;

public static class PullValidationCaseTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action, string message)
    { try { action(); } catch (ArgumentException) { return; } throw new Exception(message); }
    private static void Settle(PullValidationCaseStore store)
    { Check(SpinWait.SpinUntil(() => { store.Poll(); return !store.Busy; }, 10000), "Case operation did not settle"); }
    private static (PlanDocument Plan, ReplayAttempt Attempt, PullValidationCase Case) Fixture()
    {
        var plan = PlanDocument.CreateDefault();
        plan.AdaptiveMechanics.Add(new AdaptiveMechanic { Id = "rule", Enabled = true, TerritoryId = 777,
            AnchorActionId = 100, WindowSeconds = 3, Branches = new() { new StatusBranch {
                StatusId = 10, MaximumSeconds = 3600, SlideId = plan.Slides[0].Id } } });
        var attempt = new ReplayAttempt { Plan = plan, Duration = 10, TerritoryId = 777 };
        attempt.Evidence.Url = "https://private.invalid/report/secret";
        attempt.Evidence.Actors.Add(new EvidenceActor { Id = 11, Name = "Private Actor Name", IsLocal = true });
        attempt.Evidence.Statuses.Add(new EvidenceStatus { ActorId = 11, StatusId = 10, Time = .1f });
        attempt.Casts.Add(new RecordedCast { Source = "Live", ActionId = 100, Occurrence = 1, StartTime = 0, ObservedTime = 0 });
        var expected = new[] { new PullExpectedAssignment { RuleId = "rule", AnchorActionId = 100, Occurrence = 1, BranchIndex = 0 } };
        var reviewed = PullValidationCases.Create("Reviewed spread", "Verified against the debuff and written strategy.",
            plan, attempt, 11, 777, PullValidationCases.Fingerprint(plan, attempt, 11, 777), expected);
        expected[0].BranchIndex = -1;
        Check(reviewed.Expected[0].BranchIndex == 0, "Creation must detach user expectations");
        return (plan, attempt, reviewed);
    }
    public static void Run()
    {
        Fingerprints(); RecordingRoundtripBinding(); Metadata(); Roundtrip(); Failures(); OrderedWrites(); MalformedLibraries(); Limits(); Disposal();
        Console.WriteLine("PASS: durable reviewed cases, exact evidence binding, detached metadata, bounded/malformed libraries, ordered nonblocking writes and last-good preservation");
    }
    private static void Fingerprints()
    {
        var f = Fixture();
        var before = JsonConvert.SerializeObject(f.Attempt);
        var original = f.Case.BindingFingerprint;
        Check(original.Length == 64 && PullValidationCases.IsCompatible(f.Case,
            PullValidationCases.Fingerprint(f.Plan, f.Attempt, 11, 777)), "Identical frozen inputs must match");
        Check(before == JsonConvert.SerializeObject(f.Attempt), "Fingerprinting mutated its source");
        Check(!PullValidationCases.IsCompatible(f.Case, PullValidationCases.Fingerprint(f.Plan, f.Attempt, 12, 777)), "Actor changes must invalidate the case");
        Check(!PullValidationCases.IsCompatible(f.Case, PullValidationCases.Fingerprint(f.Plan, f.Attempt, 11, 778)), "Scope changes must invalidate the case");
        f.Attempt.Frames.Add(new ReplayFrame { Time = 9 });
        f.Attempt.Evidence.Positions.Add(new EvidencePosition { Time = 9, ActorId = 11, Position = Vector2.One });
        f.Attempt.Casts[0].CasterWorldPosition = Vector3.One;
        Check(PullValidationCases.Fingerprint(f.Plan, f.Attempt, 11, 777) == original, "Unused positions/frames must not prevent replaying assignment cases");
        f.Plan.AdaptiveMechanics[0].Enabled = false;
        Check(PullValidationCases.Fingerprint(f.Plan, f.Attempt, 11, 777) != original, "Enabled test state must be bound");
        f.Plan.AdaptiveMechanics[0].Enabled = true;
        f.Plan.AdaptiveMechanics[0].Branches[0].AdditionalStatuses = null!;
        Check(PullValidationCases.Fingerprint(f.Plan, f.Attempt, 11, 777) != original, "Malformed null conditions must not hash like a valid empty list");
        f.Plan.AdaptiveMechanics[0].Branches[0].AdditionalStatuses = new();
        f.Attempt.Evidence.Statuses[0].Baseline = true;
        Check(PullValidationCases.Fingerprint(f.Plan, f.Attempt, 11, 777) != original, "Evidence changes must invalidate the case");
        f.Attempt.Evidence.Statuses[0].Baseline = false;
        f.Attempt.Casts[0].ObservedTime = .2f;
        Check(PullValidationCases.Fingerprint(f.Plan, f.Attempt, 11, 777) != original, "Observation arrival changes must invalidate the case");
        f.Attempt.Casts[0].ObservedTime = 0;
        f.Plan.Slides[0].Notes = "Reviewed destination board changed";
        Check(PullValidationCases.Fingerprint(f.Plan, f.Attempt, 11, 777) != original, "Destination board changes must invalidate the case");
    }
    private static void Metadata()
    {
        var f = Fixture();
        foreach (var bad in new[] { "", " ", new string('x', 81) })
            Reject(() => PullValidationCases.Create(bad, "review", f.Plan, f.Attempt, 11, 777, f.Case.BindingFingerprint, f.Case.Expected), "Missing or excessive name accepted");
        foreach (var bad in new[] { "", " ", new string('x', 1001) })
            Reject(() => PullValidationCases.Create("name", bad, f.Plan, f.Attempt, 11, 777, f.Case.BindingFingerprint, f.Case.Expected), "Missing or excessive independent review accepted");
        Reject(() => PullValidationCases.Create("name", "review", f.Plan, f.Attempt, 11, 777, "bad", f.Case.Expected), "Malformed fingerprint accepted");
        Reject(() => PullValidationCases.Create("name", "review", f.Plan, f.Attempt, 11, 777, f.Case.BindingFingerprint,
            Array.Empty<PullExpectedAssignment>()), "Empty reviewed case accepted");
        var duplicated = new[] { f.Case.Expected[0], f.Case.Expected[0] };
        Reject(() => PullValidationCases.Create("name", "review", f.Plan, f.Attempt, 11, 777, f.Case.BindingFingerprint, duplicated), "Duplicate rule occurrence accepted");
    }
    private static void RecordingRoundtripBinding()
    {
        var directory = DirectoryForTest(); Directory.CreateDirectory(directory);
        try
        {
            var f = Fixture();
            f.Plan.Roster[0].Name = "Retained roster member";
            f.Plan.Slides[0].Items.Add(new CanvasItem { Kind = CanvasItemKind.Symbol,
                Emoji = "🐉", Position = new Vector2(.1234567f, .7654321f), Extent = new Vector2(.0654321f, .1234567f),
                FlipX = true, Rotation = 17, Radius = .789f, InnerRadius = .123f, SlotIndex = 4 });
            f.Plan.Slides[0].Items.Add(new CanvasItem { Kind = CanvasItemKind.Zone, Zone = ZoneShape.Donut,
                Position = new Vector2(.777777f, .234567f), Radius = .35f, InnerRadius = .11f,
                Extent = new Vector2(.45678f, .12345f), Thickness = .987f, ConeAngle = 175 });
            f.Plan.Slides[0].Items.Add(new CanvasItem { Kind = CanvasItemKind.Arrow,
                Points = new() { new Vector2(.123456f, .987654f), new Vector2(.666666f, .111111f) },
                Position = new Vector2(.9f, .3f), Radius = .9f, Extent = Vector2.One, Thickness = .008f });
            f.Plan.Slides[0].BackdropOpacity = .93f; // No backdrop: this editor residue is intentionally not retained.
            var originalJson = JsonConvert.SerializeObject(f.Attempt, PlanJson.Compact());
            var originalHash = PullValidationCases.Fingerprint(f.Plan, f.Attempt, 11, 777);
            var reviewed = PullValidationCases.Create("Restart case", "Checked the written strategy independently.",
                f.Plan, f.Attempt, 11, 777, originalHash, f.Case.Expected);
            using (var store = new PullValidationCaseStore(directory))
            { Settle(store); Check(store.Save(reviewed), "Restart case save rejected"); Settle(store); }
            var recordingPath = Path.Combine(directory, "original-recording.json");
            File.WriteAllText(recordingPath, originalJson);
            var reloaded = JsonConvert.DeserializeObject<ReplayAttempt>(File.ReadAllText(recordingPath), PlanJson.Compact())!;
            Check(JsonConvert.SerializeObject(reloaded, PlanJson.Compact()) == originalJson,
                "Retained recording fixture did not roundtrip through actual persistence settings");
            Check(reloaded.Plan.Roster[0].Id != f.Plan.Roster[0].Id &&
                reloaded.Plan.Slides[0].Items[0].Id != f.Plan.Slides[0].Items[0].Id,
                "Fixture must exercise regenerated editor-only roster and item identities");
            using var fresh = new PullValidationCaseStore(directory); Settle(fresh);
            var reloadedHash = PullValidationCases.Fingerprint(reloaded.Plan, reloaded, 11, 777);
            Check(PullValidationCases.IsCompatible(fresh.Items.Single(), reloadedHash),
                "A reviewed case must remain compatible after its original recording and local case library reload");
            reloaded.Plan.Slides[0].Items[1].InnerRadius = .18f;
            Check(PullValidationCases.Fingerprint(reloaded.Plan, reloaded, 11, 777) != reloadedHash,
                "A meaningful retained board geometry change must still invalidate the case");
            reloaded.Plan.Slides[0].Items[1].InnerRadius = .11f;
            reloaded.Plan.AdaptiveMechanics[0].Branches[0].AdditionalStatuses.Add(new StatusCondition {
                StatusId = 20, MaximumSeconds = 3600 });
            Check(PullValidationCases.Fingerprint(reloaded.Plan, reloaded, 11, 777) != reloadedHash,
                "Retained rule condition changes must still invalidate the case");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static string DirectoryForTest() => Path.Combine(Path.GetTempPath(), "shikari-case-test-" + Guid.NewGuid().ToString("N"));
    private static void Roundtrip()
    {
        var directory = DirectoryForTest();
        try
        {
            var f = Fixture();
            using (var store = new PullValidationCaseStore(directory))
            {
                Settle(store); Check(store.WritesEnabled && store.Items.Count == 0, "Missing library must load as writable empty library");
                Check(store.Save(f.Case), "Reviewed case was not accepted");
                f.Case.Expected[0].BranchIndex = -1;
                Settle(store);
                Check(store.Error == null && store.Items.Count == 1 && store.Items[0].Expected[0].BranchIndex == 0,
                    "Durable save leaked caller changes or failed publication");
                store.Items[0].Name = "UI mutated its returned item";
            }
            var path = Path.Combine(directory, "pull-validation-cases.json");
            var raw = File.ReadAllText(path);
            Check(!raw.Contains("Private Actor Name") && !raw.Contains("private.invalid") && !raw.Contains("Frames") && !raw.Contains("Positions"),
                "Reviewed cases must not duplicate recording names, links or positions");
            using (var fresh = new PullValidationCaseStore(directory))
            {
                Settle(fresh);
                Check(fresh.Items.Count == 1 && fresh.Items[0].Name == "Reviewed spread" && fresh.Items[0].Expected[0].BranchIndex == 0,
                    "A fresh store did not recover the reviewed case");
                Check(fresh.Delete(fresh.Items[0].Id), "Delete was not accepted"); Settle(fresh);
            }
            using var empty = new PullValidationCaseStore(directory); Settle(empty);
            Check(empty.Items.Count == 0, "Deletion did not survive reload");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static void Failures()
    {
        var directory = DirectoryForTest();
        try
        {
            var rejectWrite = false;
            using var store = new PullValidationCaseStore(directory, (path, text) => {
                if (rejectWrite) throw new IOException("disk refused write");
                AtomicFile.WriteAllText(path, text);
            });
            Settle(store); var f = Fixture(); Check(store.Save(f.Case), "Initial save rejected"); Settle(store);
            var path = Path.Combine(directory, "pull-validation-cases.json"); var original = File.ReadAllText(path);
            var newer = PullValidationCases.Copy(f.Case); newer.Name = "New revision"; newer.UpdatedUtc = newer.UpdatedUtc.AddSeconds(1);
            rejectWrite = true; Check(store.Save(newer), "Failure trial rejected synchronously"); Settle(store);
            Check(store.Error != null && store.Items.Single().Name == "Reviewed spread" && File.ReadAllText(path) == original,
                "Failed save replaced last-good items or file");
            rejectWrite = false; Check(store.Save(newer), "Retry rejected"); Settle(store);
            Check(store.Error == null && store.Items.Single().Name == "New revision", "Retry did not become durable");
            Check(!store.Save(f.Case), "An older metadata revision overwrote a later durable revision");
            var sameRevision = PullValidationCases.Copy(newer); sameRevision.Name = "Stale equal timestamp";
            var accepted = store.Save(sameRevision); if (accepted) Settle(store);
            Check(!accepted, "A different payload at the same revision overwrote the durable case");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static void OrderedWrites()
    {
        var directory = DirectoryForTest();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            using var store = new PullValidationCaseStore(directory, (path, text) => {
                entered.Set(); Check(release.Wait(10000), "Blocked write timed out"); AtomicFile.WriteAllText(path, text);
            });
            Settle(store); var f = Fixture(); Check(store.Save(f.Case), "First save rejected");
            Check(entered.Wait(10000) && store.Busy && store.Items.Count == 0, "Save did not run in background or published before disk acknowledgment");
            var newer = PullValidationCases.Copy(f.Case); newer.Name = "Queued revision"; newer.UpdatedUtc = newer.UpdatedUtc.AddSeconds(1);
            Check(store.Save(newer), "Ordered queued save rejected");
            store.Poll(); Check(store.Busy && store.Items.Count == 0, "Poll must return while I/O is blocked");
            release.Set(); Settle(store);
            Check(store.Items.Single().Name == "Queued revision", "A late earlier write replaced the queued revision");
            store.Items.Single().Expected[0].BranchIndex = -1;
            var extra = Fixture().Case; Check(store.Save(extra), "Independent save rejected"); Settle(store);
            using var fresh = new PullValidationCaseStore(directory); Settle(fresh);
            Check(fresh.Items.Single(c => c.Id == f.Case.Id).Expected[0].BranchIndex == 0,
                "Mutating exposed items contaminated future durable writes");
        }
        finally { release.Set(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static void MalformedLibraries()
    {
        foreach (var raw in new[] { "{", "null", "{}", "{\"Version\":99,\"Cases\":[]}", "{\"Version\":1,\"Cases\":null}",
            "{\"Version\":1,\"Cases\":[] ,\"unexpected\":true}" })
        {
            var directory = DirectoryForTest(); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "pull-validation-cases.json"); File.WriteAllText(path, raw);
            try
            {
                using var store = new PullValidationCaseStore(directory); Settle(store);
                Check(store.Error != null && !store.WritesEnabled && !store.Save(Fixture().Case) && File.ReadAllText(path) == raw,
                    "Malformed or unsupported libraries must not be silently overwritten");
            }
            finally { Directory.Delete(directory, true); }
        }
    }
    private static void Limits()
    {
        var directory = DirectoryForTest(); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "pull-validation-cases.json");
        try
        {
            var seed = Fixture().Case;
            var cases = Enumerable.Range(0, 64).Select(_ => { var c = PullValidationCases.Copy(seed); c.Id = Guid.NewGuid().ToString("N"); return c; }).ToArray();
            File.WriteAllText(path, JsonConvert.SerializeObject(new { Version = 1, Cases = cases }));
            using (var store = new PullValidationCaseStore(directory))
            {
                Settle(store); Check(store.Items.Count == 64, "Library capacity fixture was not accepted");
                var before = File.ReadAllText(path);
                Check(store.Save(Fixture().Case), "An overcapacity save should be checked asynchronously"); Settle(store);
                Check(store.Error != null && store.Items.Count == 64 && File.ReadAllText(path) == before,
                    "An overcapacity library save replaced durable data");
                var bad = PullValidationCases.Copy(seed); bad.Id = "../../unrelated";
                Check(!store.Save(bad) && !store.Delete(bad.Id), "A case identity must never become an arbitrary path");
                bad = PullValidationCases.Copy(seed);
                bad.Expected = Enumerable.Range(0, 1025).Select(i => new PullExpectedAssignment {
                    RuleId = "rule", AnchorActionId = 100, Occurrence = i + 1 }).ToList();
                Check(!store.Save(bad), "Unbounded expected assignments accepted");
            }
            File.WriteAllText(path, new string(' ', 4 * 1024 * 1024 + 1));
            using (var oversized = new PullValidationCaseStore(directory))
            { Settle(oversized); Check(!oversized.WritesEnabled && oversized.Error != null, "Oversized library was writable"); }
            var malformed = JObject.FromObject(new { Version = 1, Cases = new[] { seed } });
            malformed["Cases"]![0]!["CreatedUtc"] = "invalid";
            File.WriteAllText(path, malformed.ToString());
            using (var invalid = new PullValidationCaseStore(directory))
            { Settle(invalid); Check(!invalid.WritesEnabled && invalid.Error != null, "Malformed case metadata was writable"); }
            var omittedBranch = JObject.FromObject(new { Version = 1, Cases = new[] { seed } });
            ((JObject)omittedBranch["Cases"]![0]!["Expected"]![0]!).Remove("BranchIndex");
            File.WriteAllText(path, omittedBranch.ToString());
            using (var invalid = new PullValidationCaseStore(directory))
            { Settle(invalid); Check(!invalid.WritesEnabled && invalid.Error != null, "A missing expected branch was silently accepted as reviewed no-assignment"); }
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void Disposal()
    {
        var directory = DirectoryForTest();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var ended = new ManualResetEventSlim();
        try
        {
            using var store = new PullValidationCaseStore(directory, (path, text) => {
                entered.Set(); Check(release.Wait(10000), "Blocked write timed out");
                AtomicFile.WriteAllText(path, text); ended.Set();
            });
            Settle(store); var first = Fixture().Case; Check(store.Save(first), "Initial save rejected");
            Check(entered.Wait(10000), "Write did not begin");
            Check(store.Save(Fixture().Case), "Queued save rejected");
            store.Dispose();
            Check(!store.Busy && !store.WritesEnabled && store.Items.Count == 0, "Disposal must not wait or publish an unfinished write");
            release.Set(); Check(ended.Wait(10000), "Active atomic write did not finish");
            store.Poll(); Check(store.Items.Count == 0, "Late write completion published after disposal");
            using var fresh = new PullValidationCaseStore(directory); Settle(fresh);
            Check(fresh.Items.Count == 1 && fresh.Items[0].Id == first.Id, "Disposal must abandon queued writes while preserving a started atomic replacement");
        }
        finally { release.Set(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
