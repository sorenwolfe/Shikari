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

        var enemy = await GetCastsAsync(clientId, secret, code, fight, "Enemies", cancel).ConfigureAwait(false);
        var friendly = await GetCastsAsync(clientId, secret, code, fight, "Friendlies", cancel).ConfigureAwait(false);

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

    private async Task<List<LogCast>> GetCastsAsync(
        string clientId, string secret, string code, LogFight fight, string hostility, CancellationToken cancel)
    {
        var results = new List<LogCast>();
        var startTime = (double)fight.StartTime;
        var pages = 0;

        // begincast marks the bar going up, cast marks it resolving. Pair them so a step knows how
        // long its cast bar is; abilities with no begincast are instants.
        var pending = new Dictionary<(int Source, uint Ability), float>();

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
                    startTime: {{startTime.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}}
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
                break;

            foreach (var row in rows)
            {
                var type = row.Value<string>("type") ?? string.Empty;
                var abilityId = row.Value<uint?>("abilityGameID") ?? 0;
                var source = row.Value<int?>("sourceID") ?? 0;
                var timestamp = row.Value<long?>("timestamp") ?? 0;

                if (abilityId == 0)
                    continue;

                var relative = (timestamp - fight.StartTime) / 1000f;
                var key = (source, abilityId);

                if (type.Equals("begincast", StringComparison.OrdinalIgnoreCase))
                {
                    pending[key] = relative;
                    results.Add(new LogCast
                    {
                        SourceId = source,
                        AbilityId = abilityId,
                        TimeSeconds = relative,
                        FromEnemy = hostility == "Enemies",
                    });
                    continue;
                }

                if (!type.Equals("cast", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (pending.TryGetValue(key, out var began))
                {
                    // Fill in the bar length on the begincast we already recorded.
                    var match = results.LastOrDefault(c =>
                        c.SourceId == source && c.AbilityId == abilityId && Math.Abs(c.TimeSeconds - began) < 0.01f);

                    if (match != null)
                    {
                        var index = results.IndexOf(match);
                        results[index] = new LogCast
                        {
                            SourceId = source,
                            AbilityId = abilityId,
                            TimeSeconds = began,
                            CastSeconds = MathF.Max(0f, relative - began),
                            FromEnemy = match.FromEnemy,
                        };
                    }

                    pending.Remove(key);
                    continue;
                }

                // An instant, with no bar in front of it.
                results.Add(new LogCast
                {
                    SourceId = source,
                    AbilityId = abilityId,
                    TimeSeconds = relative,
                    FromEnemy = hostility == "Enemies",
                });
            }

            var next = events?.Value<double?>("nextPageTimestamp");
            if (next == null)
                break;

            startTime = next.Value;
        }

        return results.OrderBy(c => c.TimeSeconds).ToList();
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
            var master = page == 0 ? "masterData { abilities { gameID name } }" : string.Empty;
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
