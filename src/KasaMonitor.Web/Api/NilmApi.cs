using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using KasaMonitor.Core.Nilm;
using KasaMonitor.Workers.Nilm;

namespace KasaMonitor.Web.Api;

/// <summary>
/// HTTP API of the appliance detection (<c>/api/nilm/*</c>, JSON) for operating it without the UI (curl from the LAN).
/// Every route calls the same <see cref="NilmService"/> method as the matching button of /consumatori; no domain logic
/// here. Writes (POST) are accepted only from private addresses (10/8, 172.16/12, 192.168/16, loopback; the connection's
/// address, never a forwarded header) and logged at Information with the caller's address. When the setting
/// <see cref="TokenSetting"/> is set, every route also needs the header <see cref="TokenHeader"/> with that value.
/// </summary>
public static class NilmApi
{
    public const string Prefix = "/api/nilm";
    public const string TokenSetting = "KasaMonitor:NilmApiToken";
    public const string TokenHeader = "X-Api-Key";

    /// <summary>Events returned with a signature's sheet.</summary>
    public const int SheetEvents = 50;

    /// <summary>Longest range of <c>GET /api/nilm/events</c>; the default range is the last 24 h.</summary>
    public static readonly TimeSpan MaxEventsRange = TimeSpan.FromDays(31);

    public sealed record LabelRequest(string? Name, string? Category);

    public sealed record MarkRequest(string? Name, string? Category, DateTimeOffset? At);

    public sealed record MarkDto(string Outcome, string Name, string? Category, DateTimeOffset At, DateTimeOffset? PendingUntil,
        EventDto? Event, SignatureDto? Signature);

    public sealed record SignatureDto(long Id, string Name, string Category, string LabelSource, string? PlugAlias, bool PlugLinked,
        double MeanDeltaP, double MeanDeltaQ, double MeanTransient, int Count, bool Hidden, DateTimeOffset? LastSeen,
        int? MinRunSeconds = null, int? MaxRunSeconds = null, long? SplitFromId = null);

    /// <summary>Body of <c>POST /signatures/{id}/duration-band</c>: both null clear the band.</summary>
    public sealed record DurationBandRequest(int? MinSeconds, int? MaxSeconds);

    /// <summary>Body of <c>POST /signatures/{id}/split-by-duration</c>: <see cref="DryRun"/> absent = true.</summary>
    public sealed record SplitByDurationRequest(int? MaxSeconds, string? Name, string? Category, bool? DryRun);

    public sealed record SpreadDto(int Count, double? P10, double? P50, double? P90);

    public sealed record SplitByDurationDto(bool DryRun, bool Applied, long SignatureId, long? TargetId, string TargetName, int Runs, int Events,
        int SkippedUserRuns, double EnergyWh, SpreadDto Moved, SpreadDto Remaining, SignatureDto? Signature, SignatureDto? Target);

    public sealed record RepetitionDto(SpreadDto Durations, SpreadDto Intervals, int Sessions, double? PulsesPerSessionP50, double? EnergyPerSessionWhP50);

    public sealed record EventDto(long Id, DateTimeOffset Ts, double DeltaP, double DeltaQ, double DeltaPf, double TransientRatio,
        bool TransientKnown, double RiseSeconds, double PowerBeforeW, string? Label, string Source, long? SignatureId, string? SignatureName);

    public sealed record ProfileDto(int Events, int OnEvents, int OffEvents, double MeanDeltaP, double StdDeltaP, double MeanDeltaQ,
        double? MeanTransient, int TransientKnown, double StepPowerFactor, int Runs, double? MedianRunSeconds, double? P10RunSeconds,
        double? P90RunSeconds, IReadOnlyList<int> Hours, string? PlugAlias, bool PlugLinked, double TopPlugShare,
        DateTimeOffset? FirstSeen, DateTimeOffset? LastSeen, double Last7DaysWh, RepetitionDto? Repetition);

    public sealed record SheetDto(SignatureDto Signature, ProfileDto Profile, IReadOnlyList<EventDto> Events);

    public sealed record MergeDto(long SurvivorId, long AbsorbedId, SignatureDto? Survivor);

    public sealed record AddToDto(string Outcome, long SignatureId, long TargetId, SignatureDto? Result);

    public sealed record MergeStepDto(long SurvivorId, long AbsorbedId, string Reason);

    public sealed record TrainDto(bool Trained, int LabeledEvents, int LabeledClasses, IReadOnlyList<string> Classes, double? Accuracy,
        int? Folds, int? Examples, double DurationSeconds);

