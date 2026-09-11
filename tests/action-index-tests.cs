using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using Shikari.Services;
using SheetAction = Lumina.Excel.Sheets.Action;

// Only the external sheet and logging boundary is replaced. All index construction,
// publication, lookup, ranking and cooldown filtering run the production sources.
namespace Lumina.Excel.Sheets
{
    public sealed class SheetText
    {
        private readonly string value;
        public SheetText(string value) => this.value = value;
        public string ExtractText() => value;
    }
    public readonly record struct RowRef(uint RowId);
    public sealed class ClassJob
    {
        public uint RowId { get; init; }
        public SheetText Name { get; init; } = new("");
        public SheetText Abbreviation { get; init; } = new("");
    }
    public sealed class ClassJobCategory
    {
        public uint RowId { get; init; }
        public bool PLD { get; init; }
        public bool WAR { get; init; }
        public bool CRP { get; init; }
    }
    public sealed class Action
    {
        public uint RowId { get; init; }
        public SheetText Name { get; init; } = new("");
        public RowRef ClassJob { get; init; }
        public RowRef ClassJobCategory { get; init; }
        public bool IsPvP { get; init; }
        public bool IsRoleAction { get; init; }
        public byte ClassJobLevel { get; init; }
        public ushort Recast100ms { get; init; }
        public ushort Icon { get; init; }
    }
}
namespace Shikari
{
    public static class Plugin
    {
        public static SheetSource DataManager { get; set; } = new();
        public static TestLog Log { get; set; } = new();
    }
    public sealed class TestLog
    {
        public int Errors;
        public void Information(string message, params object[] values) { }
        public void Error(Exception error, string message) => Interlocked.Increment(ref Errors);
    }
    public sealed class SheetSource
    {
        public IEnumerable<ClassJob> Jobs { get; set; } = Array.Empty<ClassJob>();
        public IEnumerable<SheetAction> Actions { get; set; } = Array.Empty<SheetAction>();
        public IEnumerable<ClassJobCategory> Categories { get; set; } = Array.Empty<ClassJobCategory>();
        public IEnumerable<T>? GetExcelSheet<T>() => (IEnumerable<T>)(object)(typeof(T) == typeof(ClassJob)
            ? Jobs : typeof(T) == typeof(SheetAction) ? Actions : Categories);
    }
}
namespace Shikari.Tests
{
    public static class ActionIndexTests
    {
        private static int checks;
        private static void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
            Interlocked.Increment(ref checks);
        }

        private sealed class Gate : IDisposable
        {
            public ManualResetEventSlim Entered { get; } = new();
            public ManualResetEventSlim Resume { get; } = new();
            public int RowsYielded { get; private set; }
            public IEnumerable<T> AfterFirst<T>(IEnumerable<T> rows)
            {
                var first = true;
                foreach (var row in rows)
                {
                    if (!first)
                    {
                        Entered.Set();
                        if (!Resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release sheet enumeration");
                    }
                    first = false;
                    RowsYielded++;
                    yield return row;
                }
            }
            public void Wait() => Check(Entered.Wait(TimeSpan.FromSeconds(10)), "Index never reached the controlled sheet pause");
            public void Dispose() { Resume.Set(); Entered.Dispose(); Resume.Dispose(); }
        }

        private static SheetSource Fixture() => new()
        {
            Jobs = new[]
            {
                new ClassJob { RowId = 19, Name = new("paladin"), Abbreviation = new("PLD") },
                new ClassJob { RowId = 21, Name = new("warrior"), Abbreviation = new("WAR") },
                new ClassJob { RowId = 8, Name = new("carpenter"), Abbreviation = new("CRP") },
            },
            Actions = new[]
            {
                Action(100, "Shield", 19, 10, 300),
                Action(101, "Shield Wall", 19, 10, 900),
                Action(102, "Divine Shield", 21, 12, 600),
                Action(103, "Strike", 19, 10, 25),
                Action(104, "Shared Guard", 0, 11, 10, level: 0, role: true),
                Action(105, "Enemy Shield", uint.MaxValue, 0, 900, level: 0),
                Action(106, "PvP Guard", 19, 10, 900, pvp: true),
                Action(0, "Invalid", 19, 10, 900),
                Action(107, "  ", 19, 10, 900),
            },
            Categories = new[]
            {
                new ClassJobCategory { RowId = 11, PLD = true, WAR = true },
                new ClassJobCategory { RowId = 10, PLD = true },
                new ClassJobCategory { RowId = 12, WAR = true },
            },
        };

