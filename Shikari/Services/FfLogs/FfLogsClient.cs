using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Shikari.Services.FfLogs;

public sealed class FfLogsException : Exception
{
    public FfLogsException(string message, string? detail = null) : base(message) => Detail = detail;

    /// <summary>Raw response body, when there is one worth showing.</summary>
    public string? Detail { get; }
}

/// <summary>
/// Talks to the FF Logs v2 API. Needs a client id and secret, which anyone can create for
/// themselves at fflogs.com/api/clients — there is no anonymous access.
/// </summary>
public sealed class FfLogsClient : IDisposable
{
    private const string TokenUrl = "https://www.fflogs.com/oauth/token";
    private const string ApiUrl = "https://www.fflogs.com/api/v2/client";

    private readonly HttpClient http;

    public FfLogsClient() => http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    internal FfLogsClient(HttpMessageHandler handler) =>
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

    private string token = string.Empty;
    private DateTime tokenExpiresUtc = DateTime.MinValue;

    /// <summary>
    /// Which credentials the cached token belongs to. Without this a corrected id or secret goes
    /// on using the token the old ones bought, until it expires an hour later.
    /// </summary>
    private string tokenFor = string.Empty;

    /// <summary>Last raw response, kept so a failed import can show what actually came back.</summary>
    public string LastResponse { get; private set; } = string.Empty;

