namespace KasaMonitor.Core.Miele;

/// <summary>
/// Romanian texts and metric codes of the Miele values (single owner: UI, metrics, snapshots, ground truth).
/// <para>
/// Status and appliance type follow the official enums of the spec (swagger.json, <c>state.status</c>,
/// <c>ident.type</c>). The program phases are NOT documented by Miele: the numbers below come from the community
/// (Home Assistant <c>homeassistant/components/miele/const.py</c>, <c>ProgramPhaseWashingMachine</c>,
/// <c>ProgramPhaseTumbleDryer</c>, <c>ProgramPhaseWasherDryer</c>) and differ between appliance generations (e.g. the
/// spec's own example shows phase 1795 „Main wash”, which is in no list). They are a convenience, not a closed set:
/// an unknown phase falls back to the API's <c>value_localized</c> (English, Romanian is not offered) and then to
/// „faza &lt;raw&gt;”. Program names use <c>value_localized</c> (no verified list of program ids exists).
/// </para>
/// </summary>
public static class MieleTexts
{
    public const int TypeWashingMachine = 1;
    public const int TypeTumbleDryer = 2;
    public const int TypeDishwasher = 7;
    public const int TypeWasherDryer = 24;

    public const int StatusOff = 1;
    public const int StatusRunning = 5;
    public const int StatusEndProgrammed = 7;
    public const int StatusIdle = 10;
    public const int StatusNotConnected = 255;

    private static readonly Dictionary<int, (string Code, string Text)> Statuses = new()
    {
        [1] = ("off", "Oprit"),
        [2] = ("on", "Pornit"),
        [3] = ("programmed", "Programat"),
        [4] = ("waiting_to_start", "Așteaptă pornirea"),
        [5] = ("running", "În funcțiune"),
        [6] = ("pause", "Pauză"),
        [7] = ("end_programmed", "Program terminat"),
        [8] = ("failure", "Defecțiune"),
        [9] = ("programme_interrupted", "Program întrerupt"),
        [10] = ("idle", "Inactiv"),
        [11] = ("rinse_hold", "Oprire la clătire"),
        [12] = ("service", "Service"),
        [13] = ("superfreezing", "Superînghețare"),
        [14] = ("supercooling", "Superrăcire"),
        [15] = ("superheating", "Supraîncălzire"),
        [146] = ("supercooling_superfreezing", "Superrăcire și superînghețare"),
        [255] = ("not_connected", "Neconectat"),
    };

    private static readonly Dictionary<int, (string Code, string Text)> Types = new()
    {
        [TypeWashingMachine] = ("washing_machine", "Mașină de spălat"),
        [TypeTumbleDryer] = ("tumble_dryer", "Uscător"),
        [TypeDishwasher] = ("dishwasher", "Mașină de spălat vase"),
        [TypeWasherDryer] = ("washer_dryer", "Mașină de spălat cu uscător"),
    };

    private static readonly Dictionary<int, string> ProgramTypes = new()
    {
        [0] = "Normal",
        [1] = "Program propriu",
        [2] = "Automat",
        [3] = "Curățare / îngrijire",
    };

    private const string NotRunning = "nu rulează";

    // Community values (Home Assistant), see the class summary. Washing machine (and the washing part of a washer-dryer).
    private static readonly Dictionary<int, string> WashingPhases = new()
    {
        [0] = NotRunning, [256] = NotRunning, [65535] = NotRunning,
        [257] = "Prespălare", [259] = "Prespălare",
        [258] = "Înmuiere",
        [260] = "Spălare principală", [11004] = "Spălare principală",
        [261] = "Clătire", [11005] = "Clătire",
        [262] = "Oprire la clătire",
        [263] = "Curățare",
        [264] = "Răcire",
        [265] = "Evacuare",
        [266] = "Centrifugare", [11010] = "Centrifugare",
        [267] = "Anti-șifonare", [11029] = "Anti-șifonare",
        [268] = "Terminat", [11012] = "Terminat",
        [269] = "Aerisire",
        [270] = "Oprire pentru apretare",
        [271] = "Împrospătare și umezire",
        [272] = "Netezire cu abur", [295] = "Netezire cu abur",
        [279] = "Igienizare",
        [280] = "Uscare",
        [285] = "Dezinfectare",
        [11044] = "Pornire automată",
        [11047] = "Încărcare flexibilă",
        [11052] = "Pauză",
        [11053] = "Anulat",
    };