    public sealed record RegroupDto(int Events, int Anchored, int Clusters, int Signatures, int Moved, int Merged,
        IReadOnlyList<MergeStepDto> Merges, TrainDto? Training);

    public sealed record ErrorDto(string Error);

    /// <summary>
    /// Body of <c>POST /plugs/{id}/appliance</c>: <see cref="Single"/> (default true) = „un singur aparat pe priză”;
    /// <see cref="Name"/> empty = the plug's alias; <see cref="Category"/> absent = unchanged.
    /// </summary>
    public sealed record ApplianceRequest(string? Name, string? Category, bool? Single);

    /// <summary>A plug that can label events: <see cref="ApplianceName"/> = the appliance's name in effect when <see cref="Single"/>.</summary>
    public sealed record PlugDto(Guid Id, string Alias, bool Single, string? ApplianceName, string? Category, string NilmState);

    public static RouteGroupBuilder MapNilmApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(Prefix);
        api.AddEndpointFilter(TokenFilter);

        api.MapGet("/signatures", async (bool? all, NilmService nilm, CancellationToken ct) =>
            Results.Ok((await nilm.ListSignaturesAsync(all == true, ct)).Select(ToDto).ToList()));

        api.MapGet("/signatures/{id:long}", async (long id, NilmService nilm, CancellationToken ct) =>
        {
            if (await nilm.GetProfileAsync(id, ct) is not { } view || await nilm.GetSignatureAsync(id, ct) is not { } item)
                return NotFound(id);
            var events = (await nilm.ListSignatureEventsAsync(id, SheetEvents, ct)).Select(e => ToDto(e, item.Signature.Name)).ToList();
            return Results.Ok(new SheetDto(ToDto(item), ToDto(view), events));
        });

        api.MapGet("/events", async (DateTimeOffset? from, DateTimeOffset? to, double? minAbsDeltaP, NilmService nilm, CancellationToken ct) =>
        {
            var end = to ?? DateTimeOffset.UtcNow;
            var start = from ?? end.AddHours(-24);
            if (start > end) return Error(StatusCodes.Status400BadRequest, "„from” trebuie să fie înainte de „to”.");
            if (end - start > MaxEventsRange)
                return Error(StatusCodes.Status400BadRequest, $"Intervalul e prea lung: cel mult {MaxEventsRange.TotalDays:0} de zile.");
            var rows = await nilm.ListEventsAsync(start, end, Math.Max(0, minAbsDeltaP ?? 0), ct);
            return Results.Ok(rows.Select(r => ToDto(r.Event, r.SignatureName)).ToList());
        });

        Write(api.MapPost("/signatures/{id:long}/label", (long id, LabelRequest body, NilmService nilm, HttpContext http, CancellationToken ct) => Guard(async () =>
        {
            var name = body.Name is null ? null : SignatureNaming.CleanName(body.Name);
            if (name is { Length: 0 }) return Error(StatusCodes.Status400BadRequest, "Numele e gol.");
            if (UnknownCategory(body.Category) is { } bad) return bad;
            if (name is null && body.Category is null) return Error(StatusCodes.Status400BadRequest, "Trimite „name” și/sau „category”.");
            if (name is not null && !await nilm.RenameAsync(id, name, ct)) return NotFound(id);
            if (body.Category is not null && !await nilm.SetCategoryAsync(id, body.Category, ct)) return NotFound(id);
            Log(http).LogInformation("NILM API: signature {Id} labelled (name {Name}, category {Category}) from {Ip}",
                id, name ?? "-", body.Category ?? "-", Ip(http));
            return await Current(nilm, id, ct);
        })));

        Write(api.MapPost("/mark", (MarkRequest body, NilmService nilm, HttpContext http, CancellationToken ct) => Guard(async () =>
        {
            if (SignatureNaming.CleanName(body.Name).Length == 0) return Error(StatusCodes.Status400BadRequest, "Lipsește „name”.");
            if (UnknownCategory(body.Category) is { } bad) return bad;
            if (await nilm.MarkAsync(body.Name!, body.Category, body.At?.ToUniversalTime(), ct) is not { } r)
                return Error(StatusCodes.Status400BadRequest, "Lipsește „name”.");
            Log(http).LogInformation("NILM API: mark {Name} (category {Category}) at {At:o} from {Ip}: {Outcome}{Event}",
                r.Mark.Name, r.Mark.Category ?? "-", r.Mark.AtUtc, Ip(http), r.Outcome, r.Event is { } ev ? $", event {ev.Id} at {ev.TsUtc:o}" : "");
            if (r.Outcome == MarkOutcome.NotFound)
                return Error(StatusCodes.Status404NotFound,
                    $"Niciun eveniment de cel puțin {NilmService.MarkMinDeltaW:0} W în ±{NilmService.MarkWindow.TotalSeconds:0} s de {r.Mark.AtUtc:o}.");
            var sig = r.Event?.SignatureId is { } sid ? await nilm.GetSignatureAsync(sid, ct) : null;
            return Results.Ok(new MarkDto(r.Outcome == MarkOutcome.Matched ? "matched" : "pending", r.Mark.Name, r.Mark.Category, r.Mark.AtUtc,
                r.Outcome == MarkOutcome.Pending ? r.Mark.ExpiresUtc : null,
                r.Event is { } e ? ToDto(e, sig?.Signature.Name) : null, sig is null ? null : ToDto(sig)));
        })));

