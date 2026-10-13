using System.Text;
using StormATC.Domain.Intents;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Phraseology;

/// <summary>
/// French radiotelephony, following the DSNA "Manuel de phraséologie à l'usage de la
/// circulation aérienne générale".
/// </summary>
/// <remarks>
/// <para>
/// Element order and vocabulary follow the manual's French column: "roulez point d'attente
/// piste 27 via B", "alignez-vous et attendez", "piste 27, autorisé décollage, vent 280 degrés
/// 10 nœuds", "contactez Paris 132,0", "correct" for an accepted readback and "négatif" for a
/// wrong one. Numbers use the French conventions in <see cref="FrenchSpokenNumbers"/>.
/// </para>
/// <para>
/// Where the manual gives no example for a sentence StormATC needs (progressive taxi guidance,
/// for instance), the wording stays as close as possible to the manual's verbs and structure.
/// </para>
/// </remarks>
public sealed class FrenchAtcPhraseology : IAtcPhraseology
{
    /// <summary>The shared instance. The class is stateless.</summary>
    public static FrenchAtcPhraseology Instance { get; } = new();

    private FrenchAtcPhraseology()
    {
    }

    /// <summary>
    /// Chooses between equivalent wordings of courtesy and cadence words. Defaults to the first
    /// wording so output is reproducible; the desktop app switches to a random picker.
    /// </summary>
    public IVariantPicker VariantPicker { get; set; } = FirstVariantPicker.Instance;

    /// <summary>Returns one of several equivalent wordings. The first is the reference.</summary>
    private string OneOf(params string[] variants) =>
        variants[Math.Clamp(VariantPicker.Pick(variants.Length), 0, variants.Length - 1)];

    /// <summary>"Reçu" or "bien reçu": the French "roger".</summary>
    private string Received() => OneOf("reçu", "bien reçu");

    /// <inheritdoc />
    public PhraseologyLanguage Language => PhraseologyLanguage.French;

    /// <inheritdoc />
    public string StationCalling => "Station appelante";

    private const string UnknownAircraft = "Aéronef";

    // ---------------------------------------------------------------------------------
    // Values
    // ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public string SpeakCallsign(Callsign callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);

        if (callsign.Telephony is null)
        {
            // A registration such as F-GABC is spelled with the ICAO alphabet.
            return FrenchSpokenNumbers.SpellOut(callsign.Normalized);
        }

        string telephonyWord = callsign.Telephony.Length <= 2
            ? FrenchSpokenNumbers.SpellOut(callsign.Telephony)
            : string.Concat(
                char.ToUpperInvariant(callsign.Telephony[0]),
                callsign.Telephony[1..].ToLowerInvariant());

