using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Shikari.Model;

namespace Shikari.Services;

/// <summary>
/// Turns a plan into a single line of text that survives Discord, and back again.
/// RPLAN1 is gzip JSON. RPLAN2 is a checked, length-prefixed Brotli JSON payload.
/// </summary>
public static class ShareCode
{
    private const string Prefix = "RPLAN1:";
    private const string CompactPrefix = "RPLAN2:";
    private const int CompactHeaderBytes = 12; // uint32 LE unpacked length + first 8 SHA256 bytes
    public const int MaximumEncodedCharacters = 1024 * 1024;
    public const int MaximumDecompressedBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerSettings Settings = PlanJson.Compact();

    public static string Encode(PlanDocument document)
    {
        // Backdrops are local files the person on the other end does not have, and an image would
        // blow the code past what Discord will carry anyway. The drawing travels; the tracing
        // reference it was made from does not.
        var json = JsonConvert.SerializeObject(WithoutBackdrops(document), Settings);
        var raw = Encoding.UTF8.GetBytes(json);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(raw, 0, raw.Length);
        }

        var legacy = output.ToArray();
        var compact = new byte[CompactHeaderBytes + BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        // Quality 6 keeps compression suitable for the editor's periodic size estimate.
        if (BrotliEncoder.TryCompress(raw, compact.AsSpan(CompactHeaderBytes), out var written, quality: 6, window: 22)
            && CompactHeaderBytes + written < legacy.Length)
        {
            BinaryPrimitives.WriteInt32LittleEndian(compact, raw.Length);
            SHA256.HashData(raw).AsSpan(0, 8).CopyTo(compact.AsSpan(4, 8));
            return CompactPrefix + ToBase64Url(compact.AsSpan(0, CompactHeaderBytes + written).ToArray());
        }
        return Prefix + ToBase64Url(legacy);
    }

    public static bool TryDecode(string? code, out PlanDocument? document, out string error)
    {
        document = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(code))
        {
            error = "Nothing to import — the code is empty.";
            return false;
        }

        if (code.Length > MaximumEncodedCharacters)
        {
            error = "The share code is too large (maximum 1 MiB of text).";
            return false;
        }

        var cleaned = Clean(code, out var isCompact);
        if (cleaned.Length == 0)
        {
            error = "That does not look like a Shikari code.";
            return false;
        }

