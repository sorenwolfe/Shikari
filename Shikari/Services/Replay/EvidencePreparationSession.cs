using System;
using System.Threading.Tasks;

namespace Shikari.Services.Replay;

public sealed record EvidencePreparationResult(StrategyMergeSession Session, ReplayAttempt Attempt,
    StrategyMergeSession.Prepared Prepared);

/// <summary>One detached preparation at a time. Cancellation discards publication and does not
/// allow another large job to accumulate while the original worker finishes.</summary>
public sealed class EvidencePreparationSession : IDisposable
{
    private Task<EvidencePreparationResult>? work;
    private bool discard;
    private bool disposed;
    public bool Pending => work != null;
    public bool Ready => work?.IsCompleted == true;
    public bool Start(Func<EvidencePreparationResult> prepare)
    {
        if (disposed || work != null) return false;
        discard = false;
        work = Task.Run(prepare);
        return true;
    }
    public void Cancel() => discard = true;
    public bool Poll(bool compatible, out EvidencePreparationResult? completed, out string error)
    {
        completed = null; error = "";
        if (!compatible) discard = true;
        if (work?.IsCompleted != true) return false;
        var task = work; work = null;
        try
        {
            var result = task.GetAwaiter().GetResult();
            if (!discard) completed = result;
        }
        catch (Exception ex) { if (!discard) error = ex.Message; }
        return true;
    }
    public void Dispose()
    {
        disposed = true; discard = true;
        if (work != null) _ = work.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
        work = null;
    }
}
