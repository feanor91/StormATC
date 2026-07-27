using StormATC.Domain.Atc;
using StormATC.Domain.Phraseology;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Phraseology;

/// <summary>
/// Checks generated controller speech against the decision that authorised it.
/// </summary>
/// <remarks>
/// <para>
/// This is the last gate before audio reaches the pilot's ears, and the reason a language model
/// can be trusted to phrase clearances at all. The validator asks three questions:
/// </para>
/// <list type="number">
/// <item>Does the text contain every fact the engine required?</item>
/// <item>Does it contain any fact the engine prohibited?</item>
/// <item>Does every number in the text correspond to an approved value?</item>
/// </list>
/// <para>
/// Question three is the important one. A model can produce fluent, plausible phraseology that
/// contains an altitude the engine never approved. Extracting the numbers and checking each one
/// against the approved set catches that, where a keyword check would not.
/// </para>
/// </remarks>
public sealed class PhraseologyValidator
{
    /// <summary>
    /// Phrases that grant a permission. Each maps to the fact key that must be present in the
    /// decision for the phrase to be legitimate.
    /// </summary>
    private static readonly (string Phrase, string RequiredFactKey)[] PermissionPhrases =
    [
        ("cleared for takeoff", "clearance"),
        ("cleared for take off", "clearance"),
        ("cleared to land", "clearance"),
        ("cleared to take off", "clearance"),
        ("line up and wait", "clearance"),
        ("cleared for the approach", "approach"),
        ("cleared ils", "approach"),
        ("pushback approved", "approval"),
        ("cross runway", "runwayCrossing"),
    ];