    // Tumble dryer (and the drying part of a washer-dryer).
    private static readonly Dictionary<int, string> DryerPhases = new()
    {
        [0] = NotRunning, [512] = NotRunning, [535] = NotRunning, [536] = NotRunning, [537] = NotRunning, [65535] = NotRunning,
        [513] = "Program în curs",
        [514] = "Uscare", [11018] = "Uscare",
        [515] = "Uscat pentru călcat la mașină",
        [516] = "Uscat pentru călcat de mână 2",
        [517] = "Uscat normal",
        [518] = "Uscat normal plus",
        [519] = "Răcire",
        [520] = "Uscat pentru călcat de mână 1",
        [521] = "Anti-șifonare", [11029] = "Anti-șifonare",
        [522] = "Terminat", [11012] = "Terminat",
        [523] = "Uscat extra",
        [524] = "Uscat pentru călcat de mână",
        [525] = "Uscare igienică",
        [526] = "Umezire",
        [527] = "Termo-centrifugare",
        [528] = "Uscare temporizată",
        [529] = "Aer cald",
        [530] = "Netezire cu abur",
        [531] = "Răcire confort", [11055] = "Răcire confort",
        [532] = "Clătire scame",
        [533] = "Clătiri",
        [534] = "Netezire",
        [538] = "Uscat ușor",
        [539] = "Răcire de siguranță",
        [11044] = "Pornire automată",
        [11054] = "PerfectDry activ",
    };

    public static string? Status(MieleValue? v) =>
        v is null ? null : v.Raw is { } r && Statuses.TryGetValue(r, out var s) ? s.Text : v.Localized ?? (v.Raw is { } raw ? "stare " + raw : null);

    /// <summary>Value of the <c>status</c> label of <c>miele_status_info</c> (spec enum name, snake case).</summary>
    public static string StatusCode(int? raw) =>
        raw is { } r && Statuses.TryGetValue(r, out var s) ? s.Code : raw is { } x ? "status_" + x : "unknown";

    public static string TypeText(int? raw, string? localized) =>
        raw is { } r && Types.TryGetValue(r, out var t) ? t.Text : localized ?? (raw is { } x ? "aparat tip " + x : "aparat Miele");

    /// <summary>Value of the <c>type</c> metric label.</summary>
    public static string TypeCode(int? raw) =>
        raw is { } r && Types.TryGetValue(r, out var t) ? t.Code : raw is { } x ? "type_" + x : "unknown";

    public static string? ProgramType(MieleValue? v) =>
        v is null ? null : v.Raw is { } r && ProgramTypes.TryGetValue(r, out var t) ? t : v.Localized ?? (v.Raw is { } raw ? "tip " + raw : null);

    /// <summary>The program name from the API (<c>value_localized</c>), else „program &lt;raw&gt;”.</summary>
    public static string? Program(MieleValue? v) =>
        v is null ? null : v.Localized ?? (v.Raw is { } raw ? "program " + raw : null);

    /// <summary>
    /// The phase text: community dictionary of the appliance type (a washer-dryer looks in both), then the API's
    /// <c>value_localized</c>, then „faza &lt;raw&gt;”. Null when the phase is unknown altogether.
    /// </summary>
    public static string? Phase(int? typeRaw, MieleValue? v)
    {
        if (v is null) return null;
        if (v.Raw is { } raw && KnownPhase(typeRaw, raw) is { } known) return known;
        return v.Localized ?? (v.Raw is { } r ? "faza " + r : null);
    }

    public static string? KnownPhase(int? typeRaw, int raw)
    {
        switch (typeRaw)
        {
            case TypeWashingMachine:
                return WashingPhases.GetValueOrDefault(raw);
            case TypeTumbleDryer:
                return DryerPhases.GetValueOrDefault(raw);
            case TypeWasherDryer:
                return WashingPhases.GetValueOrDefault(raw) ?? DryerPhases.GetValueOrDefault(raw);
            default:
                return null;
        }
    }

    /// <summary>The phase means the appliance is not running a program (0, 256, 512, 65535 …).</summary>
    public static bool IsIdlePhase(string? phaseText) => phaseText == NotRunning;

    /// <summary>
    /// Informative only: which load a phase is expected to switch on (heating element in pre-wash/main wash, motor in
    /// the spin, drain pump), to read the NILM signatures named „&lt;aparat&gt; · &lt;fază&gt;”. Not a rule anything depends on.
    /// </summary>
    public static string? ExpectedLoad(string? phaseText) => phaseText switch
    {
        "Prespălare" or "Spălare principală" or "Igienizare" or "Dezinfectare" => "rezistență (încălzirea apei)",
        "Centrifugare" => "motor (centrifugare)",
        "Evacuare" => "pompă (evacuare)",
        _ => null,
    };
}