        private static SheetAction Action(uint id, string name, uint job, uint category, ushort recast,
            byte level = 10, bool role = false, bool pvp = false) => new()
        {
            RowId = id, Name = new(name), ClassJob = new(job), ClassJobCategory = new(category),
            Recast100ms = recast, ClassJobLevel = level, IsRoleAction = role, IsPvP = pvp, Icon = 123,
        };

        private static void EmptyReads(ActionIndex index)
        {
            var failures = new List<string>();
            void Expect(bool value, string message) { if (!value) failures.Add(message); }
            Expect(!index.Ready, "Ready exposed unfinished construction");
            Expect(index.Jobs.Count == 0, "Jobs exposed unfinished construction");
            Expect(index.Get(100) == null, "Get exposed an unfinished action");
            Expect(index.NameOf(100) == "Action #100", "NameOf bypassed the unpublished fallback");
            Expect(index.NameOf(100, "Saved action") == "Saved action", "NameOf lost the supplied fallback");
            Expect(index.Job(19) == null, "Job exposed unfinished construction");
            Expect(index.JobAbbreviation(19) == "???", "JobAbbreviation exposed unfinished construction");
            var role = new ActionEntry { RowId = 104, CategoryId = 11 };
            Expect(!index.CanJobUse(role, 19), "CanJobUse exposed unfinished categories");
            Expect(index.CanJobUse(role, 0), "Unknown job must keep the unrestricted fallback");
            Expect(index.CanJobUse(new ActionEntry { ClassJobId = 19 }, 19), "Direct owner must remain usable without category data");
            Expect(index.CooldownCount(0) == 0 && index.CooldownCount(19) == 0, "CooldownCount exposed unfinished construction");
            Expect(index.SearchPlayerActions("", 0, false).Count == 0, "Player search exposed unfinished construction");
            Expect(index.SearchPlayerActions("", 19, true).Count == 0, "Cooldown search exposed unfinished construction");
            Expect(index.SearchAllActions("").Count == 0, "All-action search exposed unfinished construction");
            Check(failures.Count == 0, string.Join("; ", failures));
        }

        private static void PublishedReads(ActionIndex index)
        {
            Check(index.Ready && index.Jobs.Select(j => j.RowId).SequenceEqual(new uint[] { 19, 21, 8 }), "Published jobs are complete and sorted");
            Check(index.Get(100)?.Name == "Shield" && index.NameOf(100) == "Shield", "Published actions are available by ID and name");
            Check(index.Job(19)?.Name == "Paladin" && index.JobAbbreviation(19) == "PLD", "Published job lookup preserves normalisation");
            Check(index.Get(105)?.ClassJobId == 0, "Unowned action sentinel must normalise to job zero");
            Check(index.Get(0) == null && index.Get(107) == null && index.Get(999) == null, "Invalid and unknown action IDs stay absent");
            Check(index.Job(999) == null && index.JobAbbreviation(999) == "???", "Unknown job lookup keeps its fallback");
            Check(index.CanJobUse(index.Get(104)!, 19) && index.CanJobUse(index.Get(104)!, 21), "Role category includes both permitted jobs");
            Check(!index.CanJobUse(index.Get(104)!, 8), "Role category excludes crafting jobs");
            Check(index.CooldownCount(0) == 4 && index.CooldownCount(19) == 3 && index.CooldownCount(21) == 2, "Cooldown counts preserve recast, role and category filtering");
            Check(index.SearchPlayerActions("", 0, false).Count == 5 && index.SearchAllActions("").Count == 7, "PvP and boss actions stay out of the player picker");
            Check(index.SearchAllActions(" SHIELD ").Select(e => e.RowId).SequenceEqual(new uint[] { 100, 101, 105, 102 }), "Search keeps exact, prefix and substring ranking");
            Check(index.SearchPlayerActions("shield", 19, true, 1).Select(e => e.RowId).SequenceEqual(new uint[] { 100 }), "Player search applies job, cooldown and result limits");
            Check(index.SearchPlayerActions("strike", 19, false).Count == 1 && index.SearchPlayerActions("strike", 19, true).Count == 0, "Rotation actions remain available only without cooldown filtering");
            if (index.Jobs is IList<JobEntry> mutableJobs)
                Check(mutableJobs.IsReadOnly, "Published Jobs must not permit caller mutation");
        }