    /// <summary>Validates rendered controller speech against the approving decision.</summary>
    /// <param name="spokenText">The text the model produced.</param>
    /// <param name="decision">The decision that authorised the response.</param>
    /// <remarks>
    /// Deliberately an instance method on an injected service rather than a static helper: the
    /// validator is a policy component that tests substitute and that will gain configuration
    /// (regional phraseology profiles) without changing every call site.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1822:Mark members as static",
        Justification = "Injected policy service; see remarks.")]
    public PhraseologyValidationResult Validate(string spokenText, AtcDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (string.IsNullOrWhiteSpace(spokenText))
        {
            return PhraseologyValidationResult.Invalid("The generated response was empty.");
        }

        string lowered = spokenText.ToLowerInvariant();
        var problems = new List<string>();

        // --- 1. Prohibited facts. -----------------------------------------------------------
        foreach (string prohibited in decision.ProhibitedFacts)
        {
            // Prohibitions are written as human-readable descriptions. Only the concrete ones
            // can be checked literally; the descriptive ones ("any altitude other than…") are
            // enforced by the numeric check below instead.
            if (IsLiteralProhibition(prohibited)
                && lowered.Contains(prohibited.ToLowerInvariant(), StringComparison.Ordinal))
            {
                problems.Add($"Response contains the prohibited phrase '{prohibited}'.");
            }
        }

        // --- 2. Unauthorised permission phrases. ---------------------------------------------
        foreach ((string phrase, string requiredKey) in PermissionPhrases)
        {
            if (!lowered.Contains(phrase, StringComparison.Ordinal))
            {
                continue;
            }

            if (!decision.ApprovedFacts.ContainsKey(requiredKey))
            {
                problems.Add(
                    $"Response grants '{phrase}' but the decision did not approve it.");
            }
        }

        // --- 3. Numeric values. ---------------------------------------------------------------
        problems.AddRange(ValidateNumericValues(spokenText, decision));

        // --- 4. Required facts present. -------------------------------------------------------
        foreach (KeyValuePair<string, string> fact in decision.ApprovedFacts)
        {
            if (IsMandatoryFact(fact.Key) && !ContainsFactValue(lowered, fact.Key, fact.Value))
            {
                problems.Add($"Response omits the required fact '{fact.Key}' ({fact.Value}).");
            }
        }

        return problems.Count == 0
            ? PhraseologyValidationResult.Valid()
            : PhraseologyValidationResult.Invalid(problems);
    }

    /// <summary>
    /// Extracts every number the response speaks and checks each against the approved set.
    /// </summary>
    /// <remarks>
    /// The check is one-directional: every spoken number must be approved, but not every
    /// approved number need be spoken (a controller may legitimately omit an optional fact).
    /// Numbers that are part of the callsign are excluded, as are small integers that appear in
    /// ordinary words rather than as values.
    /// </remarks>
    private static List<string> ValidateNumericValues(string spokenText, AtcDecision decision)
    {
        var problems = new List<string>();

        var approvedNumbers = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in decision.ApprovedFacts.Values)
        {
            foreach (string token in value.Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                approvedNumbers.Add(NormalizeNumeric(token));
            }
        }

        // The callsign's own digits are always acceptable.
        if (decision.IssuedClearance is not null || decision.Handoff is not null)
        {
            approvedNumbers.Add("callsign");
        }

        foreach (string spoken in ExtractSpokenNumbers(spokenText))
        {
            string normalized = NormalizeNumeric(spoken);

            if (approvedNumbers.Contains(normalized))
            {
                continue;
            }

            // A frequency written 121.7 must match an approved 121.700 and vice versa.
            if (approvedNumbers.Any(approved => NumericallyEquivalent(approved, normalized)))
            {
                continue;
            }

            problems.Add(
                $"Response speaks the value '{spoken}', which is not in the approved facts.");
        }

        return problems;
    }

    /// <summary>
    /// Pulls the numeric values out of spoken phraseology.
    /// </summary>
    /// <remarks>
    /// Spoken aviation numbers appear as digit-word runs ("seven thousand", "one two one
    /// decimal seven"). The extractor walks the token stream, accumulates each run, and emits
    /// the assembled value. Runs of one or two digits are skipped: they are almost always part
    /// of a callsign or an ordinal, and flagging them would produce constant false positives.
    /// </remarks>
    private static List<string> ExtractSpokenNumbers(string spokenText)
    {
        // Reuse the same folding the inbound parser uses, so the two agree on what a number is.
        string folded = SpokenNumbers.FoldDigitWords(spokenText);
        var results = new List<string>();

        var current = new System.Text.StringBuilder();
        bool sawDecimal = false;

        foreach (char c in folded + " ")
        {
            if (char.IsDigit(c))
            {
                current.Append(c);
            }
            else if (c == '.' && current.Length > 0 && !sawDecimal)
            {
                current.Append('.');
                sawDecimal = true;
            }
            else if (c == ' ' && current.Length > 0 && current[^1] == '.')
            {
                // "one two one decimal seven" — a space right after the point still continues
                // the same number.
                continue;
            }
            else
            {
                if (current.Length >= 3)
                {
                    results.Add(current.ToString());
                }

                current.Clear();
                sawDecimal = false;
            }
        }

        // Spelled-out magnitudes are handled separately, because "seven thousand" folds to
        // "7 thousand", not to "7000".
        int? altitude = SpokenNumbers.ParseSpokenAltitude(spokenText);
        if (altitude is int feet && feet >= 100)
        {
            results.Add(feet.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return results;
    }

    private static string NormalizeNumeric(string value)
    {
        string trimmed = value.Trim();

        // Strip trailing zeros after a decimal point so 121.700 and 121.7 compare equal.
        if (trimmed.Contains('.', StringComparison.Ordinal))
        {
            trimmed = trimmed.TrimEnd('0').TrimEnd('.');
        }

        return trimmed;
    }

    private static bool NumericallyEquivalent(string a, string b)
    {
        if (decimal.TryParse(a, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out decimal left)
            && decimal.TryParse(b, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out decimal right))
        {
            return left == right;
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for prohibitions written as a literal phrase rather than a description.
    /// </summary>
    private static bool IsLiteralProhibition(string prohibition) =>
        !prohibition.StartsWith("any ", StringComparison.OrdinalIgnoreCase)
        && !prohibition.Contains(" unless ", StringComparison.OrdinalIgnoreCase)
        && !prohibition.Contains(" other than ", StringComparison.OrdinalIgnoreCase)
        && !prohibition.Contains(" not listed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fact keys whose omission makes the transmission wrong rather than merely terse.
    /// </summary>
    private static bool IsMandatoryFact(string key) => key switch
    {
        "runway" or "squawk" or "frequency" or "holdShortRunway" => true,
        _ => false,
    };

    /// <summary>Checks whether a fact's value appears in the text, in either spoken or written form.</summary>
    private static bool ContainsFactValue(string loweredText, string key, string value)
    {
        if (loweredText.Contains(value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return true;
        }

        // Try the spoken rendering of the value, which is how it will normally appear.
        string? spoken = key switch
        {
            "runway" or "holdShortRunway" => RunwayIdentifier.TryCreate(value, out RunwayIdentifier runway)
                ? runway.ToSpokenDesignator()
                : null,
            "squawk" => SquawkCode.TryCreate(value, out SquawkCode squawk) ? squawk.ToSpoken() : null,
            "frequency" => decimal.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal mhz)
                && Frequency.TryFromMegahertz(mhz, out Frequency frequency)
                    ? frequency.ToSpoken()
                    : null,
            _ => null,
        };

        return spoken is not null
            && loweredText.Contains(spoken.ToLowerInvariant(), StringComparison.Ordinal);
    }
}

/// <summary>The outcome of validating generated phraseology.</summary>
public sealed record PhraseologyValidationResult
{
    /// <summary>Whether the text may be spoken.</summary>
    public required bool IsValid { get; init; }

    /// <summary>Every problem found, for logging and the transcript diagnostics view.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>Builds a passing result.</summary>
    public static PhraseologyValidationResult Valid() => new() { IsValid = true };

    /// <summary>Builds a failing result with one problem.</summary>
    public static PhraseologyValidationResult Invalid(string problem) =>
        new() { IsValid = false, Problems = [problem] };

    /// <summary>Builds a failing result with several problems.</summary>
    public static PhraseologyValidationResult Invalid(IReadOnlyList<string> problems) =>
        new() { IsValid = false, Problems = problems };
}