        return callsign.NumericSuffix is null
            ? telephonyWord
            : $"{telephonyWord} {FrenchSpokenNumbers.DigitsIndividually(callsign.NumericSuffix)}";
    }

    /// <inheritdoc />
    public string SpeakRunway(RunwayIdentifier runway) => $"piste {SpeakRunwayDesignator(runway)}";

    /// <inheritdoc />
    public string SpeakRunwayDesignator(RunwayIdentifier runway)
    {
        string side = runway.Side switch
        {
            RunwaySide.Left => " gauche",
            RunwaySide.Center => " centre",
            RunwaySide.Right => " droite",
            _ => string.Empty,
        };

        return $"{FrenchSpokenNumbers.RunwayNumber(runway.Number)}{side}";
    }

    /// <inheritdoc />
    public string SpeakAltitude(Altitude altitude) => altitude.IsFlightLevel
        ? $"niveau {FrenchSpokenNumbers.Cardinal(altitude.FlightLevelNumber)}"
        : $"altitude {FrenchSpokenNumbers.Cardinal(altitude.Feet)} pieds";

    /// <inheritdoc />
    public string SpeakHeading(Heading heading) => FrenchSpokenNumbers.Heading(heading.Degrees);

    /// <inheritdoc />
    public string SpeakFrequency(Frequency frequency) => FrenchSpokenNumbers.Frequency(frequency.Megahertz);

    /// <inheritdoc />
    public string SpeakSquawk(SquawkCode squawk) => FrenchSpokenNumbers.Squawk(squawk.Digits);

    /// <inheritdoc />
    public string SpellIcao(string icao) => FrenchSpokenNumbers.SpellOut(icao);

    /// <inheritdoc />
    public string SpellIdentifier(string identifier) => FrenchSpokenNumbers.SpellOut(identifier);

    /// <inheritdoc />
    public string Taxiways(IReadOnlyList<string> taxiways)
    {
        ArgumentNullException.ThrowIfNull(taxiways);

        var parts = new List<string>(taxiways.Count);
        foreach (string taxiway in taxiways)
        {
            // Same rule as the English profile: letter and alphanumeric designators are
            // spelled, named surfaces are spoken as words.
            bool isDesignator =
                taxiway.Length == 1
                || taxiway.Any(char.IsDigit)
                    && taxiway.All(char.IsLetterOrDigit);
            parts.Add(isDesignator ? FrenchSpokenNumbers.SpellOut(taxiway) : taxiway);
        }

        return string.Join(", ", parts);
    }

    /// <inheritdoc />
    public string ControllerName(ControllerPosition position) => position switch
    {
        ControllerPosition.Atis => "ATIS",
        ControllerPosition.ClearanceDelivery => "Prévol",
        ControllerPosition.Ground => "Sol",
        ControllerPosition.Tower => "Tour",
        ControllerPosition.Departure => "Départ",
        ControllerPosition.Center => "Contrôle",
        ControllerPosition.Approach => "Approche",
        _ => "Contrôle",
    };

    // ---------------------------------------------------------------------------------
    // Clearances and instructions
    // ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public string IfrClearance(Callsign callsign, Clearance clearance)
    {
        ArgumentNullException.ThrowIfNull(clearance);

        var builder = Phrase(callsign);

        builder.Add(clearance.ClearedToDestination is { Length: > 0 } destination
            ? $"autorisé {SpellIcao(destination)}"
            : "autorisé selon le plan de vol déposé");

        if (clearance.DepartureProcedure is { Length: > 0 } sid)
        {
            builder.Add($"départ {sid}");
        }
        else if (clearance.ClearedRoute is { Length: > 0 })
        {
            builder.Add("via la route du plan de vol");
        }

        if (clearance.Runway is RunwayIdentifier runway)
        {
            builder.Add(SpeakRunway(runway));
        }

        if (clearance.InitialAltitude is Altitude initial)
        {
            builder.Add(ReadbackItemInitialAltitude(initial));
        }

        if (clearance.Squawk is SquawkCode squawk)
        {
            builder.Add(ReadbackItemSquawk(squawk));
        }

        if (clearance.NextFrequency is Frequency frequency)
        {
            builder.Add(ReadbackItemDepartureFrequency(frequency));
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public string ClearanceThenPushbackNote => " Collationnez la clairance, puis demandez le repoussage.";

    /// <inheritdoc />
    public string ReadbackCorrect(Callsign callsign, Frequency? nextFrequency, ControllerPosition? nextController)
    {
        var builder = Phrase(callsign).Add(OneOf("correct", "collationnement correct"));

        if (nextFrequency is Frequency frequency)
        {
            string position = nextController is ControllerPosition controller
                ? ControllerName(controller)
                : "la fréquence suivante";
            builder.Add($"contactez {position} {SpeakFrequency(frequency)}");
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public string ReadbackCorrectPlain(Callsign? callsign) => callsign is null
        ? "Collationnement correct."
        : Phrase(callsign).Add(OneOf("correct", "collationnement correct")).Build();

    /// <inheritdoc />
    public string PushbackApproval(Callsign callsign, string? pushbackDirection)
    {
        var builder = Phrase(callsign).Add("repoussage approuvé");

        string? direction = pushbackDirection switch
        {
            null or "" => null,
            "tail left" => "queue vers la gauche",
            "tail right" => "queue vers la droite",
            _ => pushbackDirection,
        };

        if (direction is not null)
        {
            builder.Add(direction);
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public string TaxiClearance(Callsign callsign, Clearance clearance)
    {
        ArgumentNullException.ThrowIfNull(clearance);

        var builder = Phrase(callsign);

        builder.Add(clearance.Runway is RunwayIdentifier runway
            ? $"roulez point d'attente {SpeakRunway(runway)}"
            : "roulez point d'attente");

        if (clearance.TaxiRoute.Count > 0)
        {
            builder.Add($"via {Taxiways(clearance.TaxiRoute)}");
        }

        if (clearance.HoldShortOfRunway is RunwayIdentifier holdShort)
        {
            builder.Add(ReadbackItemHoldShort(holdShort));
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public string UnroutedTaxiClearance(Callsign callsign, RunwayIdentifier runway) =>
        Phrase(callsign)
            .Add($"roulez point d'attente {SpeakRunway(runway)}")
            .Add("itinéraire de roulage détaillé non disponible")
            .Add(ReadbackItemHoldShort(runway))
            .Build();

    /// <inheritdoc />
    public string TaxiToStandClearance(Callsign callsign, Clearance clearance)
    {
        ArgumentNullException.ThrowIfNull(clearance);

        var builder = Phrase(callsign);

        builder.Add(clearance.TaxiDestination is { Length: > 0 } stand
            ? $"roulez parking {SpellIdentifier(stand)}"
            : "roulez parking");

        if (clearance.TaxiRoute.Count > 0)
        {
            builder.Add($"via {Taxiways(clearance.TaxiRoute)}");
        }

        if (clearance.HoldShortOfRunway is RunwayIdentifier holdShort)
        {
            builder.Add(ReadbackItemHoldShort(holdShort));
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public string TaxiToStandNoRoute(Callsign callsign, string stand) =>
        Phrase(callsign)
            .Add($"roulez parking {SpellIdentifier(stand)}")
            .Add("pas d'itinéraire publié")
            .Build();

    /// <inheritdoc />
    public string ProgressiveTaxiInstruction(ProgressiveTaxiGuidance guidance)
    {
        ArgumentNullException.ThrowIfNull(guidance);

        string current = guidance.CurrentTaxiway is { Length: > 0 } taxiway
            ? Taxiways([taxiway])
            : "l'itinéraire autorisé";
        string next = guidance.NextTaxiway is { Length: > 0 } nextTaxiway
            ? Taxiways([nextTaxiway])
            : string.Empty;

        return guidance.Step switch
        {
            ProgressiveTaxiStep.ToHoldingPoint when guidance.Runway is RunwayIdentifier runway =>
                $"continuez sur {current} jusqu'au point d'attente {SpeakRunway(runway)}, "
                + ReadbackItemHoldShort(runway),
            ProgressiveTaxiStep.TurnLeft =>
                $"continuez sur {current}, à la prochaine intersection tournez à gauche sur {next}",
            ProgressiveTaxiStep.TurnRight =>
                $"continuez sur {current}, à la prochaine intersection tournez à droite sur {next}",
            ProgressiveTaxiStep.Straight =>
                $"continuez sur {current}, puis tout droit sur {next}",
            _ => $"continuez sur {current} jusqu'à {guidance.Destination ?? "la limite de clairance"}",
        };
    }

    /// <inheritdoc />
    public string HoldPositionConfirm(Callsign callsign) =>
        Phrase(callsign).Add("maintenez position").Add("confirmez votre position").Build();

    /// <inheritdoc />
    public string LineUpAndWait(Callsign callsign, RunwayIdentifier runway) =>
        Phrase(callsign).Add($"alignez-vous et attendez {SpeakRunway(runway)}").Build();

    /// <inheritdoc />
    public string TakeoffClearance(
        Callsign callsign,
        RunwayIdentifier runway,
        Heading? windDirection,
        int? windSpeedKnots,
        Heading? initialHeading)
    {
        var builder = Phrase(callsign);

        if (initialHeading is Heading heading)
        {
            builder.Add($"cap {SpeakHeading(heading)}");
        }

        builder.Add(SpeakRunway(runway));
        builder.Add("autorisé décollage");

        // Wind is only spoken when a weather source actually supplied it. Never invented.
        if (Wind(windDirection, windSpeedKnots) is string wind)
        {
            builder.Add(wind);
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public string LandingClearance(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots)
    {
        var builder = Phrase(callsign).Add(SpeakRunway(runway)).Add("autorisé atterrissage");

        if (Wind(windDirection, windSpeedKnots) is string wind)
        {
            builder.Add(wind);
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public string ApproachClearance(Callsign callsign, string approachName, RunwayIdentifier runway) =>
        Phrase(callsign).Add($"autorisé approche {approachName} {SpeakRunway(runway)}").Build();

    /// <inheritdoc />
    public string Handoff(Callsign callsign, ControllerPosition toController, Frequency frequency) =>
        Phrase(callsign)
            .Add($"contactez {ControllerName(toController)} {SpeakFrequency(frequency)}")
            .Add(OneOf("au revoir", "bonne journée", string.Empty))
            .Build();

    /// <inheritdoc />
    public string CheckInAcknowledgement(
        Callsign callsign,
        ControllerPosition controller,
        Altitude? clearedAltitude,
        bool radarContact)
    {
        var builder = Phrase(callsign).Add($"{ControllerName(controller)}, bonjour");

        if (radarContact)
        {
            builder.Add("identifié");
        }

        if (clearedAltitude is Altitude altitude)
        {
            builder.Add($"montez {SpeakAltitude(altitude)}");
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public string ClimbInstruction(Callsign callsign, Altitude altitude) =>
        Phrase(callsign).Add($"montez {SpeakAltitude(altitude)}").Build();

    /// <inheritdoc />
    public string DescentInstruction(Callsign callsign, Altitude altitude) =>
        Phrase(callsign).Add($"descendez {SpeakAltitude(altitude)}").Build();

    /// <inheritdoc />
    public string HeadingInstruction(Callsign callsign, Heading heading) =>
        Phrase(callsign).Add($"cap {SpeakHeading(heading)}").Build();

    /// <inheritdoc />
    public string DirectToApproval(Callsign callsign, string waypoint) =>
        Phrase(callsign).Add($"procédez direct {SpellIdentifier(waypoint)}").Build();

    /// <inheritdoc />
    public string DeviationApproved(Callsign callsign) =>
        Phrase(callsign)
            .Add("évitement approuvé")
            .Add("rappelez en mesure de reprendre votre navigation")
            .Build();

    /// <inheritdoc />
    public string GoAroundInstruction(Callsign callsign, Altitude? climbTo, Heading? heading)
    {
        var builder = Phrase(callsign).Add("remettez les gaz");

        if (climbTo is Altitude altitude)
        {
            builder.Add($"montez {SpeakAltitude(altitude)}");
        }

        if (heading is Heading turn)
        {
            builder.Add($"cap {SpeakHeading(turn)}");
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public string VacateInstruction(Callsign callsign, string? exitTaxiway) =>
        Phrase(callsign)
            .Add(exitTaxiway is { Length: > 0 } exit
                ? $"dégagez par {SpellIdentifier(exit)}"
                : OneOf("dégagez dès que possible", "dégagez la piste dès que possible"))
            .Build();

    /// <inheritdoc />
    public string VacateWhenAbleNoCallsign => "Dégagez la piste dès que possible.";

    // ---------------------------------------------------------------------------------
    // Conversation
    // ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public string GoAhead(Callsign callsign, ControllerPosition controller) =>
        Phrase(callsign).Add(ControllerName(controller)).Add(OneOf("transmettez", "je vous écoute")).Build();

    /// <inheritdoc />
    public string Acknowledgement(Callsign callsign) => Phrase(callsign).Add(Received()).Build();

    /// <inheritdoc />
    public string ReportPointInstruction(Callsign callsign, string pointName) =>
        Phrase(callsign).Add(Received()).Add($"rappelez {pointName}").Build();

    /// <inheritdoc />
    public string CircuitJoinInstruction(Callsign callsign, RunwayIdentifier runway, CircuitSide? side) =>
        Phrase(callsign)
            .Add($"entrez vent arrière {Hand(side)}{SpeakRunway(runway)}")
            .Add("rappelez vent arrière")
            .Build();

    /// <inheritdoc />
    public string CircuitReportInstruction(Callsign callsign, CircuitLeg leg, RunwayIdentifier runway, CircuitSide? side) =>
        Phrase(callsign)
            .Add(leg == CircuitLeg.Final
                ? $"rappelez finale {SpeakRunway(runway)}"
                : $"rappelez base {Hand(side)}{SpeakRunway(runway)}")
            .Build();

    /// <inheritdoc />
    public string TouchAndGoClearance(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots)
    {
        PhraseBuilder builder = Phrase(callsign).Add(SpeakRunway(runway)).Add("autorisé toucher");

        if (Wind(windDirection, windSpeedKnots) is string wind)
        {
            builder.Add(wind);
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public string LeaveFrequencyAcknowledgement(Callsign callsign) =>
        Phrase(callsign).Add(Received()).Add(OneOf("au revoir", "bonne journée")).Build();

    private static string Hand(CircuitSide? side) => side switch
    {
        CircuitSide.Left => "main gauche ",
        CircuitSide.Right => "main droite ",
        _ => string.Empty,
    };

    /// <inheritdoc />
    public string RogerPlain => Sentence(Received());

    /// <inheritdoc />
    public string RogerSayIntentions(Callsign callsign) =>
        Phrase(callsign).Add(Received()).Add("annoncez vos intentions").Build();

    /// <inheritdoc />
    public string RogerHoldShort(Callsign callsign) =>
        Phrase(callsign).Add(Received()).Add("maintenez avant la piste").Build();

    /// <inheritdoc />
    public string SayAgain(Callsign? callsign)
    {
        string request = OneOf("répétez", "répétez votre message");
        return callsign is null
            ? $"{StationCalling}, {request}."
            : Phrase(callsign).Add(request).Build();
    }

    /// <inheritdoc />
    public string SayRequestedLevel(Callsign callsign) =>
        Phrase(callsign).Add("annoncez le niveau demandé").Build();

    /// <inheritdoc />
    public string SayAgainWaypoint(Callsign callsign) =>
        Phrase(callsign).Add("répétez le point demandé").Build();

    /// <inheritdoc />
    public string RadioCheckReply(Callsign callsign, ControllerPosition controller) =>
        Phrase(callsign).Add(ControllerName(controller)).Add(OneOf("je vous reçois cinq sur cinq", "je vous reçois très bien")).Build();

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately measured and non-committal, like the English profile: StormATC is
    /// entertainment software and must not imply that any real assistance is being provided.
    /// </remarks>
    public string EmergencyAcknowledgement(Callsign callsign, bool isMayday) =>
        Phrase(callsign)
            .Add(isMayday ? "mayday reçu" : "pan pan reçu")
            .Add("annoncez vos intentions")
            .Build();

    /// <inheritdoc />
    public string Unable(Callsign callsign, UnableReason reason) =>
        Phrase(callsign).Add("impossible").Add(ReasonText(reason)).Build();

    /// <inheritdoc />
    public string UnableMaintain(Callsign callsign, Altitude altitude) =>
        Phrase(callsign).Add("impossible").Add($"maintenez {SpeakAltitude(altitude)}").Build();

    /// <inheritdoc />
    public string ContactController(Callsign? callsign, ControllerPosition controller) =>
        $"{SpokenOr(callsign, StationCalling)}, contactez {ControllerName(controller)}.";

    /// <inheritdoc />
    public string FrequencyInformation(Callsign callsign, ControllerPosition controller, Frequency frequency) =>
        Phrase(callsign).Add($"fréquence {ControllerName(controller)} {SpeakFrequency(frequency)}").Build();

    /// <inheritdoc />
    public string NoSeparateFrequency(Callsign callsign, ControllerPosition controller) =>
        Phrase(callsign)
            .Add($"pas de fréquence {ControllerName(controller)} distincte")
            .Add("restez sur cette fréquence")
            .Build();

    /// <inheritdoc />
    public string FrequencyUnavailable(Callsign callsign, ControllerPosition controller) =>
        Phrase(callsign)
            .Add($"fréquence {ControllerName(controller)} non disponible")
            .Add("restez sur cette fréquence")
            .Build();

    /// <inheritdoc />
    public string WrongHoldingPoint(Callsign callsign, RunwayIdentifier assignedRunway) =>
        Phrase(callsign)
            .Add("négatif")
            .Add($"votre piste de départ est la {SpeakRunwayDesignator(assignedRunway)}")
            .Add("confirmez votre position")
            .Build();

    /// <inheritdoc />
    public string HoldShortTrafficOnRunway(Callsign callsign, RunwayIdentifier runway) =>
        Phrase(callsign).Add(ReadbackItemHoldShort(runway)).Add("trafic sur la piste").Build();

    /// <inheritdoc />
    public string WrongApproachRunway(Callsign callsign, RunwayIdentifier clearedRunway) =>
        Phrase(callsign)
            .Add("négatif")
            .Add($"vous êtes autorisé pour l'approche {SpeakRunway(clearedRunway)}")
            .Add("confirmez")
            .Build();

    /// <inheritdoc />
    public string ContinueApproachTrafficOnRunway(Callsign callsign) =>
        Phrase(callsign).Add("continuez l'approche").Add("trafic sur la piste").Build();

    /// <inheritdoc />
    public string HoldShortViolation(Callsign? callsign, RunwayIdentifier runway) =>
        $"{SpokenOr(callsign, UnknownAircraft)}, vous deviez maintenir avant {SpeakRunway(runway)}, "
        + "dégagez la piste immédiatement.";

    /// <inheritdoc />
    public string NoFlightPlanToBegin =>
        "Station appelante, aucun plan de vol chargé. Importez un plan de vol pour commencer.";

    /// <inheritdoc />
    public string NoFlightPlanFirst =>
        "Station appelante, aucun plan de vol chargé. Importez d'abord un plan de vol.";

    /// <inheritdoc />
    public string ReadbackReminder(Callsign? callsign) =>
        $"{SpokenOr(callsign, StationCalling)}, {OneOf("attendez, collationnez votre clairance", "collationnez votre clairance")}.";

    // ---------------------------------------------------------------------------------
    // Readbacks
    // ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public string ReadbackItemInitialAltitude(Altitude altitude) => $"montez {SpeakAltitude(altitude)}";

    /// <inheritdoc />
    public string ReadbackItemSquawk(SquawkCode squawk) => $"transpondeur {SpeakSquawk(squawk)}";

    /// <inheritdoc />
    public string ReadbackItemDepartureFrequency(Frequency frequency) =>
        $"fréquence départ {SpeakFrequency(frequency)}";

    /// <inheritdoc />
    public string ReadbackItemTaxiRoute(IReadOnlyList<string> taxiways) => $"via {Taxiways(taxiways)}";

    /// <inheritdoc />
    public string ReadbackItemHoldShort(RunwayIdentifier runway) => $"maintenez avant {SpeakRunway(runway)}";

    /// <inheritdoc />
    public string ReadbackItemAltitudeChange(Altitude altitude, bool isClimb) =>
        $"{(isClimb ? "montez" : "descendez")} {SpeakAltitude(altitude)}";

    /// <inheritdoc />
    public string ReadbackItemApproach(string approachName) => $"approche {approachName}";

    /// <inheritdoc />
    public string ReadbackCorrection(Callsign? callsign, bool hasIncorrectValue, IReadOnlyList<string> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (parts.Count == 0)
        {
            return SayAgainReadback(callsign);
        }

        string lead = hasIncorrectValue ? "négatif" : "collationnement incomplet";
        return $"{SpokenOr(callsign, UnknownAircraft)}, {lead}, {string.Join(", ", parts)}.";
    }

    /// <inheritdoc />
    public string SayAgainReadback(Callsign? callsign) =>
        $"{SpokenOr(callsign, UnknownAircraft)}, {OneOf("répétez votre collationnement", "répétez le collationnement")}.";

    /// <inheritdoc />
    public string VerifyCallsign(Callsign? callsign) =>
        $"{SpokenOr(callsign, UnknownAircraft)}, vérifiez votre indicatif, répétez.";

    // ---------------------------------------------------------------------------------
    // Text helpers
    // ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public bool MentionsContact(string controllerText, ControllerPosition position) =>
        controllerText.Contains($"contactez {ControllerName(position)}", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string ToDisplayForm(string spokenText, Callsign callsign)
    {
        ArgumentNullException.ThrowIfNull(spokenText);
        ArgumentNullException.ThrowIfNull(callsign);

        return spokenText.Replace(SpeakCallsign(callsign), callsign.Normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sentence(string text) =>
        $"{char.ToUpperInvariant(text[0])}{text[1..]}.";

    private string SpokenOr(Callsign? callsign, string fallback) =>
        callsign is null ? fallback : SpeakCallsign(callsign);

    private static string? Wind(Heading? direction, int? speedKnots) =>
        direction is Heading heading && speedKnots is int speed
            ? $"vent {FrenchSpokenNumbers.Heading(heading.Degrees)} degrés, {FrenchSpokenNumbers.Cardinal(speed)} nœuds"
            : null;

    private PhraseBuilder Phrase(Callsign callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        return new PhraseBuilder(SpeakCallsign(callsign));
    }

    private static string ReasonText(UnableReason reason) => reason switch
    {
        UnableReason.AlreadyAirborne => "vous êtes déjà en vol",
        UnableReason.ClearanceAlreadyIssued => "clairance déjà délivrée",
        UnableReason.NoDepartureRunwayAvailable => "aucune piste de départ disponible",
        UnableReason.Airborne => "vous êtes en vol",
        UnableReason.AlreadyMoving => "vous êtes déjà en mouvement",
        UnableReason.NotAtThisStageOfFlight => "pas à ce stade du vol",
        UnableReason.ReadBackClearanceFirst => "collationnez d'abord votre clairance",
        UnableReason.NoDepartureRunwayAssigned => "aucune piste de départ assignée",
        UnableReason.ProgressiveTaxiGroundOnly => "guidage au roulage uniquement au sol",
        UnableReason.NoActiveTaxiRoute => "aucun itinéraire de roulage en cours",
        UnableReason.ContactTowerForDeparture => "contactez la Tour pour le départ",
        UnableReason.NotAtHoldingPoint => "vous n'êtes pas au point d'attente",
        UnableReason.NotWithThisController => "pas sur cette fréquence",
        UnableReason.WaypointNotOnRoute => "ce point n'est pas sur votre route",
        UnableReason.ContactApproach => "contactez Approche",
        UnableReason.DestinationDataUnavailable => "données de l'aérodrome de destination indisponibles",
        UnableReason.NoArrivalRunwayAvailable => "aucune piste d'arrivée disponible",
        UnableReason.RunwayNotAvailable => "cette piste n'est pas disponible",
        UnableReason.NotClearedForApproach => "vous n'êtes pas autorisé pour l'approche",
        UnableReason.ContactTower => "contactez la Tour",
        UnableReason.NoLandingRunwayAssigned => "aucune piste d'atterrissage assignée",
        UnableReason.NotLanded => "vous n'avez pas atterri",
        UnableReason.AerodromeDataUnavailable => "données de l'aérodrome indisponibles",
        UnableReason.NoStandAvailable => "aucun poste de stationnement disponible",
        _ => "impossible de se conformer",
    };

    /// <summary>Callsign, then comma-separated phrases, then a full stop.</summary>
    private sealed class PhraseBuilder(string spokenCallsign)
    {
        private readonly List<string> _parts = [];

        public PhraseBuilder Add(string phrase)
        {
            if (!string.IsNullOrWhiteSpace(phrase))
            {
                _parts.Add(phrase.Trim());
            }

            return this;
        }

        public string Build()
        {
            var builder = new StringBuilder(spokenCallsign);
            foreach (string part in _parts)
            {
                builder.Append(", ").Append(part);
            }

            builder.Append('.');
            return builder.ToString();
        }
    }
}
