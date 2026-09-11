using System;
using System.Collections.Concurrent;
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
    private readonly HashSet<string> deletedBeforeLoad = new();
    // Owned by loading and the serialized disk queue, never by the framework/UI thread.
    private readonly List<string> persisted = new();
    private readonly Dictionary<string, long> persistedOrder = new();
    private readonly Dictionary<string, string> storageErrors = new();
    private volatile string storageError = string.Empty;
    private readonly ConcurrentQueue<Completion> completions = new();
    // Framework-owned tickets prevent late results from resurrecting deleted/replaced recordings.
    private readonly Dictionary<string, long> pending = new();
    private readonly Dictionary<string, long> visibleOrder = new();
    private long nextOrder;
    private long pullGeneration;
    private readonly List<(PlanDocument Plan, long Pull, StrategyMergeSession.PendingCommit Commit)> planSaves = new();
    private sealed record Completion(string Id, long Order, long Pull, ReplayAttempt? Attempt,
        StrategyMergeSession? Session, StrategyMergeSession.Prepared? Prepared, string Error, bool SaveFailed = false);
    private readonly ArenaTracker tracker = new();
    private readonly Stopwatch clock = new();
    private float clockOffset;
    private float RecordingTime => clockOffset + (float)clock.Elapsed.TotalSeconds;
    private readonly Task<List<ReplayAttempt>> loading;
    private Task writes;
    private ReplayBuffer? buffer;
    private StrategyMergeSession? strategySession;
    private LocalEvidenceCapture capture = new();
    private float nextSample;
    private bool loaded;
    private bool disposed;
    private int retention;
    private volatile string status = string.Empty;

    public IReadOnlyList<ReplayAttempt> Attempts => attempts;
    /// <summary>Changes whenever retained evidence or its seat/calibration metadata changes.</summary>
    public long EvidenceRevision { get; private set; }
    public bool Recording => buffer != null;
    public string Status { get { var error = storageError; return error.Length > 0 ? error : status; } }

    public ReplayStore()
    {
        directory = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "replays");
        retention = Math.Clamp(Plugin.Config.ReplayRetention, 1, 30);
        var retain = retention;
        loading = Task.Run(() => Load(retain));
        writes = loading;
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
        buffer.Attempt.StatusObservations.Add(new StatusObservation { Time = RecordingTime,
            StatusId = observation.StatusId, Duration = observation.Duration, Parameter = observation.Parameter, SourceId = observation.SourceId,
            Removed = observation.Removed, Baseline = observation.Baseline,
            ParameterKnown = observation.ParameterKnown, DurationKnown = observation.DurationKnown });
    }

    private void RecordDecision(AdaptiveDecision decision)
    {
        if (buffer == null || buffer.Attempt.AdaptiveDecisions.Count >= 1024) return;
        buffer.Attempt.AdaptiveDecisions.Add(new AdaptiveDecision { Time = RecordingTime,
            AnchorActionId = decision.AnchorActionId, Occurrence = decision.Occurrence,
            Mechanic = decision.Mechanic, SlideId = decision.SlideId, Reason = decision.Reason,
            RuleId = decision.RuleId, BranchIndex = decision.BranchIndex, Conflict = decision.Conflict,
            Applied = decision.Applied, Navigation = decision.Navigation });
    }

    private void Begin()
    {
        if (buffer != null) Finish("Interrupted");
        pullGeneration++;
        var plan = Plugin.Plans.Active;
        if (!Plugin.Config.ReplayEnabled || plan == null || plan.Slides.Count == 0) return;
        // Account for earlier combat-start subscribers and our own snapshot work. Casts already
        // use the encounter's pull origin; samples and the final duration must use it too.
        clock.Restart();
        clockOffset = Plugin.Encounter.CombatElapsed;
        try
        {
            capture = new LocalEvidenceCapture();
            buffer = new ReplayBuffer(plan, Plugin.Roster.ResolveLocalSlot(plan), DateTime.UtcNow);
            strategySession = new StrategyMergeSession(plan);
            buffer.Attempt.TerritoryId = Plugin.ClientState.TerritoryType;
            nextSample = 0;
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
        Plugin.Plans.Poll();
        PublishPlanSaves();
        if (!loaded && loading.IsCompleted)
        {
            loaded = true;
            if (loading.IsCompletedSuccessfully)
            {
                // Imports already added during loading stay first, just as later imports do.
                // Load orders the saved subset identically for the UI and durable retention.
                attempts.AddRange(loading.Result.Where(a => !deletedBeforeLoad.Contains(a.Id) && attempts.All(current => current.Id != a.Id)));
                for (var i = 0; i < loading.Result.Count; i++)
                    if (!deletedBeforeLoad.Contains(loading.Result[i].Id)) visibleOrder.TryAdd(loading.Result[i].Id, -i - 1L);
                EvidenceRevision++;
            }
            deletedBeforeLoad.Clear();
            Trim();
        }
        if (loaded) Trim();
        PublishCompletions();
        PublishPlanSaves();
        if (buffer == null) return;
        if (!Plugin.Config.ReplayEnabled) { Finish("Recording stopped"); return; }
        var time = RecordingTime;
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
        buffer.AddCast(RecordedCast.FromLive(cast.ActionId, cast.Occurrence, cast.CombatTime, cast.TotalCastTime, cast.Context));
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
                Label = Plugin.Actions.Get(cast.ActionId)?.Name ?? "Cast #" + cast.ActionId,
                Time = cast.CombatTime, ExpectedResolve = cast.CombatTime + cast.TotalCastTime });
    }

    private void End() => Finish(Plugin.Encounter.LastPullWasWipe ? "Wipe" : "Combat ended");
    private void Wipe() => Finish("Wipe");
    private void TerritoryChanged(uint territory) => Finish("Zone changed");

    private void Finish(string reason)
    {
        var completed = buffer?.Finish(reason, RecordingTime);
        var session = strategySession;
        strategySession = null;
        buffer = null;
        clock.Stop();
        if (completed == null) return;
        var order = ++nextOrder;
        pending[completed.Id] = order;
        var pull = pullGeneration;
        var keep = Math.Clamp(Plugin.Config.ReplayRetention, 1, 30);
        var merge = !disposed && reason is "Wipe" or "Combat ended";
        status = "Processing the completed recording…";
        // No UI or capture code retains this attempt. The worker owns it until publication.
        Queue(() => ProcessFinished(completed, merge ? session : null, order, pull, keep));
    }

    private void ProcessFinished(ReplayAttempt completed, StrategyMergeSession? session, long order, long pull, int keep)
    {
        StrategyMergeSession.Prepared? prepared = null;
        var error = string.Empty;
        var saveFailed = false;
        try
        {
            completed.Mechanics.RemoveAll(m => m.Time > completed.Duration);
            completed.StatusObservations.RemoveAll(s => s.Time > completed.Duration);
            completed.Evidence.Statuses.RemoveAll(s => s.Time > completed.Duration);
            completed.Evidence.Positions.RemoveAll(s => s.Time > completed.Duration);
            completed.AdaptiveDecisions.RemoveAll(d => d.Time > completed.Duration);
            if (!ReplayValidation.IsValid(completed)) throw new IOException("Replay evidence failed validation.");
        }
        catch (Exception ex)
        {
            completions.Enqueue(new(completed.Id, order, pull, null, null, null, "Replay could not be processed: " + ex.Message));
            return;
        }
        try
        {
            if (session != null)
            {
                prepared = session.Prepare(completed);
                if (prepared.Result.Accepted) StrategyMergeSession.LinkReplay(prepared.Plan, completed);
            }
        }
        catch (Exception ex) { error = "The recording is available, but strategy analysis failed: " + ex.Message; }
        try
        {
            // Link first, serialize once, and persist before exposing the mutable replay to the UI.
            var json = Serialize(completed);
            try { Persist(completed.Id, json, keep, promote: true, order); }
            catch { saveFailed = true; } // Persist separately tracks disk failures until recovery.
        }
        catch (Exception ex) { error = "Replay could not be serialized: " + ex.Message; saveFailed = true; }
        completions.Enqueue(new(completed.Id, order, pull, completed, session, prepared, error, saveFailed));
    }

    private void PublishCompletions()
    {
        while (completions.TryDequeue(out var item))
        {
            if (!pending.TryGetValue(item.Id, out var order) || order != item.Order) continue;
            pending.Remove(item.Id);
            if (item.Attempt != null) AddVisible(item.Attempt, item.Order);
            if (item.Error.Length > 0) { status = item.Error; continue; }
            if (item.SaveFailed) { status = "Recording is available for review."; continue; }
            status = "Recording saved.";
            // A new pull must use the strategy it began with, even if an older analysis just finished.
            if (buffer != null || item.Pull != pullGeneration || item.Session == null || item.Prepared == null) continue;
            var plan = Plugin.Plans.Active;
            if (plan == null) continue;
            try
            {
                var commit = item.Session.CommitAsync(plan, item.Prepared, Plugin.Plans.RequestSave);
                planSaves.Add((plan, item.Pull, commit));
                status = commit.Ticket == null ? commit.Result.Summary : "Recording saved. Saving its strategy update…";
            }
            catch (Exception ex) { status = "The recording was saved, but its strategy update failed: " + ex.Message; }
        }
    }

    private void PublishPlanSaves()
    {
        for (var i = planSaves.Count - 1; i >= 0; i--)
        {
            var item = planSaves[i];
            var allowRollback = buffer == null && item.Pull == pullGeneration && ReferenceEquals(Plugin.Plans.Active, item.Plan) &&
                (item.Commit.Ticket == null || Plugin.Plans.GetSaveState(item.Plan.Id).RequestedRevision == item.Commit.Ticket.Revision);
            if (!item.Commit.TryComplete(allowRollback, out var result)) continue;
            if (item.Pull == pullGeneration) status = result.Summary;
            planSaves.RemoveAt(i);
        }
    }

    private void AddVisible(ReplayAttempt attempt, long order)
    {
        attempts.RemoveAll(a => a.Id == attempt.Id);
        visibleOrder[attempt.Id] = order;
        var index = attempts.FindIndex(a => !visibleOrder.TryGetValue(a.Id, out var current) || current < order);
        attempts.Insert(index < 0 ? attempts.Count : index, attempt);
        EvidenceRevision++;
        if (loaded) Trim();
    }

    public void AddImported(ReplayAttempt attempt)
    {
        if (!ReplayValidation.IsValid(attempt)) throw new IOException("Replay evidence failed validation.");
        var order = ++nextOrder;
        SaveEvidence(attempt, promote: true, order);
        pending.Remove(attempt.Id);
        AddVisible(attempt, order);
    }

    public void SaveEvidence(ReplayAttempt attempt) => SaveEvidence(attempt, promote: false,
        visibleOrder.TryGetValue(attempt.Id, out var order) ? order : 0);

    private void SaveEvidence(ReplayAttempt attempt, bool promote, long order)
    {
        if (!ReplayValidation.IsValid(attempt)) throw new IOException("Replay evidence failed validation.");
        // Snapshot now, so edits made while a queued write runs cannot tear the saved file.
        var json = Serialize(attempt);
        var id = attempt.Id;
        var keep = Math.Clamp(Plugin.Config.ReplayRetention, 1, 30);
        EvidenceRevision++;
        Queue(() => Persist(id, json, keep, promote, order));
    }

    private static string Serialize(ReplayAttempt attempt)
    {
        var json = JsonConvert.SerializeObject(attempt, PlanJson.Compact());
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
            throw new IOException("This replay exceeds the local file size limit.");
        return json;
    }

    private void Persist(string id, string json, int keep, bool promote, long order) => StorageOperation(id, () =>
    {
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(PathFor(id), json);
        if (promote || !persisted.Contains(id))
        {
            persisted.Remove(id);
            persistedOrder[id] = order;
            var index = persisted.FindIndex(current => persistedOrder[current] < order);
            persisted.Insert(index < 0 ? persisted.Count : index, id);
        }
        TrimPersisted(keep);
    });

    // Worker-owned failures stay visible independently of late successful analysis messages.
    private void StorageOperation(string key, Action operation)
    {
        try
        {
            operation();
            storageErrors.Remove(key);
        }
        catch (Exception ex)
        {
            storageErrors[key] = "Replay storage needs attention: " + ex.Message;
            throw;
        }
        finally
        {
            storageError = storageErrors.Count == 0 ? string.Empty : storageErrors.Values.First() +
                (storageErrors.Count > 1 ? $" ({storageErrors.Count} unresolved storage operations.)" : string.Empty);
        }
    }

    private void Trim()
    {
        var keep = Math.Clamp(Plugin.Config.ReplayRetention, 1, 30);
        while (attempts.Count > keep)
        {
            visibleOrder.Remove(attempts[^1].Id);
            attempts.RemoveAt(attempts.Count - 1);
            EvidenceRevision++;
        }
        if (keep == retention) return;
        retention = keep;
        Queue(() => StorageOperation("retention", () => TrimPersisted(keep)));
    }

    private void TrimPersisted(int keep)
    {
        // Only a successfully saved replacement can displace durable evidence. A failed save
        // may leave an older fallback on disk even though the UI has reached its retention cap.
        while (persisted.Count > keep)
        {
            var id = persisted[^1];
            File.Delete(PathFor(id));
            persisted.RemoveAt(persisted.Count - 1);
            persistedOrder.Remove(id);
        }
    }

    public void Delete(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return;
        if (!loaded) deletedBeforeLoad.Add(id);
        pending.Remove(id);
        visibleOrder.Remove(id);
        attempts.RemoveAll(a => a.Id == id);
        EvidenceRevision++;
        Queue(() => StorageOperation(id, () =>
        {
            var path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
            persisted.Remove(id);
            persistedOrder.Remove(id);
        }));
    }

    public void Clear()
    {
        // Queue the clear behind loading so a late load cannot resurrect files deleted in the
        // UI. Update ignores the load result once loaded is true.
        loaded = true;
        deletedBeforeLoad.Clear();
        pending.Clear();
        visibleOrder.Clear();
        attempts.Clear();
        EvidenceRevision++;
        Queue(() => StorageOperation("library", () =>
        {
            if (Directory.Exists(directory))
                foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                    if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)) File.Delete(path);
            persisted.Clear();
            persistedOrder.Clear();
            storageErrors.Clear();
            status = "Replay library cleared.";
        }));
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
            catch (Exception ex)
            {
                // Tracked disk failures must not also survive in ordinary status after recovery.
                if (storageError.Length == 0) status = "Replay processing needs attention: " + ex.Message;
            }
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
            foreach (var file in files)
            {
                try
                {
                    if (file.Length > MaxFileBytes) throw new IOException("Replay file exceeds size limit.");
                    var replay = JsonConvert.DeserializeObject<ReplayAttempt>(File.ReadAllText(file.FullName), PlanJson.Compact());
                    if (replay == null || replay.Id != Path.GetFileNameWithoutExtension(file.Name) || !ReplayValidation.IsValid(replay))
                        throw new IOException("Replay is incomplete or uses an unsupported format.");
                    if (result.Count < retention)
                    {
                        result.Add(replay);
                    }
                    else file.Delete();
                }
                catch (Exception ex) { status = "Some saved replays could not be loaded: " + ex.Message; }
            }
            // Invalid or unsupported files remain available for recovery. They do not consume
            // retention slots, and only validated older replays can be deleted automatically.
        }
        catch (Exception ex) { status = "Replay storage could not be read: " + ex.Message; }
        result.Sort((a, b) => b.StartedUtc.CompareTo(a.StartedUtc));
        persisted.AddRange(result.Select(a => a.Id));
        for (var i = 0; i < result.Count; i++) persistedOrder[result[i].Id] = -i - 1L;
        return result;
    }

    public void Dispose() => Dispose(saveRecording: true);

    internal void Dispose(bool saveRecording)
    {
        if (disposed) return;
        disposed = true;
        Plugin.Framework.Update -= Update;
        Plugin.Adaptive.Observed -= ObserveStatus;
        Plugin.Adaptive.Decided -= RecordDecision;
        Plugin.Encounter.CombatStarted -= Begin;
        Plugin.Encounter.CombatEnded -= End;
        Plugin.Encounter.Wiped -= Wipe;
        Plugin.Encounter.CastStarted -= Cast;
        Plugin.ClientState.TerritoryChanged -= TerritoryChanged;
        if (saveRecording) Finish("Plugin unloaded");
        else { buffer = null; strategySession = null; clock.Stop(); }
        // Let bounded pending writes complete before the plugin's load context is released.
        Task.WaitAll(new[] { loading, writes }, TimeSpan.FromSeconds(3));
    }
}
