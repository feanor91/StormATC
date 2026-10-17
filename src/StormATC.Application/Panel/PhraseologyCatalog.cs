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
    public static PhraseologyStage StageFor(AtcSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        bool vfr = session.FlightRules == FlightRules.Vfr;

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
                new("Appel d'arrivée", $"Tour, bonjour, {cs}, PA 28, VFR de Melun pour un toucher, mille deux cents pieds, estimé Écho à zéro cinq, information India, demandons consignes d'intégration.", $"{cs}, reçu, rappelez Écho."),
                new("Au point de report", $"Tour, {cs}, passe Écho.", $"{cs}, entrez vent arrière main gauche piste XX, rappelez vent arrière."),
                new("Vent arrière", $"Tour, {cs}, vent arrière main gauche piste XX.", $"{cs}, rappelez base main gauche piste XX."),
                new("Base", $"Tour, {cs}, base main gauche piste XX.", $"{cs}, rappelez finale piste XX."),
                new("Finale", $"Tour, {cs}, en finale piste XX.", $"{cs}, piste XX, autorisé atterrissage."),
                new("Demander un toucher", $"{cs}, demandons toucher.", $"{cs}, piste XX, autorisé toucher."),
                new("Quitter le circuit", $"{cs}, sortie de circuit, je quitte la fréquence.", $"{cs}, reçu, au revoir."),
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
                new("Arrival call", $"Tower, {cs}, Cessna 172, VFR from Melun for touch and go, one thousand two hundred feet, estimating Echo at zero five, information India, request joining instructions.", $"{cs}, roger, report Echo."),
                new("At the reporting point", $"Tower, {cs}, passing Echo.", $"{cs}, join left-hand downwind runway XX, report downwind."),
                new("Downwind", $"Tower, {cs}, downwind left hand runway XX.", $"{cs}, report base left hand runway XX."),
                new("Base", $"Tower, {cs}, base left hand runway XX.", $"{cs}, report final runway XX."),
                new("Final", $"Tower, {cs}, final runway XX.", $"{cs}, runway XX, cleared to land."),
                new("Ask for a touch and go", $"{cs}, request touch and go.", $"{cs}, runway XX, cleared touch and go."),
                new("Leave the circuit", $"{cs}, leaving the circuit and frequency.", $"{cs}, roger, goodbye."),
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