        Write(api.MapPost("/signatures/{id:long}/merge-into/{targetId:long}", (long id, long targetId, NilmService nilm, HttpContext http, CancellationToken ct) => Guard(async () =>
        {
            if (id == targetId) return Error(StatusCodes.Status400BadRequest, "O semnătură nu se poate uni cu ea însăși.");
            if (await nilm.MergeWithAsync(id, targetId, ct) is not { } survivor) return NotFound(id, targetId);
            var absorbed = survivor == id ? targetId : id;
            Log(http).LogInformation("NILM API: signatures {Id} and {TargetId} merged, {Survivor} stays, from {Ip}", id, targetId, survivor, Ip(http));
            var item = await nilm.GetSignatureAsync(survivor, ct);
            return Results.Ok(new MergeDto(survivor, absorbed, item is null ? null : ToDto(item)));
        })));

        Write(api.MapPost("/signatures/{id:long}/add-to/{targetId:long}", (long id, long targetId, NilmService nilm, HttpContext http, CancellationToken ct) => Guard(async () =>
        {
            if (id == targetId) return Error(StatusCodes.Status400BadRequest, "O semnătură nu se poate adăuga la ea însăși.");
            var outcome = await nilm.AddToAsync(id, targetId, ct);
            if (outcome == AddToOutcome.Failed) return NotFound(id, targetId);
            var kept = outcome == AddToOutcome.Merged ? targetId : id;
            Log(http).LogInformation("NILM API: signature {Id} added to {TargetId} ({Outcome}) from {Ip}", id, targetId, outcome, Ip(http));
            var item = await nilm.GetSignatureAsync(kept, ct);
            return Results.Ok(new AddToDto(outcome == AddToOutcome.Merged ? "merged" : "named", id, targetId, item is null ? null : ToDto(item)));
        })));

        Write(api.MapPost("/signatures/{id:long}/duration-band", (long id, DurationBandRequest body, NilmService nilm, HttpContext http, CancellationToken ct) => Guard(async () =>
        {
            if (DurationBands.Invalid(body.MinSeconds, body.MaxSeconds) is { } invalid) return Error(StatusCodes.Status400BadRequest, invalid);
            if (!await nilm.SetDurationBandAsync(id, body.MinSeconds, body.MaxSeconds, ct)) return NotFound(id);
            Log(http).LogInformation("NILM API: signature {Id} duration band {Min}..{Max} s from {Ip}",
                id, body.MinSeconds?.ToString() ?? "-", body.MaxSeconds?.ToString() ?? "-", Ip(http));
            return await Current(nilm, id, ct);
        })));

        Write(api.MapPost("/signatures/{id:long}/split-by-duration", (long id, SplitByDurationRequest body, NilmService nilm, HttpContext http, CancellationToken ct) => Guard(async () =>
        {
            if (body.MaxSeconds is not { } max || max <= 0) return Error(StatusCodes.Status400BadRequest, "„maxSeconds” trebuie să fie un număr pozitiv.");
            var name = SignatureNaming.CleanName(body.Name);
            if (name.Length == 0) return Error(StatusCodes.Status400BadRequest, "Lipsește „name”.");
            if (UnknownCategory(body.Category) is { } bad) return bad;
            if (await nilm.GetSignatureAsync(id, ct) is not { } current) return NotFound(id);
            if (string.Equals(current.Signature.Name, name, StringComparison.OrdinalIgnoreCase))
                return Error(StatusCodes.Status400BadRequest, "Semnătura nouă trebuie să aibă alt nume decât cea împărțită.");
            var dryRun = body.DryRun ?? true;
            if (await nilm.SplitByDurationAsync(id, max, name, body.Category, dryRun, ct) is not { } r) return NotFound(id);
            if (!dryRun)
                Log(http).LogInformation("NILM API: signature {Id} split at {Max} s into {Name}: {Runs} runs moved, from {Ip}", id, max, r.TargetName, r.Runs, Ip(http));
            var source = r.Applied ? await nilm.GetSignatureAsync(id, ct) : current;
            var target = r.TargetId is { } tid ? await nilm.GetSignatureAsync(tid, ct) : null;
            return Results.Ok(new SplitByDurationDto(r.DryRun, r.Applied, r.SignatureId, r.TargetId, r.TargetName, r.Runs, r.Events, r.SkippedUserRuns, r.EnergyWh,
                ToDto(r.MovedDurations), ToDto(r.RemainingDurations), source is null ? null : ToDto(source), target is null ? null : ToDto(target)));
        })));

