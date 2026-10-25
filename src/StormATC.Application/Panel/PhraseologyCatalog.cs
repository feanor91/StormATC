using StormATC.Domain;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;

namespace StormATC.Application.Panel;

/// <summary>The stages of a flight that the phraseology guide is organised by.</summary>
public enum PhraseologyStage
{
    /// <summary>IFR: asking for and reading back the clearance.</summary>
    Clearance,

    /// <summary>VFR: the call before taxiing.</summary>
    VfrDeparture,

    /// <summary>Pushback and engine start.</summary>
    Pushback,

    /// <summary>Taxi to the runway.</summary>
    TaxiOut,

    /// <summary>Holding point, line-up and take-off.</summary>
    Departure,

    /// <summary>IFR departure and climb.</summary>
    Climb,

    /// <summary>IFR en route.</summary>
    Enroute,

    /// <summary>IFR descent and arrival.</summary>
    Descent,

    /// <summary>Approach and landing.</summary>
    Approach,

    /// <summary>Go-around.</summary>
    GoAround,

    /// <summary>After landing: vacating, taxi to the stand.</summary>
    TaxiIn,

    /// <summary>VFR: arrival call, circuit legs, touch and go, leaving the circuit.</summary>
    VfrCircuit,

    /// <summary>Distress and urgency.</summary>
    Abnormal,
}

/// <summary>One thing a pilot may need to say, and what the controller answers.</summary>
/// <param name="Situation">When to say it.</param>
/// <param name="Pilot">What the pilot says.</param>
/// <param name="Controller">What the controller answers; empty when it is a readback.</param>
public sealed record PhraseExample(string Situation, string Pilot, string Controller);

/// <summary>A theme of the military page and its calls.</summary>
/// <param name="Key">A stable identifier.</param>
/// <param name="Title">The theme's name, in the radio language.</param>
/// <param name="Examples">The calls.</param>
public sealed record PhraseologySection(string Key, string Title, IReadOnlyList<PhraseExample> Examples);

/// <summary>The phraseology for one stage of the flight.</summary>
/// <param name="Stage">The stage.</param>
/// <param name="Title">The stage's title, in the radio language.</param>
/// <param name="Examples">The calls for this stage, in the order they happen.</param>
public sealed record PhraseologyGuide(
    PhraseologyStage Stage,
    string Title,
    IReadOnlyList<PhraseExample> Examples);

/// <summary>
/// Example calls for each stage of a flight, in English and French, shown by the in-simulator
/// panel for the stage the flight is in.
/// </summary>
/// <remarks>
/// These are teaching examples, not a second source of truth: the state engine decides what is
/// cleared. Numbers are written as placeholders (<c>XX</c>) because the real values come from the
/// controller; the callsign is the aircraft's own.
/// </remarks>
public static class PhraseologyCatalog
{
    /// <summary>The stage a session is in.</summary>
    /// <remarks>
    /// The session's phase says where ATC thinks the flight is; the aircraft says where it really
    /// is. A pilot who taxis or takes off without calling leaves the phase behind, so the
    /// aircraft's movement moves the stage forward (never backward).
    /// </remarks>
    public static PhraseologyStage StageFor(AtcSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        bool vfr = session.FlightRules.IsVisual();
        bool beforeTaxi = session.Phase is FlightPhase.Disconnected or FlightPhase.Preflight
            or FlightPhase.ClearanceRequested or FlightPhase.IFRClearanceIssued or FlightPhase.Pushback;
        bool onGroundPhase = beforeTaxi || session.Phase is FlightPhase.TaxiOut
            or FlightPhase.HoldingShort or FlightPhase.LineUp or FlightPhase.Takeoff;

        if (onGroundPhase && !session.AircraftState.IsOnGround && session.AircraftState.AltitudeAboveGroundFeet > 50)
        {
            return vfr ? PhraseologyStage.VfrCircuit : PhraseologyStage.Climb;
        }

        if (beforeTaxi && session.AircraftState.IsOnGround && session.AircraftState.GroundSpeedKnots > 3)
        {
            return PhraseologyStage.TaxiOut;
        }

        return session.Phase switch
        {
            FlightPhase.Disconnected or FlightPhase.Preflight or FlightPhase.ClearanceRequested
                or FlightPhase.IFRClearanceIssued => vfr ? PhraseologyStage.VfrDeparture : PhraseologyStage.Clearance,
            FlightPhase.Pushback => PhraseologyStage.Pushback,
            FlightPhase.TaxiOut => PhraseologyStage.TaxiOut,
            FlightPhase.HoldingShort or FlightPhase.LineUp or FlightPhase.Takeoff => PhraseologyStage.Departure,
            FlightPhase.InitialClimb or FlightPhase.Departure => vfr ? PhraseologyStage.VfrCircuit : PhraseologyStage.Climb,
            FlightPhase.Enroute => vfr ? PhraseologyStage.VfrCircuit : PhraseologyStage.Enroute,
            FlightPhase.Descent or FlightPhase.Arrival => vfr ? PhraseologyStage.VfrCircuit : PhraseologyStage.Descent,
            FlightPhase.Approach or FlightPhase.Final => vfr ? PhraseologyStage.VfrCircuit : PhraseologyStage.Approach,
            FlightPhase.Circuit => PhraseologyStage.VfrCircuit,
            FlightPhase.GoAround => PhraseologyStage.GoAround,
            FlightPhase.Landed or FlightPhase.TaxiIn or FlightPhase.Parked => PhraseologyStage.TaxiIn,
            FlightPhase.Abnormal => PhraseologyStage.Abnormal,
            _ => PhraseologyStage.Clearance,
        };
    }

