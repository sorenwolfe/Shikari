using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Shikari.Services.Storage;

namespace Shikari.Services.Replay;

/// <summary>Owner-thread facade for an ordered, atomically persisted local reviewed-case library.</summary>
public sealed class PullValidationCaseStore : IDisposable
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private sealed class Library
    {
        [JsonProperty(Required = Required.Always)] public int Version { get; set; } = 1;
        [JsonProperty(Required = Required.Always)] public List<PullValidationCase> Cases { get; set; } = new();
    }
    private sealed record Operation(PullValidationCase? Save, string DeleteId);
    private sealed record Output(List<PullValidationCase> Durable, IReadOnlyList<PullValidationCase> Published);
    private sealed class RequiredCaseMembers : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization serialization)
        {
            var property = base.CreateProperty(member, serialization);
            if (member.DeclaringType == typeof(PullValidationCase) || member.DeclaringType == typeof(PullExpectedAssignment))
                property.Required = Required.Always;
            return property;
        }
    }
    private readonly string path;
    private readonly Action<string, string> write;
    private readonly Queue<Operation> pending = new();
    private List<PullValidationCase> durable = new();
    private Task<Output>? work;
    private bool loading = true;
    private bool writeBlocked;
    private bool disposed;
    public IReadOnlyList<PullValidationCase> Items { get; private set; } = Array.Empty<PullValidationCase>();
    public bool Busy => work != null || pending.Count != 0;
    public string? Error { get; private set; }
    public bool WritesEnabled => !disposed && !loading && !writeBlocked;

    public PullValidationCaseStore(string directory) : this(directory, AtomicFile.WriteAllText) { }
    internal PullValidationCaseStore(string directory, Action<string, string> write)
    {
        path = Path.Combine(Path.GetFullPath(directory), "pull-validation-cases.json");
        this.write = write;
        work = Task.Run(() => Publish(Read()));
    }

    /// <summary>Accepts a detached write; Items changes only after Poll observes durable success.</summary>
    public bool Save(PullValidationCase reviewed)
    {
        if (!WritesEnabled) return false;
        try
        {
            var copy = PullValidationCases.Copy(reviewed);
            var newest = pending.Where(o => o.Save?.Id == copy.Id).Select(o => o.Save!)
                .Concat(durable.Where(c => c.Id == copy.Id)).OrderByDescending(c => c.UpdatedUtc).FirstOrDefault();
            // The in-flight case is tracked too, so a stale caller cannot enqueue an older replacement.
            if (active?.Save?.Id == copy.Id && (newest == null || active.Save.UpdatedUtc > newest.UpdatedUtc)) newest = active.Save;
            if (newest != null && (copy.UpdatedUtc < newest.UpdatedUtc || copy.CreatedUtc != newest.CreatedUtc ||
                (copy.UpdatedUtc == newest.UpdatedUtc && !SameRevision(copy, newest))))
                throw new ArgumentException("This reviewed case is older than the latest saved or queued revision.");
            if (pending.Count >= PullValidationCases.MaxCases)
                throw new ArgumentException("The reviewed-case save queue is full. Wait for the current save to finish.");
            pending.Enqueue(new(copy, "")); StartNext(); return true;
        }
        catch (ArgumentException ex) { Error = ex.Message; return false; }
    }

    public bool Delete(string id)
    {
        if (!WritesEnabled) return false;
        if (!PullValidationCases.IsId(id) || pending.Count >= PullValidationCases.MaxCases)
        { Error = "The reviewed-case deletion is invalid or the save queue is full."; return false; }
        pending.Enqueue(new(null, id)); StartNext(); return true;
    }

    private Operation? active;
    private void StartNext()
    {
        if (!WritesEnabled || work != null || pending.Count == 0) return;
        var operation = active = pending.Dequeue();
        var before = durable; // Never exposed to UI or mutated by a worker.
        work = Task.Run(() =>
        {
            var next = before.Where(c => c.Id != (operation.Save?.Id ?? operation.DeleteId)).ToList();
            if (operation.Save != null) next.Add(operation.Save);
            if (next.Count > PullValidationCases.MaxCases)
                throw new InvalidOperationException("The local library holds up to 64 reviewed cases. Delete an older case first.");
            var json = JsonConvert.SerializeObject(new Library { Cases = next }, Settings());
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
                throw new InvalidOperationException("The reviewed-case library exceeds its 4 MiB limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            write(path, json);
            return Publish(next);
        });
    }

    public void Poll()
    {
        if (disposed || work == null || !work.IsCompleted) return;
        var completed = work; work = null; active = null;
        try
        {
            var output = completed.GetAwaiter().GetResult();
            durable = output.Durable; Items = output.Published; Error = null;
        }
        catch (Exception ex)
        {
            if (loading) writeBlocked = true;
            Error = (loading ? "Reviewed cases could not be loaded; this library will not be overwritten. " :
                "Reviewed cases were not saved. The previous library is unchanged. ") + ex.Message;
        }
        loading = false;
        StartNext();
    }

    private List<PullValidationCase> Read()
    {
        if (!File.Exists(path)) return new();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxBytes) throw new InvalidDataException("The library exceeds 4 MiB.");
        using var text = new StreamReader(file, new UTF8Encoding(false, true));
        using var reader = new JsonTextReader(text) { MaxDepth = 32, DateTimeZoneHandling = DateTimeZoneHandling.Utc };
        var root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new InvalidDataException("The library contains trailing content.");
        if (root["Version"]?.Type != JTokenType.Integer || (int?)root["Version"] != 1 ||
            root["Cases"] is not JArray cases || cases.Count > PullValidationCases.MaxCases)
            throw new InvalidDataException("The library version or case list is unsupported.");
        var library = root.ToObject<Library>(JsonSerializer.Create(Settings())) ?? throw new InvalidDataException("The library is missing.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reviewed in library.Cases)
        {
            PullValidationCases.Validate(reviewed);
            if (!ids.Add(reviewed.Id)) throw new InvalidDataException("The library contains duplicate case identities.");
        }
        return library.Cases;
    }

    private static JsonSerializerSettings Settings() => new() {
        Formatting = Formatting.Indented, MissingMemberHandling = MissingMemberHandling.Error,
        TypeNameHandling = TypeNameHandling.None, DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        MaxDepth = 32, ContractResolver = new RequiredCaseMembers(),
    };
    private static Output Publish(List<PullValidationCase> cases) => new(cases,
        Array.AsReadOnly(cases.Select(PullValidationCases.Copy).ToArray()));

    private static bool SameRevision(PullValidationCase left, PullValidationCase right) =>
        left.Id == right.Id && left.Name == right.Name && left.ReviewNote == right.ReviewNote &&
        left.AttemptId == right.AttemptId && left.PlanId == right.PlanId && left.ActorId == right.ActorId &&
        left.TerritoryId == right.TerritoryId && left.BindingFingerprint == right.BindingFingerprint &&
        left.Expected.Select(e => (e.RuleId, e.AnchorActionId, e.Occurrence, e.BranchIndex, e.Conflict))
            .SequenceEqual(right.Expected.Select(e => (e.RuleId, e.AnchorActionId, e.Occurrence, e.BranchIndex, e.Conflict)));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; pending.Clear(); active = null;
        var abandoned = work; work = null;
        // A write already inside the atomic replacement may finish, but queued operations and
        // late publication stop here. Unloading never blocks the UI on storage.
        if (abandoned != null)
            _ = abandoned.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