        Write(api.MapPost("/signatures/{id:long}/hide", (long id, NilmService nilm, HttpContext http, CancellationToken ct) =>
            SetHidden(id, true, nilm, http, ct)));
        Write(api.MapPost("/signatures/{id:long}/unhide", (long id, NilmService nilm, HttpContext http, CancellationToken ct) =>
            SetHidden(id, false, nilm, http, ct)));

        api.MapGet("/plugs", async (NilmService nilm, CancellationToken ct) =>
            Results.Ok((await nilm.ListPlugsAsync(ct)).Select(ToDto).ToList()));

        Write(api.MapPost("/plugs/{id:guid}/appliance", (Guid id, ApplianceRequest body, NilmService nilm, HttpContext http, CancellationToken ct) => Guard(async () =>
        {
            if (UnknownCategory(body.Category) is { } bad) return bad;
            if (await nilm.SetPlugApplianceAsync(id, body.Single ?? true, body.Name, body.Category, ct) is not { } row)
                return Error(StatusCodes.Status404NotFound, $"Priza {id} nu există (sau nu are rolul de priză).");
            Log(http).LogInformation("NILM API: plug {Alias} single appliance {Single} ({Name}, {Category}) from {Ip}",
                row.Alias, row.SingleAppliance, row.ApplianceName ?? "-", row.ApplianceCategory ?? "-", Ip(http));
            return Results.Ok(ToDto(row));
        })));

        Write(api.MapPost("/regroup", (NilmService nilm, HttpContext http, CancellationToken ct) => Guard(async () =>
        {
            var r = await nilm.RegroupNowAsync(ct);
            Log(http).LogInformation("NILM API: regroup from {Ip}: {Events} events, {Merged} merged, {Signatures} signatures",
                Ip(http), r.Events, r.Merged, r.Signatures);
            return Results.Ok(ToDto(r));
        })));

        Write(api.MapPost("/train", (NilmService nilm, HttpContext http, CancellationToken ct) => Guard(async () =>
        {
            var t = await nilm.TrainNowAsync(ct);
            Log(http).LogInformation("NILM API: train from {Ip}: trained {Trained}, {Events} labelled events, {Classes} classes",
                Ip(http), t.Trained, t.LabeledEvents, t.LabeledClasses);
            return Results.Ok(ToDto(t));
        })));