        private static async Task PauseDuring(string stage, bool cancel)
        {
            using var gate = new Gate();
            using var cancellation = new CancellationTokenSource();
            var source = Fixture();
            if (stage == "jobs") source.Jobs = gate.AfterFirst(source.Jobs);
            else if (stage == "actions") source.Actions = gate.AfterFirst(source.Actions);
            else source.Categories = gate.AfterFirst(source.Categories);
            Plugin.DataManager = source;
            Plugin.Log = new();
            var index = new ActionIndex();
            EmptyReads(index);
            var build = index.BuildAsync(cancellation.Token);
            try
            {
                gate.Wait();
                await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
                {
                    for (var i = 0; i < 100; i++) EmptyReads(index);
                })));
                if (cancel) cancellation.Cancel();
            }
            finally { gate.Resume.Set(); await build.WaitAsync(TimeSpan.FromSeconds(10)); }
            if (cancel)
            {
                EmptyReads(index);
                Check(gate.RowsYielded == 2, $"Cancellation must stop the {stage} sheet before requesting another row");
            }
            else PublishedReads(index);
            Check(Plugin.Log.Errors == 0, "Cancellation and normal startup should not log build failures");
        }

        private static IEnumerable<SheetAction> FailingActions()
        {
            yield return Action(100, "Unfinished replacement", 19, 10, 300);
            throw new InvalidOperationException("Controlled sheet failure");
        }

        private static async Task FailedBuildPublishesNothing()
        {
            Plugin.DataManager = Fixture();
            Plugin.DataManager.Actions = FailingActions();
            Plugin.Log = new();
            var index = new ActionIndex();
            await index.BuildAsync().WaitAsync(TimeSpan.FromSeconds(10));
            EmptyReads(index);
            Check(Plugin.Log.Errors == 1, "A genuine sheet failure should be reported once");
        }

        private static async Task RebuildKeepsPublishedSnapshot(bool cancel)
        {
            Plugin.DataManager = Fixture();
            Plugin.Log = new();
            var index = new ActionIndex();
            await index.BuildAsync().WaitAsync(TimeSpan.FromSeconds(10));
            PublishedReads(index);
            var originalJobs = index.Jobs;
            using var gate = new Gate();
            using var cancellation = new CancellationTokenSource();
            Plugin.DataManager = Fixture();
            Plugin.DataManager.Actions = gate.AfterFirst(Plugin.DataManager.Actions);
            var build = index.BuildAsync(cancellation.Token);
            try
            {
                gate.Wait();
                await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
                {
                    for (var i = 0; i < 100; i++) PublishedReads(index);
                })));
                Check(ReferenceEquals(originalJobs, index.Jobs), "Rebuild must retain the previous published jobs until completion");
                if (cancel) cancellation.Cancel();
            }
            finally { gate.Resume.Set(); await build.WaitAsync(TimeSpan.FromSeconds(10)); }
            PublishedReads(index);
            Check(ReferenceEquals(originalJobs, index.Jobs) == cancel, "Only a completed rebuild should replace the published snapshot");
            Plugin.DataManager = Fixture();
            Plugin.DataManager.Actions = FailingActions();
            await index.BuildAsync().WaitAsync(TimeSpan.FromSeconds(10));
            PublishedReads(index);
        }

        public static async Task Run()
        {
            foreach (var stage in new[] { "jobs", "actions", "categories" })
            {
                await PauseDuring(stage, cancel: false);
                await PauseDuring(stage, cancel: true);
            }
            await FailedBuildPublishesNothing();
            await RebuildKeepsPublishedSnapshot(cancel: false);
            await RebuildKeepsPublishedSnapshot(cancel: true);
            Console.WriteLine($"PASS: action-index publication, concurrent startup reads, cancellation and search/filter semantics ({checks} checks)");
        }
    }
}