    /// <summary>Every stage, in the order a flight goes through them.</summary>
    public static IReadOnlyList<PhraseologyStage> Stages { get; } =
    [
        PhraseologyStage.Clearance,
        PhraseologyStage.VfrDeparture,
        PhraseologyStage.Pushback,
        PhraseologyStage.TaxiOut,
        PhraseologyStage.Departure,
        PhraseologyStage.Climb,
        PhraseologyStage.Enroute,
        PhraseologyStage.Descent,
        PhraseologyStage.Approach,
        PhraseologyStage.GoAround,
        PhraseologyStage.TaxiIn,
        PhraseologyStage.VfrCircuit,
        PhraseologyStage.Abnormal,
    ];

    /// <summary>The guide for a stage.</summary>
    /// <param name="stage">The stage.</param>
    /// <param name="language">The radio language.</param>
    /// <param name="callsign">The aircraft's callsign as it is spoken.</param>
    public static PhraseologyGuide For(PhraseologyStage stage, PhraseologyLanguage language, string callsign)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callsign);

        return language == PhraseologyLanguage.French
            ? French(stage, callsign)
            : English(stage, callsign);
    }

    /// <summary>
    /// The guide for a stage under the given rules: the civil guide, plus the military calls that apply
    /// at that stage when the flight is in a CAM regime.
    /// </summary>
    /// <param name="stage">The stage.</param>
    /// <param name="language">The radio language.</param>
    /// <param name="callsign">The aircraft's callsign as it is spoken.</param>
    /// <param name="rules">The rules the flight is operated under.</param>
    public static PhraseologyGuide For(PhraseologyStage stage, PhraseologyLanguage language, string callsign, FlightRules rules)
    {
        PhraseologyGuide guide = For(stage, language, callsign);
        if (!rules.IsMilitary())
        {
            return guide;
        }

        bool french = language == PhraseologyLanguage.French;
        string regime = rules.ToSpokenLabel();
        PhraseExample[] extra = stage switch
        {
            PhraseologyStage.VfrDeparture or PhraseologyStage.TaxiOut or PhraseologyStage.Clearance => french
                ? [new($"Demander le roulage en {regime}", $"Sol, {callsign}, {regime}, parking, demandons le roulage.", $"{callsign}, roulez point d'arrêt piste XX, QFE XXXX.")]
                : [new($"Request taxi in {regime}", $"Ground, {callsign}, {regime}, parking, request taxi.", $"{callsign}, taxi to holding point runway XX, QFE XXXX.")],
            PhraseologyStage.VfrCircuit or PhraseologyStage.Approach => french
                ? [new("Finale, contrôle du train", $"Tour, {callsign}, finale piste XX, train sorti verrouillé.", $"{callsign}, piste XX, autorisé atterrissage.")]
                : [new("Final, gear check", $"Tower, {callsign}, final runway XX, gear down and locked.", $"{callsign}, runway XX, cleared to land.")],
            _ => [],
        };

        return extra.Length == 0 ? guide : guide with { Examples = [.. guide.Examples, .. extra] };
    }

    /// <summary>The calls that are always possible, whatever the stage and the rules.</summary>
    /// <param name="language">The radio language.</param>
    /// <param name="callsign">The aircraft's callsign as it is spoken.</param>
    /// <param name="rules">The rules the flight is operated under.</param>
    public static IReadOnlyList<PhraseExample> Always(PhraseologyLanguage language, string callsign, FlightRules rules)
    {
        IReadOnlyList<PhraseExample> civil = Always(language, callsign);
        if (!rules.IsMilitary())
        {
            return civil;
        }

        string other = rules == FlightRules.CamV ? FlightRules.CamI.ToSpokenLabel() : FlightRules.CamV.ToSpokenLabel();
        PhraseExample change = language == PhraseologyLanguage.French
            ? new($"Changer de régime ({rules.ToSpokenLabel()} actuellement)", $"{callsign}, demandons {other}.", $"{callsign}, vous êtes maintenant en {other}.")
            : new($"Change regime (now {rules.ToSpokenLabel()})", $"{callsign}, request {other}.", $"{callsign}, you are now {other}.");
        return [.. civil, change];
    }

    /// <summary>
    /// The calls of the French military regimes (CAM V, CAM I), by theme: taxi, arrival and break, PAR and SRA,
    /// regime change. They follow Instruction 1950 DSAE/DIRCAM as far as StormATC answers them.
    /// </summary>
    /// <param name="language">The radio language.</param>
    /// <param name="callsign">The aircraft's callsign as it is spoken.</param>
    public static IReadOnlyList<PhraseologySection> MilitarySections(PhraseologyLanguage language, string callsign)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callsign);
        string cs = callsign;

        return language == PhraseologyLanguage.French
            ?
            [
                new("ground", "Roulage",
                [
                    new("Demander le roulage", $"Sol, {cs}, CAM Victor, parking, demandons le roulage.", $"{cs}, roulez piste XX, QFE XXXX."),
                    new("Collationner", $"Roulons piste XX, QFE XXXX, {cs}.", $"{cs}, collationnement correct."),
                ]),
                new("arrival", "Arrivée et break",
                [
                    new("Premier appel", $"Tour, {cs}, vingt nautiques au sud, demande le break.", $"{cs}, piste XX, vent XXX degrés XX nœuds, QFE XXXX, rappelez initiale."),
                    new("À l'initiale", $"{cs}, initiale, QFE XXXX.", $"{cs}, correct, numéro un, rappelez dernier virage, train sorti verrouillé."),
                    new("Dernier virage", $"{cs}, dernier virage piste XX, train sorti verrouillé.", $"{cs}, piste XX, vent XXX degrés XX nœuds, autorisé atterrissage."),
                    new("Si le train n'a pas été annoncé", $"{cs}, dernier virage piste XX.", $"{cs}, vérifiez train sorti verrouillé."),
                ]),
                new("radar", "Approche radar PAR et SRA",
                [
                    new("Demander un PAR", $"{cs}, demande PAR piste XX.", $"{cs}, radar PAR piste XX, QFE XXXX, ne répondez plus aux transmissions, sauf demande."),
                    new("Demander un SRA", $"{cs}, demande SRA piste XX.", $"{cs}, approche de surveillance piste XX, QFE XXXX, ne répondez plus aux transmissions, sauf demande."),
                    new("Pendant le PAR (sans répondre)", string.Empty, $"{cs}, légèrement à gauche de l'axe, route XXX, sous la pente, distance trois milles."),
                    new("Pendant le SRA (sans répondre)", string.Empty, $"{cs}, sur l'axe, distance quatre milles, altitude recommandée XXXX pieds."),
                    new("Fin de SRA", $"{cs}, piste en vue.", $"{cs}, piste XX, autorisé atterrissage."),
                ]),
                new("marsa", "MARSA (patrouille)",
                [
                    new("MARSA au départ", $"{cs}, patrouille de deux, demande MARSA départ piste XX.", $"{cs}, MARSA départ piste XX approuvé, initial cap XXX."),
                    new("MARSA à l'arrivée", $"{cs}, patrouille de deux, demande MARSA approche, trail trois milles.", $"{cs}, MARSA approche piste XX approuvé."),
                ]),
                new("tactical", "Situation tactique (CAM T)",
                [
                    new("Demander la situation", $"{cs}, demande la situation.", $"{cs}, deux contacts, premier, gisement zéro quatre cinq, trente-huit milles, vingt-cinq mille pieds, chaud, deuxième, …"),
                    new("Rien en vue", $"{cs}, situation.", $"{cs}, aucun contact."),
                ]),
                new("regime", "Régime CAM",
                [
                    new("Passer en CAM India", $"{cs}, demandons CAM India.", $"{cs}, vous êtes maintenant en CAM India."),
                    new("Passer en CAM Victor", $"{cs}, demandons CAM Victor.", $"{cs}, vous êtes maintenant en CAM Victor."),
                    new("Passer en CAM Tango (vol tactique)", $"{cs}, demandons CAM Tango.", $"{cs}, vous êtes en CAM Tango, vol tactique, hors service de la circulation aérienne, rappelez pour reprendre CAM Victor ou CAM India."),
                ]),
            ]
            :
            [
                new("ground", "Taxi",
                [
                    new("Request taxi", $"Ground, {cs}, CAM Victor, parking, request taxi.", $"{cs}, taxi runway XX, QFE XXXX."),
                    new("Read back", $"Taxi runway XX, QFE XXXX, {cs}.", $"{cs}, readback correct."),
                ]),
                new("arrival", "Arrival and break",
                [
                    new("First call", $"Tower, {cs}, twenty miles south, request the break.", $"{cs}, runway XX, wind XXX degrees XX knots, QFE XXXX, report initial."),
                    new("At the initial point", $"{cs}, initial, QFE XXXX.", $"{cs}, correct, number one, report turning final, gear down and locked."),
                    new("Turning final", $"{cs}, turning final runway XX, gear down and locked.", $"{cs}, runway XX, wind XXX degrees XX knots, cleared to land."),
                    new("If the gear was not reported", $"{cs}, turning final runway XX.", $"{cs}, check gear down and locked."),
                ]),
                new("radar", "PAR and SRA radar approach",
                [
                    new("Request a PAR", $"{cs}, request PAR runway XX.", $"{cs}, PAR approach, runway XX, QFE XXXX, do not acknowledge further transmissions unless requested."),
                    new("Request an SRA", $"{cs}, request SRA runway XX.", $"{cs}, surveillance radar approach, runway XX, QFE XXXX, do not acknowledge further transmissions unless requested."),
                    new("During the PAR (do not answer)", string.Empty, $"{cs}, slightly left of centreline, heading XXX, below glidepath, three miles from touchdown."),
                    new("During the SRA (do not answer)", string.Empty, $"{cs}, on centreline, four miles from touchdown, recommended altitude XXXX."),
                    new("End of SRA", $"{cs}, runway in sight.", $"{cs}, runway XX, cleared to land."),
                ]),
                new("marsa", "MARSA (formation)",
                [
                    new("MARSA departure", $"{cs}, flight of two, request MARSA departure runway XX.", $"{cs}, MARSA departure runway XX approved, initial heading XXX."),
                    new("MARSA approach", $"{cs}, flight of two, request MARSA approach, trail three miles.", $"{cs}, MARSA approach runway XX approved."),
                ]),
                new("tactical", "Tactical picture (CAM T)",
                [
                    new("Ask for the picture", $"{cs}, request picture.", $"{cs}, two contacts, first, bearing zero four five, 38 miles, 25000 feet, hot, second, …"),
                    new("Nothing around", $"{cs}, picture.", $"{cs}, no contact."),
                ]),
                new("regime", "CAM regime",
                [
                    new("Change to CAM India", $"{cs}, request CAM India.", $"{cs}, you are now CAM India."),
                    new("Change to CAM Victor", $"{cs}, request CAM Victor.", $"{cs}, you are now CAM Victor."),
                    new("Change to CAM Tango (tactical flight)", $"{cs}, request CAM Tango.", $"{cs}, you are CAM Tango, tactical flight, outside the air traffic service, call to resume CAM Victor or CAM India."),
                ]),
            ];
    }

    /// <summary>The calls that are always possible, whatever the stage.</summary>
    public static IReadOnlyList<PhraseExample> Always(PhraseologyLanguage language, string callsign)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callsign);

        return language == PhraseologyLanguage.French
            ?
            [
                new("Vous n'avez pas compris", $"{callsign}, répétez.", $"{callsign}, (répète l'instruction)."),
                new("Connaître une fréquence", $"{callsign}, quelle est la fréquence de la Tour ?", $"{callsign}, Tour, cent dix-huit décimale sept."),
                new("Accuser réception", $"Reçu, {callsign}.", string.Empty),
                new("Vous ne pouvez pas exécuter", $"Incapables, {callsign}.", string.Empty),
            ]
            :
            [
                new("You did not understand", $"{callsign}, say again.", $"{callsign}, (repeats the instruction)."),
                new("Ask for a frequency", $"{callsign}, request tower frequency.", $"{callsign}, tower one one eight decimal seven."),
                new("Acknowledge", $"Roger, {callsign}.", string.Empty),
                new("You cannot comply", $"Unable, {callsign}.", string.Empty),
            ];
    }

    private static PhraseologyGuide French(PhraseologyStage stage, string cs)
    {
        (string Title, PhraseExample[] Examples) guide = stage switch
        {
            PhraseologyStage.Clearance => ("Clairance IFR",
            [
                new("Appeler le Prévol", $"Prévol, bonjour, {cs}, demandons la clairance IFR.", $"{cs}, prêts à copier."),
                new("Répondre quand le contrôleur est prêt", $"{cs}, prêts à copier.", $"{cs}, autorisé … départ …, piste XX, montée initiale XXXX pieds, transpondeur XXXX."),
                new("Collationner : tout répéter", $"Autorisés …, départ …, piste XX, montée initiale XXXX pieds, transpondeur XXXX, {cs}.", $"{cs}, collationnement correct."),
            ]),
            PhraseologyStage.VfrDeparture => ("Départ VFR : avant le roulage",
            [
                new("Premier appel", $"Sol, {cs}, DR 400, parking Alpha, information India, vol local, demandons le roulage.", $"{cs}, roulez point d'attente piste XX, QNH XXXX."),
                new("Collationner le roulage", $"Roulons point d'attente piste XX, QNH XXXX, {cs}.", $"{cs}, collationnement correct."),
                new("Sans Sol sur le terrain", $"Tour, {cs}, VFR, vol local, demandons le roulage.", string.Empty),
            ]),
            PhraseologyStage.Pushback => ("Repoussage et mise en route",
            [
                new("Demander", $"Sol, {cs}, demandons repoussage et mise en route.", $"{cs}, repoussage et mise en route approuvés."),
                new("Accuser réception", $"Repoussage et mise en route approuvés, {cs}.", string.Empty),
            ]),
            PhraseologyStage.TaxiOut => ("Roulage vers la piste",
            [
                new("Demander le roulage", $"{cs}, demandons le roulage.", $"{cs}, roulez point d'attente piste XX via Alpha, Charlie."),
                new("Collationner", $"Roulons point d'attente piste XX via Alpha, Charlie, maintenons avant piste XX, {cs}.", $"{cs}, collationnement correct."),
                new("Arrivé au point d'attente", $"{cs}, au point d'attente piste XX.", $"{cs}, contactez Tour cent dix-huit décimale sept."),
                new("Traverser une piste", $"{cs}, au point d'attente piste XX, demandons à traverser.", $"{cs}, traversez piste XX."),
                new("Remonter la piste", $"{cs}, demandons la remontée de piste XX.", $"{cs}, remontez la piste XX, rappelez prêt au départ."),
                new("Demander l'heure", $"{cs}, demandons l'heure exacte.", $"{cs}, il est un deux zéro zéro UTC."),
                new("Tractage", $"{cs}, demandons le tractage.", $"{cs}, tractage autorisé, rappelez tractage terminé."),
            ]),
            PhraseologyStage.Departure => ("Point d'attente, alignement, décollage",
            [
                new("Prêt au départ", $"{cs}, au point d'attente piste XX, prêts au départ.", $"{cs}, alignez-vous piste XX et attendez."),
                new("Alignement", $"Alignons-nous piste XX et attendons, {cs}.", string.Empty),
                new("Décollage", $"Piste XX, autorisés décollage, {cs}.", $"{cs}, piste XX, vent XXX degrés XX nœuds, autorisé décollage."),
            ]),
            PhraseologyStage.Climb => ("Montée et départ",
            [
                new("Premier contact avec Départ", $"Départ, bonjour, {cs}, passant deux mille pieds en montée vers cinq mille.", $"{cs}, bonjour, radar, montez niveau XXX."),
                new("Montée", $"Montons niveau XXX, {cs}.", string.Empty),
                new("Changement de fréquence", $"Contactons Contrôle cent vingt-quatre décimale huit, {cs}.", string.Empty),
            ]),
            PhraseologyStage.Enroute => ("En route",
            [
                new("Demander un direct", $"{cs}, demandons direct NOVAR.", $"{cs}, autorisé direct NOVAR."),
                new("Demander un niveau", $"{cs}, demandons à monter niveau deux quatre zéro.", $"{cs}, montez niveau deux quatre zéro."),
                new("Cap", $"Tournons à gauche cap deux sept zéro, {cs}.", string.Empty),
                new("Évitement météo", $"{cs}, demandons un évitement.", $"{cs}, évitement approuvé."),
                new("Trafic annoncé", $"Je surveille, {cs}.", $"{cs}, trafic à douze heures, trois milles, cinq cents pieds au-dessus."),
                new("Annuler l'IFR", $"{cs}, annulons l'IFR.", $"{cs}, annulation du plan de vol IFR enregistrée, bon vol."),
                new("Ident", $"{cs}, ident.", $"{cs}, identifié."),
                new("Parlez plus lentement", $"{cs}, parlez plus lentement s'il vous plaît.", $"Je répète lentement : …"),
                new("Nouvelle route", $"{cs}, demandons une nouvelle route via KILOX.", $"{cs}, nouvelle route approuvée via KILOX, rappelez au point suivant."),
                new("Vitesse", $"{cs}, demandons vitesse deux cent cinquante.", $"{cs}, maintenez vitesse deux cinq zéro nœuds."),
                new("Attente", $"{cs}, demandons une attente sur KILOX.", $"{cs}, attendez sur KILOX, virages à droite, éloignement une minute."),
                new("Quitter l'attente", $"{cs}, prêts à quitter l'attente.", $"{cs}, quittez l'attente, poursuivez votre route."),
                new("Compte rendu de position", $"{cs}, position KILOX, niveau un deux zéro.", $"{cs}, reçu."),
                new("RVSM", $"{cs}, RVSM approuvé.", $"{cs}, reçu, RVSM approuvé."),
                new("SELCAL et CPDLC", $"{cs}, contrôle SELCAL.", $"{cs}, contrôle SELCAL reçu."),
                new("Panne CPDLC", $"{cs}, panne CPDLC.", $"{cs}, panne CPDLC reçue, poursuivez à la phonie."),
                new("Décalage parallèle", $"{cs}, demande un décalage de deux nautiques à droite de la route.", $"{cs}, autorisé décalage deux nautiques à droite de la route, rappelez de retour sur la route."),
                new("Retour sur la route", $"{cs}, de retour sur la route.", $"{cs}, reçu, fin du décalage, poursuivez votre navigation."),
            ]),
            PhraseologyStage.Descent => ("Descente et arrivée",
            [
                new("Descente", $"Descendons altitude trois mille pieds, QNH XXXX, {cs}.", string.Empty),
                new("Demander l'approche", $"{cs}, demandons l'ILS piste XX.", $"{cs}, autorisé approche ILS piste XX."),
                new("Changement de fréquence", $"Contactons Tour cent dix-huit décimale sept, {cs}.", string.Empty),
            ]),
            PhraseologyStage.Approach => ("Approche et atterrissage",
            [
                new("Établi", $"{cs}, établis ILS piste XX.", $"{cs}, contactez Tour cent dix-huit décimale sept."),
                new("Contact Tour", $"Tour, {cs}, établis ILS piste XX.", $"{cs}, piste XX, vent XXX degrés XX nœuds, autorisé atterrissage."),
                new("Collationner", $"Piste XX, autorisés atterrissage, {cs}.", string.Empty),
                new("Remise de gaz", $"{cs}, remise de gaz.", $"{cs}, montez …, cap …."),
            ]),
            PhraseologyStage.GoAround => ("Remise de gaz",
            [
                new("Annoncer", $"{cs}, remise de gaz.", $"{cs}, montez altitude XXXX pieds, cap XXX."),
                new("Collationner", $"Montons altitude XXXX pieds, cap XXX, {cs}.", string.Empty),
            ]),
            PhraseologyStage.TaxiIn => ("Après l'atterrissage",
            [
                new("Piste dégagée", $"{cs}, piste dégagée.", $"{cs}, contactez Sol cent vingt et un décimale sept."),
                new("Rouler vers le parking", $"Sol, {cs}, demandons le roulage vers le parking.", $"{cs}, roulez parking XX."),
                new("Collationner", $"Roulons parking XX, {cs}.", string.Empty),
            ]),
            PhraseologyStage.VfrCircuit => ("Circuit d'aérodrome VFR",
            [
                new("En route : suivi VFR", $"Paris Information, {cs}, PA 28, VFR de Lognes vers Pontoise, deux mille cinq cents pieds, demande de suivi.", $"{cs}, Paris Information, suivi accordé, affichez deux deux trois quatre."),
                new("En route : changer de secteur", $"{cs}, contactez Paris Information cent vingt-cinq décimale sept.", $"Cent vingt-cinq décimale sept, {cs}."),
                new("Appel d'arrivée", $"Tour, bonjour, {cs}, PA 28, VFR de Melun pour un toucher, mille deux cents pieds, estimé Écho à zéro cinq, information India, demandons consignes d'intégration.", $"{cs}, reçu, rappelez Écho."),
                new("Au point de report", $"Tour, {cs}, passe Écho.", $"{cs}, entrez vent arrière main gauche piste XX, rappelez vent arrière."),
                new("Vent arrière", $"Tour, {cs}, vent arrière main gauche piste XX.", $"{cs}, rappelez base main gauche piste XX."),
                new("Base", $"Tour, {cs}, base main gauche piste XX.", $"{cs}, rappelez finale piste XX."),
                new("Finale", $"Tour, {cs}, en finale piste XX.", $"{cs}, piste XX, autorisé atterrissage."),
                new("Demander un toucher", $"{cs}, demandons toucher.", $"{cs}, piste XX, autorisé toucher."),
                new("Quitter le circuit", $"{cs}, sortie de circuit, je quitte la fréquence.", $"{cs}, reçu, au revoir."),
                new("VFR spécial", $"Tour, {cs}, demandons VFR spécial.", $"{cs}, autorisé VFR spécial dans la zone, rappelez en sortie de zone."),
                new("Transit par la verticale", $"Tour, {cs}, demandons transit par la verticale de l'aérodrome.", $"{cs}, transit autorisé, altitude deux mille pieds, QNH XXXX, rappelez à la verticale."),
                new("360", $"Tour, {cs}, demandons un 360 par la droite.", $"{cs}, 360 par la droite approuvé, rappelez vent arrière."),
                new("Allonger la vent arrière", $"Tour, {cs}, demandons d'allonger la vent arrière.", $"{cs}, allongez la vent arrière, je vous rappelle pour la base."),
                new("Clôturer le plan de vol", $"{cs}, demandons la clôture du plan de vol.", $"{cs}, plan de vol clos, bonne journée."),
            ]),
            PhraseologyStage.Abnormal => ("Urgence",
            [
                new("Détresse", $"Mayday, mayday, mayday, {cs}, (nature de la panne), (intentions).", string.Empty),
                new("Urgence", $"Pan-pan, pan-pan, pan-pan, {cs}, (nature du problème).", string.Empty),
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
        };

        return new PhraseologyGuide(stage, guide.Title, guide.Examples);
    }

    private static PhraseologyGuide English(PhraseologyStage stage, string cs)
    {
        (string Title, PhraseExample[] Examples) guide = stage switch
        {
            PhraseologyStage.Clearance => ("IFR clearance",
            [
                new("Call Delivery", $"Delivery, {cs}, request IFR clearance.", $"{cs}, ready to copy."),
                new("Answer when the controller is ready", $"{cs}, ready to copy.", $"{cs}, cleared to …, departure …, runway XX, initial climb XXXX feet, squawk XXXX."),
                new("Read back every item", $"Cleared to …, departure …, runway XX, initial climb XXXX feet, squawk XXXX, {cs}.", $"{cs}, readback correct."),
            ]),
            PhraseologyStage.VfrDeparture => ("VFR departure: before taxi",
            [
                new("First call", $"Ground, {cs}, Cessna 172, parking Alpha, information India, local flight, request taxi.", $"{cs}, taxi to holding point runway XX, QNH XXXX."),
                new("Read back the taxi clearance", $"Taxi to holding point runway XX, QNH XXXX, {cs}.", $"{cs}, readback correct."),
                new("No Ground at this airfield", $"Tower, {cs}, VFR local, request taxi.", string.Empty),
            ]),
            PhraseologyStage.Pushback => ("Pushback and start-up",
            [
                new("Ask", $"Ground, {cs}, request pushback and start.", $"{cs}, pushback and start approved."),
                new("Acknowledge", $"Pushback and start approved, {cs}.", string.Empty),
            ]),
            PhraseologyStage.TaxiOut => ("Taxi to the runway",
            [
                new("Request taxi", $"{cs}, request taxi.", $"{cs}, taxi to holding point runway XX via Alpha, Charlie."),
                new("Read back", $"Taxi to holding point runway XX via Alpha, Charlie, hold short runway XX, {cs}.", $"{cs}, readback correct."),
                new("At the holding point", $"{cs}, holding short runway XX.", $"{cs}, contact Tower one one eight decimal seven."),
                new("Cross a runway", $"{cs}, holding short runway XX, request crossing.", $"{cs}, cross runway XX."),
                new("Backtrack the runway", $"{cs}, request backtrack runway XX.", $"{cs}, backtrack runway XX, report ready for departure."),
                new("Ask the time", $"{cs}, request time check.", $"{cs}, time one two zero zero UTC."),
                new("Tow", $"{cs}, request tow.", $"{cs}, cleared to tow, report towing completed."),
            ]),
            PhraseologyStage.Departure => ("Holding point, line-up, take-off",
            [
                new("Ready for departure", $"{cs}, holding short runway XX, ready for departure.", $"{cs}, line up and wait runway XX."),
                new("Line up", $"Line up and wait runway XX, {cs}.", string.Empty),
                new("Take-off", $"Runway XX, cleared for take-off, {cs}.", $"{cs}, runway XX, wind XXX degrees XX knots, cleared for take-off."),
            ]),
            PhraseologyStage.Climb => ("Departure and climb",
            [
                new("First contact with Departure", $"Departure, {cs}, passing two thousand feet climbing five thousand.", $"{cs}, radar contact, climb flight level XXX."),
                new("Climb", $"Climb flight level XXX, {cs}.", string.Empty),
                new("Frequency change", $"Contact Control one two four decimal eight, {cs}.", string.Empty),
            ]),
            PhraseologyStage.Enroute => ("En route",
            [
                new("Ask for a direct", $"{cs}, request direct NOVAR.", $"{cs}, cleared direct NOVAR."),
                new("Ask for a level", $"{cs}, request climb flight level two four zero.", $"{cs}, climb flight level two four zero."),
                new("Heading", $"Turn left heading two seven zero, {cs}.", string.Empty),
                new("Weather deviation", $"{cs}, request deviation.", $"{cs}, deviation approved."),
                new("Traffic information", $"Looking out, {cs}.", $"{cs}, traffic twelve o'clock, three miles, five hundred feet above."),
                new("Cancel IFR", $"{cs}, cancel IFR.", $"{cs}, IFR flight plan cancelled, good day."),
                new("Ident", $"{cs}, squawk ident.", $"{cs}, identified."),
                new("Speak slower", $"{cs}, speak slower please.", $"I say again, slowly: …"),
                new("New route", $"{cs}, request new route via KILOX.", $"{cs}, new route approved via KILOX, report the next fix."),
                new("Speed", $"{cs}, request speed two five zero.", $"{cs}, maintain two five zero knots."),
                new("Hold", $"{cs}, request hold over KILOX.", $"{cs}, hold over KILOX, right-hand pattern, one minute legs."),
                new("Leave the hold", $"{cs}, ready to leave the hold.", $"{cs}, leave the hold, resume own navigation."),
                new("Position report", $"{cs}, position KILOX, level one two zero.", $"{cs}, roger."),
                new("RVSM", $"{cs}, RVSM approved.", $"{cs}, roger, RVSM approved."),
                new("SELCAL and CPDLC", $"{cs}, SELCAL check.", $"{cs}, SELCAL check received."),
                new("CPDLC failure", $"{cs}, CPDLC failure.", $"{cs}, CPDLC failure received, continue on voice."),
                new("Parallel offset", $"{cs}, request offset two miles right of route.", $"{cs}, cleared to fly offset two miles right of route, report back on route."),
                new("Back on route", $"{cs}, back on route.", $"{cs}, roger, offset cancelled, resume own navigation."),
            ]),
            PhraseologyStage.Descent => ("Descent and arrival",
            [
                new("Descent", $"Descend altitude three thousand feet, QNH XXXX, {cs}.", string.Empty),
                new("Ask for the approach", $"{cs}, request ILS runway XX.", $"{cs}, cleared ILS approach runway XX."),
                new("Frequency change", $"Contact Tower one one eight decimal seven, {cs}.", string.Empty),
            ]),
            PhraseologyStage.Approach => ("Approach and landing",
            [
                new("Established", $"{cs}, established ILS runway XX.", $"{cs}, contact Tower one one eight decimal seven."),
                new("Contact Tower", $"Tower, {cs}, established ILS runway XX.", $"{cs}, runway XX, wind XXX degrees XX knots, cleared to land."),
                new("Read back", $"Runway XX, cleared to land, {cs}.", string.Empty),
                new("Go around", $"{cs}, going around.", $"{cs}, climb …, heading …."),
            ]),
            PhraseologyStage.GoAround => ("Go-around",
            [
                new("Announce", $"{cs}, going around.", $"{cs}, climb altitude XXXX feet, heading XXX."),
                new("Read back", $"Climb altitude XXXX feet, heading XXX, {cs}.", string.Empty),
            ]),
            PhraseologyStage.TaxiIn => ("After landing",
            [
                new("Runway vacated", $"{cs}, runway vacated.", $"{cs}, contact Ground one two one decimal seven."),
                new("Taxi to the stand", $"Ground, {cs}, request taxi to the stand.", $"{cs}, taxi to stand XX."),
                new("Read back", $"Taxi to stand XX, {cs}.", string.Empty),
            ]),
            PhraseologyStage.VfrCircuit => ("VFR aerodrome circuit",
            [
                new("En route: flight following", $"Paris Information, {cs}, Cessna 172, VFR from Lognes to Pontoise, two thousand five hundred feet, request flight following.", $"{cs}, Paris Information, flight following approved, squawk two three four five."),
                new("En route: change of sector", $"{cs}, contact Paris Information one two five decimal seven.", $"One two five decimal seven, {cs}."),
                new("Arrival call", $"Tower, {cs}, Cessna 172, VFR from Melun for touch and go, one thousand two hundred feet, estimating Echo at zero five, information India, request joining instructions.", $"{cs}, roger, report Echo."),
                new("At the reporting point", $"Tower, {cs}, passing Echo.", $"{cs}, join left-hand downwind runway XX, report downwind."),
                new("Downwind", $"Tower, {cs}, downwind left hand runway XX.", $"{cs}, report base left hand runway XX."),
                new("Base", $"Tower, {cs}, base left hand runway XX.", $"{cs}, report final runway XX."),
                new("Final", $"Tower, {cs}, final runway XX.", $"{cs}, runway XX, cleared to land."),
                new("Ask for a touch and go", $"{cs}, request touch and go.", $"{cs}, runway XX, cleared touch and go."),
                new("Leave the circuit", $"{cs}, leaving the circuit and frequency.", $"{cs}, roger, goodbye."),
                new("Special VFR", $"Tower, {cs}, request special VFR.", $"{cs}, cleared special VFR in the zone, report leaving the zone."),
                new("Transit overhead", $"Tower, {cs}, request transit overhead the aerodrome.", $"{cs}, cleared to transit, altitude two thousand feet, QNH XXXX, report overhead."),
                new("360", $"Tower, {cs}, request right 360.", $"{cs}, right 360 approved, report downwind."),
                new("Extend downwind", $"Tower, {cs}, request extended downwind.", $"{cs}, extend downwind, I will call you for base."),
                new("Close the flight plan", $"{cs}, request flight plan closure.", $"{cs}, flight plan closed, good day."),
            ]),
            PhraseologyStage.Abnormal => ("Emergency",
            [
                new("Distress", $"Mayday, mayday, mayday, {cs}, (nature of the emergency), (intentions).", string.Empty),
                new("Urgency", $"Pan-pan, pan-pan, pan-pan, {cs}, (nature of the problem).", string.Empty),
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
        };

        return new PhraseologyGuide(stage, guide.Title, guide.Examples);
    }
}
