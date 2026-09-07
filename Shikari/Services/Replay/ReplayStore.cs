using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Shikari.Model;
using Shikari.Services.Live;
using Shikari.Services.Storage;

namespace Shikari.Services.Replay;

/// <summary>Framework-owned capture; disk work is serialized off the drawing/game thread.</summary>
public sealed class ReplayStore : IDisposable
{
    private const int MaxFileBytes = 32 * 1024 * 1024;
    private readonly string directory;
    private readonly List<ReplayAttempt> attempts = new();
    private readonly ArenaTracker tracker = new();
    private readonly Stopwatch clock = new();
    private readonly Task<List<ReplayAttempt>> loading;
    private Task writes = Task.CompletedTask;
    private ReplayBuffer? buffer;
    private LocalEvidenceCapture capture = new();
    private float nextSample;
    private bool loaded;
    private bool disposed;
    private volatile string status = string.Empty;

    public IReadOnlyList<ReplayAttempt> Attempts => attempts;
    public bool Recording => buffer != null;
    public string Status => status;

    public ReplayStore()
    {
        directory = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "replays");
        var retain = Math.Clamp(Plugin.Config.ReplayRetention, 1, 30);
        loading = Task.Run(() => Load(retain));
        Plugin.Encounter.CombatStarted += Begin;
        Plugin.Encounter.CombatEnded += End;
        Plugin.Encounter.Wiped += Wipe;
        Plugin.Encounter.CastStarted += Cast;
        Plugin.ClientState.TerritoryChanged += TerritoryChanged;
        Plugin.Framework.Update += Update;
        Plugin.Adaptive.Observed += ObserveStatus;
        Plugin.Adaptive.Decided += RecordDecision;
    }

    private void ObserveStatus(StatusObservation observation)
    {
        if (buffer == null || buffer.Attempt.StatusObservations.Count >= 4096) return;
        buffer.Attempt.StatusObservations.Add(new StatusObservation { Time = (float)clock.Elapsed.TotalSeconds,
            StatusId = observation.StatusId, Duration = observation.Duration, Parameter = observation.Parameter, SourceId = observation.SourceId,
            Removed = observation.Removed, Baseline = observation.Baseline,
            ParameterKnown = observation.ParameterKnown, DurationKnown = observation.DurationKnown });
    }

    private void RecordDecision(AdaptiveDecision decision)
    {
        if (buffer == null || buffer.Attempt.AdaptiveDecisions.Count >= 1024) return;
        buffer.Attempt.AdaptiveDecisions.Add(new AdaptiveDecision { Time = (float)clock.Elapsed.TotalSeconds,
            AnchorActionId = decision.AnchorActionId, Occurrence = decision.Occurrence,
            Mechanic = decision.Mechanic, SlideId = decision.SlideId, Reason = decision.Reason,
            Applied = decision.Applied, Navigation = decision.Navigation });
    }

    private void Begin()
    {
        if (buffer != null) Finish("Interrupted");
        var plan = Plugin.Plans.Active;
        if (!Plugin.Config.ReplayEnabled || plan == null || plan.Slides.Count == 0) return;
        try
        {
            capture = new LocalEvidenceCapture();
            buffer = new ReplayBuffer(plan, Plugin.Roster.ResolveLocalSlot(plan), DateTime.UtcNow);
            buffer.Attempt.TerritoryId = Plugin.ClientState.TerritoryType;
            nextSample = 0;
            clock.Restart();
            foreach (var entry in buffer.Attempt.Plan.Timeline.Where(e => e.Enabled && e.Trigger == TriggerKind.CombatTime))
                buffer.AddMechanic(new ReplayMechanic { EntryId = entry.Id, SlideId = entry.SlideId,
                    Label = entry.Label, Time = Math.Max(0, entry.TimeSeconds), ExpectedResolve = Math.Max(0, entry.TimeSeconds) });
        }
        catch (Exception ex)
        {
            buffer = null;
            status = "Recording could not start: " + ex.Message;
            Plugin.Log.Warning(ex, "Could not begin mechanic replay.");
        }
    }

    private void Update(IFramework framework)
    {
        if (disposed) return;
        if (!loaded && loading.IsCompleted)
        {
            loaded = true;
            if (loading.IsCompletedSuccessfully)
            {
                attempts.AddRange(loading.Result.Where(a => attempts.All(current => current.Id != a.Id)));
                attempts.Sort((a, b) => b.StartedUtc.CompareTo(a.StartedUtc));
            }
            Trim();
        }
        if (loaded) Trim();
        if (buffer == null) return;
        if (!Plugin.Config.ReplayEnabled) { Finish("Recording stopped"); return; }
        var time = (float)clock.Elapsed.TotalSeconds;
        if (time > ReplayBuffer.MaxDuration) { Finish("30 minute limit"); return; }
        if (time < nextSample) return;
        nextSample = time + ReplayBuffer.SampleInterval;
        try
        {
            capture.Capture(buffer.Attempt, time);
            var plan = buffer.Attempt.Plan;
            // Use the recorded plan even if the editor is changed mid-pull. A different active
            // plan has no meaningful slide correspondence and must create a gap instead.
            var samePlan = Plugin.Plans.Active?.Id == plan.Id;
            var activeSlides = Plugin.Plans.Active?.Slides;
            var activeIndex = Plugin.Main.SlideIndex;
            var slideId = samePlan && activeSlides != null && activeIndex >= 0 && activeIndex < activeSlides.Count
                ? activeSlides[activeIndex].Id : string.Empty;
            var slide = plan.FindSlide(slideId);
            var players = slide == null ? Array.Empty<ArenaTracker.LivePlayer>() : tracker.Read(plan, slide, buffer.Attempt.LocalSlot);
            var frame = new ReplayFrame { Time = time, SlideId = slideId,
                Valid = slide != null && tracker.Aligned, BoardPerYalm = tracker.BoardPerYalm };
            foreach (var player in players)
            {
                frame.Players.Add(new ReplayPlayer { Name = player.Name, JobId = player.JobId,
                    SlotIndex = player.IsLocal ? buffer.Attempt.LocalSlot : player.SlotIndex,
                    Board = player.Board, IsLocal = player.IsLocal });
            }
            // If an override occupied another resolved seat, that other player is ambiguous.
            foreach (var duplicate in frame.Players.Where(p => p.SlotIndex >= 0).GroupBy(p => p.SlotIndex).Where(g => g.Count() > 1))
                foreach (var player in duplicate.Where(p => !p.IsLocal)) player.SlotIndex = -1;
            buffer.TryAdd(frame);
        }
        catch (Exception ex)
        {
            capture.Invalidate(buffer.Attempt, time);
            buffer.TryAdd(new ReplayFrame { Time = time });
            status = "A recording sample was unavailable: " + ex.Message;
        }
    }

    private void Cast(CastEvent cast)
    {
        if (buffer == null) return;
        var matched = false;
        foreach (var entry in buffer.Attempt.Plan.Timeline.Where(e => e.Enabled && e.CastActionId == cast.ActionId &&
                     e.Trigger is TriggerKind.BossCast or TriggerKind.AfterCast or TriggerKind.Predicted &&
                     (e.Occurrence <= 0 || e.Occurrence == cast.Occurrence)))
        {
            matched = true;
            var expected = cast.CombatTime + (entry.Trigger == TriggerKind.AfterCast ? entry.OffsetSeconds : cast.TotalCastTime);
            buffer.AddMechanic(new ReplayMechanic { EntryId = entry.Id, SlideId = entry.SlideId, Label = entry.Label,
                ActionId = cast.ActionId, Occurrence = cast.Occurrence, Time = cast.CombatTime,
                ExpectedResolve = expected });
        }
        if (!matched)
            buffer.AddMechanic(new ReplayMechanic { ActionId = cast.ActionId, Occurrence = cast.Occurrence,
                Label = "Cast #" + cast.ActionId, Time = cast.CombatTime, ExpectedResolve = cast.CombatTime + cast.TotalCastTime });
    }

    private void End() => Finish(Plugin.Encounter.LastPullWasWipe ? "Wipe" : "Combat ended");
    private void Wipe() => Finish("Wipe");
    private void TerritoryChanged(uint territory) => Finish("Zone changed");

    private void Finish(string reason)
    {
        var completed = buffer?.Finish(reason, (float)clock.Elapsed.TotalSeconds);
        buffer = null;
        clock.Stop();
        if (completed == null) return;
        completed.Mechanics.RemoveAll(m => m.Time > completed.Duration);
        completed.StatusObservations.RemoveAll(s => s.Time > completed.Duration);
        completed.Evidence.Statuses.RemoveAll(s => s.Time > completed.Duration);
        completed.Evidence.Positions.RemoveAll(s => s.Time > completed.Duration);
        completed.AdaptiveDecisions.RemoveAll(d => d.Time > completed.Duration);
        try { AddImported(completed); }
        catch (Exception ex) { status = "Replay could not be saved: " + ex.Message; Plugin.Log.Warning(ex, "Replay save failed."); }
    }

    public void AddImported(ReplayAttempt attempt)
    {
        if (!ReplayValidation.IsValid(attempt)) throw new IOException("Replay evidence failed validation.");
        SaveEvidence(attempt);
        attempts.RemoveAll(a => a.Id == attempt.Id);
        attempts.Insert(0, attempt);
        if (loaded) Trim();
    }

    public void SaveEvidence(ReplayAttempt attempt)
    {
        if (!ReplayValidation.IsValid(attempt)) throw new IOException("Replay evidence failed validation.");
        // Snapshot now, so edits made while a queued write runs cannot tear the saved file.
        var json = JsonConvert.SerializeObject(attempt, PlanJson.Compact());
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
            throw new IOException("This replay exceeds the local file size limit.");
        Queue(() =>
        {
            Directory.CreateDirectory(directory);
            AtomicFile.WriteAllText(PathFor(attempt.Id), json);
        });
    }

    private void Trim()
    {
        var keep = Math.Clamp(Plugin.Config.ReplayRetention, 1, 30);
        while (attempts.Count > keep) Delete(attempts[^1].Id);
    }

    public void Delete(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return;
        attempts.RemoveAll(a => a.Id == id);
        Queue(() => { var path = PathFor(id); if (File.Exists(path)) File.Delete(path); });
    }

    public void Clear()
    {
        // Loading has a finite bound. Queue the clear behind it so a late load cannot resurrect
        // files deleted in the UI. Update ignores the load result once loaded is true.
        loaded = true;
        attempts.Clear();
        Queue(() =>
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)) File.Delete(path);
        });
    }

    private string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new IOException("Invalid replay identity.");
        return Path.Combine(directory, id + ".json");
    }

    private void Queue(Action action)
    {
        // Every caller runs on the framework/UI thread; continuations serialize all disk writes.
        writes = writes.ContinueWith(_ =>
        {
            try { action(); }
            catch (Exception ex) { status = "Replay storage needs attention: " + ex.Message; }
        }, TaskScheduler.Default);
    }

    private List<ReplayAttempt> Load(int retention)
    {
        var result = new List<ReplayAttempt>();
        try
        {
            if (!Directory.Exists(directory)) return result;
            var files = new DirectoryInfo(directory).EnumerateFiles("*.json")
                .Where(f => Guid.TryParseExact(Path.GetFileNameWithoutExtension(f.Name), "N", out _))
                .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            foreach (var file in files.Take(retention))
            {
                try
                {
                    if (file.Length > MaxFileBytes) throw new IOException("Replay file exceeds size limit.");
                    var replay = JsonConvert.DeserializeObject<ReplayAttempt>(File.ReadAllText(file.FullName), PlanJson.Compact());
                    if (replay == null || replay.Id != Path.GetFileNameWithoutExtension(file.Name) || !ReplayValidation.IsValid(replay))
                        throw new IOException("Replay is incomplete or uses an unsupported format.");
                    result.Add(replay);
                }
                catch (Exception ex) { status = "Some saved replays could not be loaded: " + ex.Message; }
            }
            // Only known replay files are eligible; unrelated files are never touched.
            foreach (var file in files.Skip(retention)) file.Delete();
        }
        catch (Exception ex) { status = "Replay storage could not be read: " + ex.Message; }
        return result;
    }

    public void Dispose()
    {
        disposed = true;
        Plugin.Framework.Update -= Update;
        Plugin.Adaptive.Observed -= ObserveStatus;
        Plugin.Adaptive.Decided -= RecordDecision;
        Plugin.Encounter.CombatStarted -= Begin;
        Plugin.Encounter.CombatEnded -= End;
        Plugin.Encounter.Wiped -= Wipe;
        Plugin.Encounter.CastStarted -= Cast;
        Plugin.ClientState.TerritoryChanged -= TerritoryChanged;
        Finish("Plugin unloaded");
        // Let bounded pending writes complete before the plugin's load context is released.
        Task.WaitAll(new[] { loading, writes }, TimeSpan.FromSeconds(3));
    }
}