    public async Task<string> GetTokenAsync(string clientId, string clientSecret, CancellationToken cancel = default)
    {
        var fingerprint = Fingerprint(clientId, clientSecret);

        if (!string.IsNullOrEmpty(token) && DateTime.UtcNow < tokenExpiresUtc && tokenFor == fingerprint)
            return token;

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new FfLogsException("No FF Logs client id and secret set. Add them in settings.");

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
        });

        using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        LastResponse = body;

        if (!response.IsSuccessStatusCode)
        {
            throw new FfLogsException(
                $"FF Logs refused the credentials ({(int)response.StatusCode}). Check the client id and secret.",
                body);
        }

        var json = JObject.Parse(body);
        token = json.Value<string>("access_token") ?? string.Empty;
        if (string.IsNullOrEmpty(token))
            throw new FfLogsException("FF Logs returned no access token.", body);

        var seconds = json.Value<int?>("expires_in") ?? 3600;
        tokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 60));
        tokenFor = fingerprint;

        return token;
    }

    /// <summary>Identifies a credential pair without keeping it around in a readable form.</summary>
    public static string Fingerprint(string clientId, string clientSecret) =>
        Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes((clientId ?? string.Empty) + "\u0000" + (clientSecret ?? string.Empty))));

    /// <summary>Throws away any cached token, so the next call has to authenticate again.</summary>
    public void ForgetToken()
    {
        token = string.Empty;
        tokenExpiresUtc = DateTime.MinValue;
        tokenFor = string.Empty;
    }

    private async Task<JObject> QueryAsync(string clientId, string clientSecret, string query, CancellationToken cancel)
    {
        var bearer = await GetTokenAsync(clientId, clientSecret, cancel).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = new StringContent(
            JsonConvert.SerializeObject(new { query }),
            Encoding.UTF8,
            "application/json");

        using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        LastResponse = body;

        if (!response.IsSuccessStatusCode)
            throw new FfLogsException($"FF Logs returned {(int)response.StatusCode}.", body);

        var json = JObject.Parse(body);

        if (json["errors"] is JArray errors && errors.Count > 0)
        {
            var first = errors[0]?.Value<string>("message") ?? "unknown error";
            throw new FfLogsException("FF Logs rejected the query: " + first, body);
        }

        return json;
    }

    /// <summary>Fights in a report, so the user can pick which pull to import.</summary>
    public async Task<List<LogFight>> GetFightsAsync(string clientId, string secret, string code, CancellationToken cancel = default)
    {
        var query = $$"""
        query {
          reportData {
            report(code: "{{Escape(code)}}") {
              fights {
                id
                encounterID
                name
                startTime
                endTime
                kill
                fightPercentage
              }
            }
          }
        }
        """;

        var json = await QueryAsync(clientId, secret, query, cancel).ConfigureAwait(false);
        var fights = json.SelectToken("data.reportData.report.fights") as JArray;

        if (fights == null)
            throw new FfLogsException("That report has no fights, or the code is wrong.", LastResponse);

        return fights.Select(f => new LogFight
        {
            Id = f.Value<int?>("id") ?? 0,
            EncounterId = (uint)Math.Max(0, f.Value<int?>("encounterID") ?? 0),
            Name = f.Value<string>("name") ?? "Fight",
            StartTime = f.Value<long?>("startTime") ?? 0,
            EndTime = f.Value<long?>("endTime") ?? 0,
            Kill = f.Value<bool?>("kill") ?? false,
            FightPercentage = f.Value<float?>("fightPercentage") ?? 0f,
        }).ToList();
    }

    /// <summary>Everything needed to import one fight: who was there, and every cast.</summary>
    public async Task<LogFightData> GetFightDataAsync(
        string clientId, string secret, string code, LogFight fight, CancellationToken cancel = default)
    {
        var master = $$"""
        query {
          reportData {
            report(code: "{{Escape(code)}}") {
              fights(fightIDs: [{{fight.Id}}]) {
                id startTime endTime friendlyPlayers
                enemyNPCs { id instanceCount }
                friendlyNPCs { id instanceCount }
              }
              masterData {
                actors { id name type subType }
                abilities { gameID name }
              }
            }
          }
        }
        """;

        var masterJson = await QueryAsync(clientId, secret, master, cancel).ConfigureAwait(false);
        var root = masterJson.SelectToken("data.reportData.report.masterData");

        var actors = (root?["actors"] as JArray ?? new JArray()).Select(a => new LogActor
        {
            Id = a.Value<int?>("id") ?? 0,
            Name = a.Value<string>("name") ?? string.Empty,
            Type = a.Value<string>("type") ?? string.Empty,
            Job = a.Value<string>("subType") ?? string.Empty,
        }).ToList();

        var abilityNames = new Dictionary<uint, string>();
        foreach (var ability in root?["abilities"] as JArray ?? new JArray())
        {
            var id = ability.Value<uint?>("gameID");
            var name = ability.Value<string>("name");
            if (id.HasValue && !string.IsNullOrEmpty(name))
                abilityNames[id.Value] = name;
        }

        var uniqueActors = SingleInstanceActors(masterJson.SelectToken("data.reportData.report") as JObject, fight);
        var enemy = await GetCastsAsync(clientId, secret, code, fight, "Enemies", uniqueActors, cancel).ConfigureAwait(false);
        var friendly = await GetCastsAsync(clientId, secret, code, fight, "Friendlies", uniqueActors, cancel).ConfigureAwait(false);

        foreach (var cast in enemy.Concat(friendly))
        {
            if (string.IsNullOrEmpty(cast.AbilityName) && abilityNames.TryGetValue(cast.AbilityId, out var name))
                cast.AbilityName = name;
        }

        return new LogFightData
        {
            ReportCode = code,
            Fight = fight,
            Actors = actors,
            EnemyCasts = enemy,
            PlayerCasts = friendly,
            AbilityNames = abilityNames,
        };
    }

    private static JObject? SelectedFight(JObject? report, LogFight fight)
    {
        if (report?["fights"] is not JArray fights) return null;
        var matches = fights.OfType<JObject>().Where(f => LogEvidenceParser.Integer(f["id"], 1, int.MaxValue) == fight.Id).ToArray();
        return matches.Length == 1 && LogEvidenceParser.Number(matches[0]["startTime"], out var start) && start == fight.StartTime &&
            LogEvidenceParser.Number(matches[0]["endTime"], out var end) && end == fight.EndTime ? matches[0] : null;
    }

    private static HashSet<int>? PlayerScope(JObject? report, LogFight fight)
    {
        if (SelectedFight(report, fight)?["friendlyPlayers"] is not JArray ids || ids.Count > 32 ||
            report?["masterData"] is not JObject metadata || metadata["actors"] is not JArray actors) return null;
        var byId = actors.OfType<JObject>().GroupBy(a => LogEvidenceParser.Integer(a["id"], 1, int.MaxValue))
            .Where(g => g.Key.HasValue).ToDictionary(g => (int)g.Key!.Value, g => g.ToArray());
        var result = new HashSet<int>();
        foreach (var token in ids)
        {
            var id = LogEvidenceParser.Integer(token, 1, int.MaxValue);
            if (id == null || !result.Add((int)id.Value) || !byId.TryGetValue((int)id.Value, out var rows) || rows.Length != 1 ||
                rows[0]["type"]?.Type != JTokenType.String || !string.Equals(rows[0].Value<string>("type"), "Player", StringComparison.OrdinalIgnoreCase)) return null;
        }
        return result;
    }

    private static HashSet<int> SingleInstanceActors(JObject? report, LogFight fight)
    {
        var metadata = report?["masterData"] as JObject;
        var actors = (metadata?["actors"] as JArray ?? new()).OfType<JObject>()
            .GroupBy(a => LogEvidenceParser.Integer(a["id"], 1, int.MaxValue)).Where(g => g.Key.HasValue && g.Count() == 1)
            .ToDictionary(g => (int)g.Key!.Value, g => g.Single()["type"]?.Type == JTokenType.String ? g.Single().Value<string>("type") : null);
        var result = actors.Where(a => string.Equals(a.Value, "Player", StringComparison.OrdinalIgnoreCase)).Select(a => a.Key).ToHashSet();
        var selected = SelectedFight(report, fight);
        var npcs = new[] { "enemyNPCs", "friendlyNPCs" }.SelectMany(key => (selected?[key] as JArray ?? new()).OfType<JObject>())
            .GroupBy(a => LogEvidenceParser.Integer(a["id"], 1, int.MaxValue));
        foreach (var group in npcs)
            if (group.Key is { } id && group.Count() == 1 && LogEvidenceParser.Integer(group.Single()["instanceCount"], 1, int.MaxValue) == 1 &&
                actors.TryGetValue((int)id, out var type) && string.Equals(type, "NPC", StringComparison.OrdinalIgnoreCase)) result.Add((int)id);
        return result;
    }

    private async Task<List<LogCast>> GetCastsAsync(
        string clientId, string secret, string code, LogFight fight, string hostility, IReadOnlySet<int> uniqueActors, CancellationToken cancel)
    {
        var parser = new LogCastParser(fight, hostility == "Enemies", uniqueActors);
        var startTime = (double)fight.StartTime;
        var pages = 0;
        var count = 0;
        var finished = false;

        // A cast event is completion evidence, not proof of a damage/effect resolution.

        while (pages++ < 20)
        {
            var query = $$"""
            query {
              reportData {
                report(code: "{{Escape(code)}}") {
                  events(
                    fightIDs: [{{fight.Id}}]
                    dataType: Casts
                    hostilityType: {{hostility}}
                    startTime: {{startTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}
                    endTime: {{fight.EndTime}}
                    limit: 10000
                  ) {
                    data
                    nextPageTimestamp
                  }
                }
              }
            }
            """;

            var json = await QueryAsync(clientId, secret, query, cancel).ConfigureAwait(false);
            var events = json.SelectToken("data.reportData.report.events");
            var rows = events?["data"] as JArray;

            if (rows == null)
                throw new FfLogsException("FF Logs returned no readable cast event page. Retry the import.");
            // Occurrence numbers require a complete cast history. Do not return a silently
            // truncated list that optional, separately fetched evidence might label complete.
            if (count + (long)rows.Count > 200000)
                throw new FfLogsException("FF Logs cast history exceeded the event limit. Choose a shorter pull.");
            count += rows.Count;

            cancel.ThrowIfCancellationRequested();
            parser.Add(rows);

            var nextToken = events?["nextPageTimestamp"];
            if (nextToken?.Type == JTokenType.Null)
            {
                finished = true;
                break;
            }
            if (!LogEvidenceParser.Number(nextToken, out var next) || next <= startTime || next > fight.EndTime)
                throw new FfLogsException("FF Logs returned an invalid cast pagination cursor. Retry the import.");

            startTime = next;
        }

        if (!finished)
            throw new FfLogsException("FF Logs cast history reached the page limit. Choose a shorter pull.");
        cancel.ThrowIfCancellationRequested();
        return parser.Finish();
    }

    /// <summary>Read optional status and sparse position evidence, bounded to 20 pages / 200,000
    /// events. Partial reads carry warnings; caller cancellation always propagates.</summary>
    public async Task<LogEvidence> GetEvidenceAsync(
        string clientId, string secret, string code, LogFight fight, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        if (fight.Id <= 0 || fight.StartTime < 0 || fight.EndTime < fight.StartTime)
            throw new ArgumentException("Invalid fight range.", nameof(fight));
        var names = new Dictionary<uint, string>();
        var parser = new LogEvidenceParser(fight, names);
        var cursor = (double)fight.StartTime;
        var count = 0;
        var finished = false;
        for (var page = 0; page < 20; page++)
        {
            cancel.ThrowIfCancellationRequested();
            var master = page == 0 ? $$"""
                masterData { abilities { gameID name } actors { id type } }
                fights(fightIDs: [{{fight.Id}}]) { id startTime endTime friendlyPlayers }
                """ : string.Empty;
            var query = $$"""
            query {
              reportData {
                report(code: "{{Escape(code)}}") {
                  {{master}}
                  events(
                    fightIDs: [{{fight.Id}}]
                    dataType: All
                    includeResources: true
                    startTime: {{cursor.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}
                    endTime: {{fight.EndTime.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
                    limit: 10000
                  ) { data nextPageTimestamp }
                }
              }
            }
            """;
            JObject json;
            try
            {
                json = await QueryAsync(clientId, secret, query, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                parser.Warn("FF Logs evidence request timed out; the evidence is incomplete.", true);
                break;
            }
            catch (Exception ex) when (ex is FfLogsException or HttpRequestException or JsonException)
            {
                parser.Warn("FF Logs evidence could not be fully retrieved; retry the import.", true);
                break;
            }
            var report = json.SelectToken("data.reportData.report") as JObject;
            if (page == 0)
            {
                var targets = PlayerScope(report, fight);
                parser = new LogEvidenceParser(fight, names, targets);
                if (targets == null)
                {
                    parser.Result.EffectsComplete = false;
                    parser.Warn("The selected fight's player list could not be verified. Damage filtering and effect coverage are unverified; status observations remain available.");
                }
            }
            if (report?["masterData"] is JObject masterData && masterData["abilities"] is JArray abilities)
            {
                foreach (var ability in abilities.OfType<JObject>())
                {
                    var id = LogEvidenceParser.Integer(ability["gameID"], 1, uint.MaxValue);
                    if (id.HasValue && ability["name"]?.Type == JTokenType.String)
                        names[(uint)id.Value] = ability.Value<string>("name") ?? string.Empty;
                }
            }
            if (report?["events"] is not JObject events || events["data"] is not JArray rows)
            {
                parser.Warn("FF Logs returned no readable event page; the evidence is incomplete.", true);
                break;
            }
            // The service can exceed limit slightly to finish a group at one timestamp.
            if (count + (long)rows.Count > 200000)
            {
                parser.Warn("FF Logs evidence exceeded the event limit; the evidence is incomplete.", true);
                break;
            }
            count += rows.Count;
            parser.AddPage(rows, cancel);
            var nextToken = events["nextPageTimestamp"];
            if (nextToken?.Type == JTokenType.Null)
            {
                finished = true;
                break;
            }
            if (!LogEvidenceParser.Number(nextToken, out var next) || next <= cursor || next > fight.EndTime)
            {
                parser.Warn("FF Logs returned an invalid pagination cursor; the evidence is incomplete.", true);
                break;
            }
            cursor = next;
        }
        if (!finished && parser.Result.Complete)
            parser.Warn("FF Logs evidence reached the page limit; the evidence is incomplete.", true);
        parser.Warn("Log status IDs are candidates until validated against the game's Status sheet.");
        parser.Warn("Log status parameters are unknown; stacks and extraInfo are not live status parameters.");
        if (parser.Result.StatusEvents.Any(s => s.Duration == null))
            parser.Warn("Some status durations are unknown; initial auras are baseline observations.");
        parser.Warn(parser.Result.Positions.Count == 0
            ? "No source positions were available in this pull."
            : "Positions are sparse FF Logs centicoordinates and require calibration before overlaying the plan.");
        // Stable ordering preserves the log's apply/remove order at identical timestamps.
        var statuses = parser.Result.StatusEvents.OrderBy(s => s.Time).ToArray();
        parser.Result.StatusEvents.Clear();
        parser.Result.StatusEvents.AddRange(statuses);
        var positions = parser.Result.Positions.OrderBy(p => p.Time).ToArray();
        parser.Result.Positions.Clear();
        parser.Result.Positions.AddRange(positions);
        return parser.Result;
    }

    private static string Escape(string value) => value.Replace("\"", string.Empty).Replace("\\", string.Empty);

    public void Dispose() => http.Dispose();
}
