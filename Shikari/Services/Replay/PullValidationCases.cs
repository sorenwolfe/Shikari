using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Shikari.Model;

namespace Shikari.Services.Replay;

/// <summary>Local reviewed expectations. The original recording is required; no replay or source link is stored here.</summary>
public sealed class PullValidationCase
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ReviewNote { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string AttemptId { get; set; } = "";
    public string PlanId { get; set; } = "";
    public long ActorId { get; set; }
    public uint TerritoryId { get; set; }
    public string BindingFingerprint { get; set; } = "";
    public List<PullExpectedAssignment> Expected { get; set; } = new();
}

public static class PullValidationCases
{
    public const int MaxCases = 64;
    public const int MaxExpected = 1024;
    public const int MaxName = 80;
    public const int MaxReviewNote = 1000;

    // Match the retained plan's semantics: editor IDs and fields unused by an item's kind are
    // intentionally not persisted. Only collection guards need overriding, because a malformed
    // null collection must not hash like a valid empty one or throw in ShouldSerialize.
    private sealed class ExactContractResolver : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization serialization)
        {
            var property = base.CreateProperty(member, serialization);
            if (member.DeclaringType == typeof(StatusBranch) && member.Name == nameof(StatusBranch.AdditionalStatuses) ||
                member.DeclaringType == typeof(PlanDocument) && member.Name is nameof(PlanDocument.AdaptiveMechanics) or
                    nameof(PlanDocument.StrategyEvidence) or nameof(PlanDocument.Timeline) ||
                member.DeclaringType == typeof(Slide) && member.Name == nameof(Slide.Items) ||
                member.DeclaringType == typeof(CanvasItem) && member.Name == nameof(CanvasItem.Points) ||
                member.DeclaringType == typeof(TimelineEntry) && member.Name is nameof(TimelineEntry.Assignments) or nameof(TimelineEntry.SlotCallText))
                property.ShouldSerialize = null;
            return property;
        }
    }

    /// <summary>Run on the validation worker with detached inputs. Stress scenario and unused position history are excluded.</summary>
    public static string Fingerprint(PlanDocument testedPlan, ReplayAttempt frozenAttempt, long actorId, uint territoryId)
    {
        ArgumentNullException.ThrowIfNull(testedPlan);
        ArgumentNullException.ThrowIfNull(frozenAttempt);
        if (frozenAttempt.Casts == null || frozenAttempt.Casts.Count > ReplayBuffer.MaxCasts ||
            frozenAttempt.Mechanics == null || frozenAttempt.Mechanics.Count > ReplayBuffer.MaxMechanics ||
            frozenAttempt.Evidence?.Actors == null || frozenAttempt.Evidence.Actors.Count > 32 ||
            frozenAttempt.Evidence.Statuses == null || frozenAttempt.Evidence.Statuses.Count > ReplayEvidence.MaxStatuses ||
            frozenAttempt.StatusObservations == null || frozenAttempt.StatusObservations.Count > 4096 ||
            frozenAttempt.AdaptiveDecisions == null || frozenAttempt.AdaptiveDecisions.Count > 1024)
            throw new ArgumentException("The recording exceeds reviewed-case limits.");

        var evidence = frozenAttempt.Evidence;
        var inputs = new
        {
            BindingVersion = 1, TestedPlan = testedPlan, ActorId = actorId, TerritoryId = territoryId,
            Recording = new
            {
                frozenAttempt.Version, frozenAttempt.Id, frozenAttempt.StartedUtc, frozenAttempt.Plan,
                frozenAttempt.LocalSlot, frozenAttempt.TerritoryId, frozenAttempt.Duration, frozenAttempt.EndReason,
                Casts = frozenAttempt.Casts.Select(c => c == null ? null : new {
                    c.Source, c.ActionId, c.Occurrence, c.ObservedTime, c.StartTime, c.ExpectedEndTime,
                    c.CompletionTime, c.ObservedAtUtc, c.StartedAtUtc, c.CasterId, c.TargetId }),
                frozenAttempt.Mechanics, frozenAttempt.StatusObservations, frozenAttempt.AdaptiveDecisions,
                Evidence = new { evidence.Source, evidence.Url, evidence.ReportCode, evidence.FightId,
                    evidence.EncounterId, evidence.Complete, evidence.Warnings, evidence.Actors, evidence.Statuses },
            },
        };
        // Only the digest is retained. Streaming avoids a second large serialized evidence string.
        using var hash = SHA256.Create();
        using (var crypto = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write, leaveOpen: true))
        using (var text = new StreamWriter(crypto, new UTF8Encoding(false), 4096, leaveOpen: true))
        using (var json = new JsonTextWriter(text) { CloseOutput = false })
        {
            var serializer = JsonSerializer.Create(new JsonSerializerSettings {
                ContractResolver = new ExactContractResolver(), Culture = CultureInfo.InvariantCulture,
                NullValueHandling = NullValueHandling.Include, DefaultValueHandling = DefaultValueHandling.Include,
                TypeNameHandling = TypeNameHandling.None, DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                // Retained boards use four-decimal normalized coordinates. The same canonical
                // values bind a case before and after ReplayStore serializes its recording.
                Converters = { new CompactVector2Converter() },
            });
            serializer.Serialize(json, inputs);
            json.Flush(); text.Flush(); crypto.FlushFinalBlock();
        }
        return Convert.ToHexString(hash.Hash!);
    }

    /// <summary>The fingerprint must be the worker-produced binding for these exact tested inputs.</summary>
    public static PullValidationCase Create(string name, string reviewNote, PlanDocument testedPlan,
        ReplayAttempt frozenAttempt, long actorId, uint territoryId, string bindingFingerprint,
        IReadOnlyList<PullExpectedAssignment> expected)
    {
        ArgumentNullException.ThrowIfNull(testedPlan);
        ArgumentNullException.ThrowIfNull(frozenAttempt);
        ArgumentNullException.ThrowIfNull(expected);
        var now = DateTime.UtcNow;
        var result = new PullValidationCase {
            Id = Guid.NewGuid().ToString("N"), Name = name?.Trim() ?? "", ReviewNote = reviewNote?.Trim() ?? "",
            CreatedUtc = now, UpdatedUtc = now, AttemptId = frozenAttempt.Id, PlanId = testedPlan.Id,
            ActorId = actorId, TerritoryId = territoryId, BindingFingerprint = bindingFingerprint,
            Expected = CopyExpected(expected),
        };
        Validate(result);
        foreach (var e in result.Expected)
        {
            var rules = testedPlan.AdaptiveMechanics?.Where(r => r != null && r.Id == e.RuleId).ToArray();
            if (rules is not { Length: 1 } || rules[0].AnchorActionId != e.AnchorActionId ||
                rules[0].TerritoryId != territoryId || (rules[0].Occurrence != 0 && rules[0].Occurrence != e.Occurrence) ||
                (e.BranchIndex >= 0 && (rules[0].Branches == null || e.BranchIndex >= rules[0].Branches.Count)))
                throw new ArgumentException("An expected assignment does not belong to the tested rule and occurrence.");
        }
        return result;
    }

    public static bool IsCompatible(PullValidationCase reviewed, string bindingFingerprint) =>
        reviewed != null && IsFingerprint(reviewed.BindingFingerprint) && IsFingerprint(bindingFingerprint) &&
        string.Equals(reviewed.BindingFingerprint, bindingFingerprint, StringComparison.OrdinalIgnoreCase);

    public static PullValidationCase Copy(PullValidationCase source)
    {
        Validate(source);
        return new PullValidationCase {
            Id = source.Id, Name = source.Name, ReviewNote = source.ReviewNote, CreatedUtc = source.CreatedUtc,
            UpdatedUtc = source.UpdatedUtc, AttemptId = source.AttemptId, PlanId = source.PlanId,
            ActorId = source.ActorId, TerritoryId = source.TerritoryId, BindingFingerprint = source.BindingFingerprint,
            Expected = CopyExpected(source.Expected),
        };
    }

    private static List<PullExpectedAssignment> CopyExpected(IReadOnlyList<PullExpectedAssignment> expected)
    {
        if (expected == null || expected.Count is < 1 or > MaxExpected || expected.Any(e => e == null))
            throw new ArgumentException("A reviewed case needs between 1 and 1,024 expected assignments.");
        return expected.Select(e => new PullExpectedAssignment { RuleId = e.RuleId, AnchorActionId = e.AnchorActionId,
            Occurrence = e.Occurrence, BranchIndex = e.BranchIndex, Conflict = e.Conflict }).ToList();
    }

    internal static bool IsId(string id) => id?.Length == 32 && Guid.TryParseExact(id, "N", out _);
    private static bool IsFingerprint(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    internal static void Validate(PullValidationCase reviewed)
    {
        if (reviewed == null || !IsId(reviewed.Id) || string.IsNullOrWhiteSpace(reviewed.Name) || reviewed.Name.Length > MaxName ||
            string.IsNullOrWhiteSpace(reviewed.ReviewNote) || reviewed.ReviewNote.Length > MaxReviewNote ||
            reviewed.CreatedUtc == default || reviewed.UpdatedUtc < reviewed.CreatedUtc ||
            reviewed.CreatedUtc.Kind != DateTimeKind.Utc || reviewed.UpdatedUtc.Kind != DateTimeKind.Utc ||
            string.IsNullOrWhiteSpace(reviewed.AttemptId) || reviewed.AttemptId.Length > 128 ||
            string.IsNullOrWhiteSpace(reviewed.PlanId) || reviewed.PlanId.Length > 128 ||
            reviewed.ActorId <= 0 || reviewed.TerritoryId == 0 || !IsFingerprint(reviewed.BindingFingerprint) ||
            reviewed.Expected is not { Count: > 0 and <= MaxExpected })
            throw new ArgumentException("The reviewed case has missing or invalid metadata.");
        var keys = new HashSet<(string, uint, int)>();
        foreach (var e in reviewed.Expected)
            if (e == null || string.IsNullOrWhiteSpace(e.RuleId) || e.RuleId.Length > 128 || e.AnchorActionId == 0 ||
                e.Occurrence is < 1 or > ReplayBuffer.MaxCasts || e.BranchIndex is < -1 or > 15 ||
                (e.Conflict && e.BranchIndex != -1) || !keys.Add((e.RuleId, e.AnchorActionId, e.Occurrence)))
                throw new ArgumentException("An expected assignment is invalid or duplicated.");
    }
}
