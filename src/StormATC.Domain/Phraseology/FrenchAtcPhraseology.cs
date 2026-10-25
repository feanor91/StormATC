using System.Text;
using StormATC.Domain.Airports;
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

    /// <summary>Renders the name of a departure or arrival procedure: <c>PILU2F</c> becomes "PILU deux Foxtrot".</summary>
    public static string SpeakProcedure(string procedure)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        return ProcedureNames.TrySplit(procedure, out string word, out string rest)
            ? $"{word} {FrenchSpokenNumbers.SpellOut(rest)}"
            : procedure;
    }

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
        ControllerPosition.Information => "Information",
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
            builder.Add($"départ {SpeakProcedure(sid)}");
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
    public string MilitaryArrivalInstruction(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots, int? qfeHectopascals) =>
        Phrase(callsign)
            .Add(SpeakRunway(runway))
            .Add(Wind(windDirection, windSpeedKnots) ?? string.Empty)
            .Add(qfeHectopascals is int qfe ? Qfe(qfe) : string.Empty)
            .Add("rappelez initiale")
            .Build();

    /// <inheritdoc />
    public string InitialPointInstruction(Callsign callsign, int? correctedQfe) =>
        Phrase(callsign)
            .Add(correctedQfe is int qfe ? $"négatif, {Qfe(qfe)}" : "correct")
            .Add("numéro un")
            .Add("rappelez dernier virage, train sorti verrouillé")
            .Build();

    /// <inheritdoc />
    public string CheckGearDownAndLocked(Callsign callsign) =>
        Phrase(callsign).Add("vérifiez train sorti verrouillé").Build();

    /// <inheritdoc />
    public string RadarApproachStart(Callsign callsign, Approach.RadarApproachKind kind, RunwayIdentifier runway, int? qfeHectopascals) =>
        Phrase(callsign)
            .Add(kind == Approach.RadarApproachKind.Par ? "radar PAR" : "approche de surveillance")
            .Add(SpeakRunway(runway))
            .Add(qfeHectopascals is int qfe ? Qfe(qfe) : string.Empty)
            .Add("ne répondez plus aux transmissions, sauf demande")
            .Build();

    /// <inheritdoc />
    public string RadarContact(Callsign callsign, int distanceNauticalMiles) =>
        Phrase(callsign).Add("contact radar").Add($"{Miles(distanceNauticalMiles)} du seuil").Build();

    /// <inheritdoc />
    public string RadarGuidance(Callsign callsign, Approach.RadarCall radarCall)
    {
        PhraseBuilder phrase = Phrase(callsign).Add(radarCall.Lateral switch
        {
            Approach.RadarLateral.OnCentreline => "sur l'axe",
            Approach.RadarLateral.SlightlyLeft => "légèrement à gauche de l'axe",
            Approach.RadarLateral.SlightlyRight => "légèrement à droite de l'axe",
            Approach.RadarLateral.Left => "à gauche de l'axe",
            _ => "à droite de l'axe",
        });

        if (radarCall.CorrectionHeading is int heading)
        {
            phrase.Add($"route {FrenchSpokenNumbers.Heading(heading)}");
        }

        if (radarCall.Vertical is Approach.RadarVertical vertical)
        {
            phrase.Add(vertical switch
            {
                Approach.RadarVertical.OnPath => "sur la pente",
                Approach.RadarVertical.SlightlyAbove => "légèrement au-dessus de la pente",
                Approach.RadarVertical.SlightlyBelow => "légèrement sous la pente",
                Approach.RadarVertical.Above => "au-dessus de la pente",
                _ => "sous la pente",
            });
        }

        phrase.Add($"distance {Miles(radarCall.DistanceNauticalMiles)}");
        if (radarCall.RecommendedAltitudeFeet is int altitude)
        {
            phrase.Add($"altitude recommandée {FrenchSpokenNumbers.Cardinal(altitude)} pieds");
        }

        return phrase.Build();
    }

    /// <inheritdoc />
    public string RadarApproachEnd(Callsign callsign, Approach.RadarApproachKind kind) =>
        Phrase(callsign)
            .Add(kind == Approach.RadarApproachKind.Par ? "fin d'approche PAR" : "fin d'approche de surveillance")
            .Add("annoncez piste en vue ou remise de gaz")
            .Build();

    private static string Miles(int miles) =>
        miles == 1 ? "un mille" : $"{FrenchSpokenNumbers.Cardinal(miles)} milles";

    /// <inheritdoc />
    public string Qfe(int qfeHectopascals) => $"QFE {FrenchSpokenNumbers.Cardinal(qfeHectopascals)}";

    /// <inheritdoc />
    public string TacticalPicture(Callsign callsign, IReadOnlyList<Approach.TacticalContact> contacts)
    {
        PhraseBuilder phrase = Phrase(callsign)
            .Add(contacts.Count == 1 ? "un contact" : $"{FrenchSpokenNumbers.Cardinal(contacts.Count)} contacts");
        for (int i = 0; i < contacts.Count; i++)
        {
            Approach.TacticalContact contact = contacts[i];
            phrase.Add(
                $"{(i == 0 ? "premier" : i == 1 ? "deuxième" : "troisième")}, gisement {FrenchSpokenNumbers.Heading(contact.BearingMagnetic)}, "
                + $"{FrenchSpokenNumbers.Cardinal(Math.Max(1, contact.RangeNauticalMiles))} milles, "
                + $"{FrenchSpokenNumbers.Cardinal(contact.AltitudeFeet)} pieds, "
                + contact.Aspect switch
                {
                    Approach.TacticalAspect.Hot => "chaud",
                    Approach.TacticalAspect.Cold => "froid",
                    _ => "flanc",
                });
        }

        return phrase.Build();
    }

    /// <inheritdoc />
    public string NoTacticalContact(Callsign callsign) =>
        Phrase(callsign).Add("aucun contact").Build();

    /// <inheritdoc />
    public string MarsaApproved(Callsign callsign, Approach.MarsaKind kind, RunwayIdentifier runway, int initialHeading) =>
        kind == Approach.MarsaKind.Departure
            ? Phrase(callsign)
                .Add($"MARSA départ {SpeakRunway(runway)} approuvé")
                .Add($"initial cap {FrenchSpokenNumbers.Heading(initialHeading)}")
                .Build()
            : Phrase(callsign).Add($"MARSA approche {SpeakRunway(runway)} approuvé").Build();

    /// <inheritdoc />
    public string TacticalRegime(Callsign callsign) =>
        Phrase(callsign)
            .Add("vous êtes en CAM Tango")
            .Add("vol tactique, hors service de la circulation aérienne")
            .Add("rappelez pour reprendre CAM Victor ou CAM India")
            .Build();

    /// <inheritdoc />
    public string RegimeChange(Callsign callsign, string regime) =>
        Phrase(callsign).Add($"vous êtes maintenant en {regime}").Build();

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
    public string OptionClearance(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots) =>
        ManoeuvreClearance(callsign, runway, "autorisé option", windDirection, windSpeedKnots);

    /// <inheritdoc />
    public string LowPassClearance(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots) =>
        ManoeuvreClearance(callsign, runway, "autorisé passage bas", windDirection, windSpeedKnots);

    private string ManoeuvreClearance(
        Callsign callsign,
        RunwayIdentifier runway,
        string clearance,
        Heading? windDirection,
        int? windSpeedKnots)
    {
        PhraseBuilder builder = Phrase(callsign).Add(SpeakRunway(runway)).Add(clearance);

        if (Wind(windDirection, windSpeedKnots) is string wind)
        {
            builder.Add(wind);
        }

        return builder.Build();
    }

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

    /// <inheritdoc />
    public string SpeedReduction(Callsign callsign, int knots) =>
        Phrase(callsign)
            .Add($"réduisez vitesse {FrenchSpokenNumbers.DigitsIndividually(knots.ToString(System.Globalization.CultureInfo.InvariantCulture))} nœuds")
            .Build();

    /// <inheritdoc />
    public string ReadbackItemSpeed(int knots) =>
        $"vitesse {FrenchSpokenNumbers.DigitsIndividually(knots.ToString(System.Globalization.CultureInfo.InvariantCulture))} nœuds";

    /// <inheritdoc />
    public string ReadbackItemHold(string? fix) => fix is null ? "attente en position actuelle" : $"attente sur {fix}";

    /// <inheritdoc />
    public string ReadbackItemDirect(string fix) => $"direct {fix}";

    /// <inheritdoc />
    public string RouteChangeApproved(Callsign callsign, string? fix) =>
        Phrase(callsign)
            .Add(fix is null ? "nouvelle route approuvée" : $"nouvelle route approuvée via {fix}")
            .Add("rappelez au point suivant")
            .Build();

    /// <inheritdoc />
    public string SpeedInstruction(Callsign callsign, int? speedKnots, decimal? mach, bool free) =>
        Phrase(callsign)
            .Add(free
                ? "vitesse libre"
                : mach is decimal number
                    ? $"maintenez mach {FrenchSpokenNumbers.DigitsIndividually(number.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Replace("0.", "décimale ", StringComparison.Ordinal))}"
                    : speedKnots is int knots
                        ? $"maintenez vitesse {FrenchSpokenNumbers.DigitsIndividually(knots.ToString(System.Globalization.CultureInfo.InvariantCulture))} nœuds"
                        : "maintenez votre vitesse actuelle")
            .Build();

    /// <inheritdoc />
    public string HoldInstruction(Callsign callsign, string? fix) =>
        Phrase(callsign)
            .Add(fix is null ? "attendez en position actuelle" : $"attendez sur {fix}")
            .Add("virages à droite, éloignement une minute")
            .Add("rappelez prêt à quitter l'attente")
            .Build();

    /// <inheritdoc />
    public string LeaveHoldInstruction(Callsign callsign) =>
        Phrase(callsign).Add("quittez l'attente").Add("poursuivez votre route").Build();

    /// <inheritdoc />
    public string RvsmAcknowledgement(Callsign callsign, bool approved) =>
        Phrase(callsign)
            .Add(approved ? "reçu, RVSM approuvé" : "reçu, négatif RVSM, maintenez un niveau inférieur au niveau deux huit zéro")
            .Build();

    /// <inheritdoc />
    public string SelcalCheckAcknowledgement(Callsign callsign) =>
        Phrase(callsign).Add("contrôle SELCAL reçu").Build();

    /// <inheritdoc />
    public string CpdlcLogonAcknowledgement(Callsign callsign) =>
        Phrase(callsign).Add("connexion CPDLC acceptée").Build();

    /// <inheritdoc />
    public string TowClearance(Callsign callsign) =>
        Phrase(callsign).Add("tractage autorisé").Add("rappelez tractage terminé").Build();

    /// <inheritdoc />
    public string RunwayInspectionClearance(Callsign callsign, RunwayIdentifier runway) =>
        Phrase(callsign)
            .Add($"autorisé à pénétrer piste {SpeakRunway(runway)} pour inspection")
            .Add("rappelez piste dégagée")
            .Build();

    /// <inheritdoc />
    public string ArrivalConditions(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots, int? qnhHectopascals)
    {
        var builder = Phrase(callsign).Add($"piste en service {SpeakRunway(runway)}");
        if (Wind(windDirection, windSpeedKnots) is string wind)
        {
            builder.Add(wind);
        }

        if (qnhHectopascals is int qnh)
        {
            builder.Add($"QNH {FrenchSpokenNumbers.DigitsIndividually(qnh.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public string SequenceNumber(Callsign callsign, int number) =>
        Phrase(callsign).Add($"numéro {FrenchSpokenNumbers.Cardinal(number)}").Add("suivez le trafic en circuit").Build();

    private static string AreaName(HazardArea area) => area.Kind == HazardKind.Weather
        ? $"la zone de mauvais temps {area.Name}"
        : $"la zone militaire {area.Name}";

    /// <inheritdoc />
    public string FlowControlRefusal(Callsign callsign) =>
        Phrase(callsign)
            .Add("négatif, régulation du trafic à l'arrivée")
            .Add("maintenez votre route actuelle")
            .Build();

    /// <inheritdoc />
    public string RouteRefusedHazard(Callsign callsign, HazardArea area) =>
        Phrase(callsign)
            .Add($"négatif, {AreaName(area)} est active sur cette route")
            .Add("maintenez votre route actuelle")
            .Build();

    /// <inheritdoc />
    public string HazardAvoidance(Callsign callsign, Heading heading, bool turnRight, HazardArea area) =>
        Phrase(callsign)
            .Add($"pour éviter {AreaName(area)}, virez à {(turnRight ? "droite" : "gauche")} cap {SpeakHeading(heading)}")
            .Build();

    /// <inheritdoc />
    public string HazardCleared(Callsign callsign, HazardArea area) =>
        Phrase(callsign).Add($"{AreaName(area)} est dégagée, reprenez votre navigation").Build();

    /// <inheritdoc />
    public string ReadbackItemHeading(Heading heading) => $"cap {SpeakHeading(heading)}";

    /// <inheritdoc />
    public string OffsetApproved(Callsign callsign, int miles, bool right) =>
        Phrase(callsign)
            .Add($"autorisé décalage {FrenchSpokenNumbers.Cardinal(miles)} nautiques à {(right ? "droite" : "gauche")} de la route")
            .Add("rappelez de retour sur la route")
            .Build();

    /// <inheritdoc />
    public string OffsetRefused(Callsign callsign) =>
        Phrase(callsign).Add("négatif décalage, maximum vingt nautiques").Add("rappelez votre demande").Build();

    /// <inheritdoc />
    public string OffsetEnded(Callsign callsign) =>
        Phrase(callsign).Add(Received()).Add("fin du décalage, poursuivez votre navigation").Build();

    /// <inheritdoc />
    public string CpdlcFailureAcknowledgement(Callsign callsign) =>
        Phrase(callsign)
            .Add("panne CPDLC reçue")
            .Add("poursuivez à la phonie")
            .Add("les instructions vous seront données sur cette fréquence")
            .Build();

    /// <inheritdoc />
    public string RadarSequence(Callsign callsign, int number) =>
        Phrase(callsign)
            .Add($"numéro {FrenchSpokenNumbers.Cardinal(number)} pour l'approche")
            .Add("attendez-vous à des vecteurs radar")
            .Build();

    /// <inheritdoc />
    public string WakeTurbulenceCaution(string? aircraftType) =>
        aircraftType is { Length: > 0 }
            ? $"Attention turbulence de sillage, {aircraftType}."
            : "Attention turbulence de sillage.";

    /// <inheritdoc />
    public string BacktrackInstruction(Callsign callsign, RunwayIdentifier runway) =>
        Phrase(callsign).Add($"remontez la piste {SpeakRunway(runway)}").Add("rappelez prêt au départ").Build();

    /// <inheritdoc />
    public string TimeCheck(Callsign callsign, DateTimeOffset utc) =>
        Phrase(callsign)
            .Add($"il est {FrenchSpokenNumbers.DigitsIndividually(utc.UtcDateTime.ToString("HHmm", System.Globalization.CultureInfo.InvariantCulture))} UTC")
            .Build();

    /// <inheritdoc />
    public string IdentAcknowledgement(Callsign callsign, bool airborne) =>
        Phrase(callsign).Add(airborne ? "identifié" : Received()).Build();

    /// <inheritdoc />
    public string SpeakSlowly(Callsign callsign, string? lastTransmission) =>
        lastTransmission is { Length: > 0 }
            ? $"Je répète lentement : {lastTransmission}"
            : Phrase(callsign).Add("reçu, je parle plus lentement").Build();

    /// <inheritdoc />
    public string CancelIfrAcknowledgement(Callsign callsign) =>
        Phrase(callsign).Add("annulation du plan de vol IFR enregistrée").Add("bon vol").Build();

    /// <inheritdoc />
    public string SpecialVfrClearance(Callsign callsign) =>
        Phrase(callsign)
            .Add("autorisé VFR spécial dans la zone")
            .Add("restez hors des nuages et maintenez la vue du sol")
            .Add("rappelez en sortie de zone")
            .Build();

    /// <inheritdoc />
    public string SpecialVfrRefused(Callsign callsign, int visibilityMeters) =>
        Phrase(callsign)
            .Add($"VFR spécial impossible, visibilité {visibilityMeters} mètres")
            .Build();

    /// <inheritdoc />
    public string TransitClearance(Callsign callsign, Altitude altitude, int? qnhHectopascals)
    {
        var builder = Phrase(callsign)
            .Add("transit autorisé par la verticale de l'aérodrome")
            .Add($"altitude {SpeakAltitude(altitude)}");
        if (qnhHectopascals is int qnh)
        {
            builder.Add($"QNH {FrenchSpokenNumbers.DigitsIndividually(qnh.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        }

        return builder.Add("rappelez à la verticale").Build();
    }

    /// <inheritdoc />
    public string TransitContinue(Callsign callsign) =>
        Phrase(callsign).Add("reçu, poursuivez").Add("rappelez en sortie de zone").Build();

    /// <inheritdoc />
    public string OrbitApproved(Callsign callsign, CircuitSide? side) =>
        Phrase(callsign)
            .Add(side switch
            {
                CircuitSide.Left => "360 par la gauche approuvé",
                CircuitSide.Right => "360 par la droite approuvé",
                _ => "360 approuvé",
            })
            .Add("rappelez vent arrière")
            .Build();

    /// <inheritdoc />
    public string ExtendDownwindApproved(Callsign callsign) =>
        Phrase(callsign).Add("allongez la vent arrière").Add("je vous rappelle pour la base").Build();

    /// <inheritdoc />
    public string FlightPlanClosed(Callsign callsign) =>
        Phrase(callsign).Add("plan de vol clos").Add(OneOf("bonne journée", "au revoir")).Build();

    /// <inheritdoc />
    public string ImmediateTakeoffClearance(Callsign callsign, RunwayIdentifier runway) =>
        Phrase(callsign).Add(SpeakRunway(runway)).Add("autorisé décollage immédiat").Build();

    /// <inheritdoc />
    public string TrafficInformation(Callsign callsign, int clockPosition, int miles, int relativeAltitudeFeet) =>
        Phrase(callsign)
            .Add($"trafic à {clockPosition} heures")
            .Add($"{miles} {(miles > 1 ? "milles" : "mille")}")
            .Add(Math.Abs(relativeAltitudeFeet) < 300
                ? "même altitude"
                : $"{Math.Abs(relativeAltitudeFeet / 100) * 100} pieds {(relativeAltitudeFeet > 0 ? "au-dessus" : "en dessous")}")
            .Build();

    /// <inheritdoc />
    public string FlightFollowingAccepted(Callsign callsign, string serviceName, SquawkCode squawk) =>
        Phrase(callsign)
            .Add($"{serviceName}, suivi accordé")
            .Add($"affichez {SpeakSquawk(squawk)}")
            .Add("rappelez en cas de changement de vos intentions")
            .Build();

    /// <inheritdoc />
    public string FlightFollowingHandoff(Callsign callsign, string serviceName, Frequency frequency) =>
        Phrase(callsign).Add($"contactez {serviceName} {SpeakFrequency(frequency)}").Build();

    /// <inheritdoc />
    public string FlightFollowingEnded(Callsign callsign, string? nextController, Frequency? frequency) =>
        nextController is not null && frequency is Frequency next
            ? Phrase(callsign).Add("fin de suivi").Add($"contactez {nextController} {SpeakFrequency(next)}").Build()
            : Phrase(callsign).Add("fin de suivi").Add(OneOf("bon vol", "bonne journée")).Build();

    /// <inheritdoc />
    public string FlightFollowingUnavailable(Callsign callsign) =>
        Phrase(callsign).Add("aucun service d'information de vol n'est disponible dans cette région").Build();

    /// <inheritdoc />
    public string FlightFollowingCallService(Callsign callsign, string serviceName, Frequency frequency) =>
        Phrase(callsign).Add($"pour un suivi, contactez {serviceName} {SpeakFrequency(frequency)}").Build();

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
    public string ContactController(Callsign? callsign, ControllerPosition controller, Frequency? frequency = null) =>
        $"{SpokenOr(callsign, StationCalling)}, contactez {ControllerName(controller)}"
        + (frequency is Frequency f ? $" {SpeakFrequency(f)}." : ".");

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
    public string RunwayCrossingClearance(Callsign callsign, RunwayIdentifier runway) =>
        Phrase(callsign).Add($"traversez {SpeakRunway(runway)}").Build();

    /// <inheritdoc />
    public string HoldPositionTrafficOnShortFinal(Callsign callsign, RunwayIdentifier runway) =>
        Phrase(callsign).Add(ReadbackItemHoldShort(runway)).Add("trafic en courte finale").Build();

    /// <inheritdoc />
    public string HoldPositionLinedUp(Callsign callsign) =>
        Phrase(callsign).Add("maintenez position").Add("trafic en courte finale").Build();

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
        UnableReason.NoRunwayCrossingPending => "aucune traversée de piste prévue sur votre itinéraire",
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