        try
        {
            var compressed = FromBase64Url(cleaned);

            var json = isCompact ? UnpackCompact(compressed) : UnpackLegacy(compressed);

            var parsed = JsonConvert.DeserializeObject<PlanDocument>(json, Settings);
            if (parsed == null)
            {
                error = "The code unpacked but held no plan.";
                return false;
            }

            if (parsed.FormatVersion > PlanDocument.CurrentFormatVersion)
            {
                error = $"This plan was made with a newer version of Shikari (format {parsed.FormatVersion}). Update the plugin first.";
                return false;
            }

            ValidateStructure(parsed);
            document = PlanNormaliser.Normalise(parsed);
            foreach (var rule in document.AdaptiveMechanics) rule.Enabled = false;
            return true;
        }
        catch (Exception ex)
        {
            error = "The code is damaged or incomplete — check that the whole line was copied. " + ex.Message;
            return false;
        }
    }

    private static string UnpackCompact(byte[] compressed)
    {
        if (compressed.Length <= CompactHeaderBytes)
            throw new InvalidDataException("The compact header is incomplete.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(compressed);
        if (length <= 0 || length > MaximumDecompressedBytes)
            throw new InvalidDataException("The unpacked plan exceeds the 4 MiB limit or has an invalid length.");
        var raw = new byte[length];
        using var decoder = new BrotliDecoder();
        var payload = compressed.AsSpan(CompactHeaderBytes);
        var status = decoder.Decompress(payload, raw, out var consumed, out var written);
        if (status != OperationStatus.Done || consumed != payload.Length || written != length ||
            !SHA256.HashData(raw).AsSpan(0, 8).SequenceEqual(compressed.AsSpan(4, 8)))
            throw new InvalidDataException("The compact payload is incomplete or its integrity check failed.");
        return Encoding.UTF8.GetString(raw);
    }

    private static string UnpackLegacy(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var unpacked = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (unpacked.Length + count > MaximumDecompressedBytes)
                throw new InvalidDataException("The unpacked plan exceeds the 4 MiB limit.");
            unpacked.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(unpacked.ToArray());
    }

    private static void ValidateStructure(PlanDocument plan)
    {
        if (!StrategyEvidenceValidation.IsValid(plan))
            throw new InvalidDataException("Invalid or oversized strategy evidence.");
        if (plan.Arena != null && !SlideMetadataValidation.ValidArena(plan.Arena))
            throw new InvalidDataException("Invalid arena settings.");
        if ((plan.AdaptiveMechanics?.Count ?? 0) > 128 ||
            plan.AdaptiveMechanics?.Any(r => r == null || r.Branches == null || r.Branches.Count > 16 ||
                r.Branches.Any(b => b == null || b.AdditionalStatuses == null || b.AdditionalStatuses.Count > 3 ||
                    b.AdditionalStatuses.Any(c => c == null))) == true)
            throw new InvalidDataException("Invalid adaptive mechanic rules or too many branches.");
        if ((plan.Roster?.Count ?? 0) > 48 || (plan.Slides?.Count ?? 0) > 256 ||
            (plan.Timeline?.Count ?? 0) > 4096)
            throw new InvalidDataException("The plan has too many seats, slides or timeline entries.");
        long items = 0, points = 0, assignments = 0;
        if (plan.Roster?.Any(slot => slot == null) == true)
            throw new InvalidDataException("The roster contains an empty seat entry.");
        if (plan.Slides != null)
            foreach (var slide in plan.Slides)
            {
                if (slide == null) throw new InvalidDataException("The plan contains an empty slide entry.");
                if (!SlideMetadataValidation.IsValid(slide))
                    throw new InvalidDataException("Invalid slide arena or source metadata.");
                items += slide.Items?.Count ?? 0;
                if (slide.Items == null) continue;
                foreach (var item in slide.Items)
                {
                    if (item == null) throw new InvalidDataException("A slide contains an empty drawing entry.");
                    points += item.Points?.Count ?? 0;
                }
            }
        if (plan.Timeline != null)
            foreach (var entry in plan.Timeline)
            {
                if (entry == null) throw new InvalidDataException("The timeline contains an empty entry.");
                assignments += entry.Assignments?.Count ?? 0;
                if (entry.Assignments?.Any(a => a == null) == true || (entry.SlotCallText?.Count ?? 0) > 48)
                    throw new InvalidDataException("The timeline contains invalid assignments or too many seat calls.");
            }
        if (items > 16384 || points > 131072 || assignments > 32768)
            throw new InvalidDataException("The plan has too many drawing items, points or assignments.");
    }

    private static string Clean(string code, out bool isCompact)
    {
        var span = code.Trim();
        var compactIndex = span.IndexOf(CompactPrefix, StringComparison.OrdinalIgnoreCase);
        var legacyIndex = span.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        isCompact = compactIndex >= 0 && (legacyIndex < 0 || compactIndex < legacyIndex);
        var idx = isCompact ? compactIndex : legacyIndex;
        if (idx >= 0)
            span = span[(idx + Prefix.Length)..];

        var sb = new StringBuilder(span.Length);
        foreach (var c in span)
        {
            if (char.IsWhiteSpace(c))
                continue;
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>A copy with the tracing backdrops stripped, for sharing.</summary>
    private static PlanDocument WithoutBackdrops(PlanDocument document)
    {
        if (document.Slides == null || !document.Slides.Any(s => s.HasBackdrop))
            return document;

        var copy = JsonConvert.DeserializeObject<PlanDocument>(
            JsonConvert.SerializeObject(document, Settings), Settings)!;

        foreach (var slide in copy.Slides)
            slide.BackdropId = string.Empty;

        return copy;
    }

    private static string ToBase64Url(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }
}