        return api;
    }

    /// <summary>A private (LAN) or loopback address: 10/8, 172.16/12, 192.168/16, 127/8, ::1 (IPv4-mapped IPv6 included).</summary>
    public static bool IsPrivate(IPAddress? ip)
    {
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>400 for a category that is not one of <see cref="ApplianceCategories.All"/>; null when absent or known.</summary>
    private static IResult? UnknownCategory(string? category) =>
        category is not null && ApplianceCategories.All.All(c => c.Key != category)
            ? Error(StatusCodes.Status400BadRequest,
                $"Categorie necunoscută „{category}”; valori: {string.Join(", ", ApplianceCategories.All.Select(c => c.Key))}.")
            : null;

    private static void Write(RouteHandlerBuilder route) => route.AddEndpointFilter(LanOnlyFilter);

    private static async ValueTask<object?> LanOnlyFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (IsPrivate(http.Connection.RemoteIpAddress)) return await next(context);
        Log(http).LogWarning("NILM API: {Method} {Path} refused from {Ip} (not a LAN address)", http.Request.Method, http.Request.Path.Value, Ip(http));
        return Error(StatusCodes.Status403Forbidden, "API-ul NILM acceptă modificări doar din rețeaua locală.");
    }

    private static async ValueTask<object?> TokenFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var token = http.RequestServices.GetRequiredService<IConfiguration>()[TokenSetting];
        if (string.IsNullOrEmpty(token) || TokenMatches(token, http.Request.Headers[TokenHeader].ToString()))
            return await next(context);
        return Error(StatusCodes.Status401Unauthorized, $"Lipsește sau e greșit antetul {TokenHeader}.");
    }

    private static bool TokenMatches(string expected, string given) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given));

    private static Task<IResult> SetHidden(long id, bool hidden, NilmService nilm, HttpContext http, CancellationToken ct) => Guard(async () =>
    {
        if (!await nilm.SetHiddenAsync(id, hidden, ct)) return NotFound(id);
        Log(http).LogInformation("NILM API: signature {Id} {Action} from {Ip}", id, hidden ? "hidden" : "unhidden", Ip(http));
        return await Current(nilm, id, ct);
    });

    /// <summary>The detection busy (backfill, nightly job, another regroup) → 503 with the service's message.</summary>
    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (NilmBusyException ex) { return Error(StatusCodes.Status503ServiceUnavailable, ex.Message); }
    }

    private static async Task<IResult> Current(NilmService nilm, long id, CancellationToken ct) =>
        await nilm.GetSignatureAsync(id, ct) is { } item ? Results.Ok(ToDto(item)) : NotFound(id);

    private static IResult NotFound(params long[] ids) => Error(StatusCodes.Status404NotFound,
        ids.Length == 1 ? $"Semnătura {ids[0]} nu există." : $"Semnătura {ids[0]} sau {ids[1]} nu există.");

    private static IResult Error(int status, string message) => Results.Json(new ErrorDto(message), statusCode: status);

    private static ILogger Log(HttpContext http) => http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(NilmApi).FullName!);

    private static string Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "?";

    private static SignatureDto ToDto(SignatureListItem i) => new(i.Signature.Id, i.Signature.Name, i.Signature.Category,
        i.Signature.LabelSource.ToString(), i.PlugAlias, i.PlugLinked, i.Signature.MeanDeltaP, i.Signature.MeanDeltaQ, i.Signature.MeanTransient,
        i.Signature.Count, i.Signature.Hidden, i.Signature.LastSeenUtc, i.Signature.MinRunSeconds, i.Signature.MaxRunSeconds, i.Signature.SplitFromId);

    private static SpreadDto ToDto(DurationSpread s) => new(s.Count, s.P10, s.P50, s.P90);

    private static RepetitionDto ToDto(RunRepetition r) => new(ToDto(r.Durations), ToDto(r.Intervals), r.Sessions, r.PulsesPerSessionP50, r.EnergyPerSessionWhP50);

    private static EventDto ToDto(MeterEvent e, string? signatureName) => new(e.Id, e.TsUtc, e.DeltaP, e.DeltaQ, e.DeltaPf, e.TransientRatio,
        e.TransientKnown, e.RiseSeconds, e.PowerBeforeW, e.Label, e.Source.ToString(), e.SignatureId, signatureName);

    private static ProfileDto ToDto(SignatureProfileView v)
    {
        var p = v.Profile;
        return new ProfileDto(p.Events, p.OnEvents, p.OffEvents, p.MeanDeltaP, p.StdDeltaP, p.MeanDeltaQ, p.MeanTransient, p.TransientKnown,
            p.StepPowerFactor, p.Runs, p.MedianRunSeconds, p.P10RunSeconds, p.P90RunSeconds, p.Hours, v.PlugAlias, v.PlugLinked, p.TopPlugShare,
            p.FirstSeenUtc, p.LastSeenUtc, p.Last7DaysWh, p.Repetition is { } r ? ToDto(r) : null);
    }

    private static PlugDto ToDto(PlugNilmRow p) => new(p.PlugId, p.Alias, p.SingleAppliance, p.ApplianceName, p.ApplianceCategory, p.State.ToString());

    private static TrainDto ToDto(NilmTrainResult t) => new(t.Trained, t.LabeledEvents, t.LabeledClasses,
        t.Info?.Classes ?? Array.Empty<string>(), t.Info?.Accuracy, t.Info?.Folds, t.Info?.Examples, t.Duration.TotalSeconds);

    private static RegroupDto ToDto(NilmRegroupResult r) => new(r.Events, r.Anchored, r.Clusters, r.Signatures, r.Moved, r.Merged,
        (r.Merges ?? Array.Empty<MergeStep>()).Select(m => new MergeStepDto(m.SurvivorId, m.AbsorbedId, m.Reason.ToString())).ToList(),
        r.Training is { } t ? ToDto(t) : null);
}
