using System;
using System.Collections.Generic;
using Shikari.Model;
using Shikari.Services.Storage;
namespace Shikari.Tests;
public static class EditorAutosaveTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        var editor = new EditorAutosave();
        var plan = PlanDocument.CreateDefault();
        var now = DateTime.UtcNow;
        var requests = new List<PlanSaveTicket>();
        PlanSaveTicket Request(PlanDocument p) { var t = new PlanSaveTicket(p.Id, requests.Count + 1); requests.Add(t); return t; }
        void Finish(int i, PlanSaveOutcome outcome) => requests[i].Source.SetResult(new(plan.Id, requests[i].Revision, outcome, null, now));
        Check(!editor.Update(plan, now, Request) && requests.Count == 0, "An untouched plan needs no save");
        editor.MarkDirty(plan);
        editor.RequestNow(plan, now, Request);
        Check(editor.IsDirty(plan) && requests.Count == 1, "Enqueueing does not acknowledge durability");
        editor.Update(plan, now.AddSeconds(3), Request);
        Check(requests.Count == 1, "An unchanged in-flight snapshot must not be queued repeatedly");
        editor.MarkDirty(plan);
        Finish(0, PlanSaveOutcome.Saved);
        Check(!editor.Update(plan, now.AddSeconds(3), Request) && editor.IsDirty(plan) && requests.Count == 2,
            "An older completion must not acknowledge a newer edit");
        Finish(1, PlanSaveOutcome.Failed);
        Check(!editor.Update(plan, now.AddSeconds(3.1), Request) && editor.IsDirty(plan), "Failure keeps edits pending");
        editor.RequestNow(plan, now.AddSeconds(3.2), Request);
        Finish(2, PlanSaveOutcome.Superseded);
        Check(!editor.Update(plan, now.AddSeconds(3.3), Request) && editor.IsDirty(plan), "Superseded is not saved");
        editor.RequestNow(plan, now.AddSeconds(4), Request);
        Finish(3, PlanSaveOutcome.Saved);
        Check(editor.Update(plan, now.AddSeconds(4.1), Request) && !editor.IsDirty(plan), "Matching success acknowledges edits once");
        Check(!editor.Update(plan, now.AddSeconds(5), Request), "Acknowledgment is not repeated");
        editor.MarkDirty(plan); editor.RequestNow(plan, now.AddSeconds(6), Request);
        var replacement = PlanDocument.CreateDefault(); replacement.Id = plan.Id;
        editor.MarkDirty(replacement);
        Finish(4, PlanSaveOutcome.Saved);
        Check(!editor.Update(replacement, now.AddSeconds(6.1), Request) && editor.IsDirty(replacement),
            "Same-ID replacement cannot receive the old object's acknowledgment");
        editor.RequestNow(replacement, now.AddSeconds(7), Request);
        Check(requests.Count >= 6, "Closing or retry can immediately request the current revision");
        Console.WriteLine("PASS: editor autosave revisions, delayed success, failure/retry, supersession, replacement and single acknowledgment");
    }
}
