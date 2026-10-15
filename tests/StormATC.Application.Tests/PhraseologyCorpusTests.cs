using StormATC.Application.Intent;
using StormATC.Domain;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>
/// A corpus of the ways a pilot really asks for things in French, checked against the intent the
/// controller must understand. The controller may ask for a readback to be repeated; it must not
/// ask the pilot to repeat a request because the wording differed from an example.
/// </summary>
/// <remarks>
/// Many of these are written the way the speech recogniser writes them, with its usual mistakes:
/// "près au départ" for "prêts au départ", missing articles, a callsign spelled out loosely.
/// </remarks>
public class PhraseologyCorpusTests
{
    private static AtcSession Session(FlightPhase phase, FlightRules rules = FlightRules.Vfr, bool onGround = true)
    {
        AtcSession session = AtcSession
            .Create(Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, TestAerodromes.AtStand(Fixture.Now))
            with
        {
            FlightPlan = new FlightPlan
            {
                FlightPlanId = "corpus",
                Callsign = Callsign.Create("FHAST"),
                DepartureIcao = "LFPL",
                DestinationIcao = "LFPL",
                SourceIdentifier = "Test",
            },
            FlightRules = rules,
            Phase = phase,
        };

        return onGround
            ? session
            : session with { AircraftState = session.AircraftState with { IsOnGround = false } };
    }

    private static PilotIntentType Understand(string french, AtcSession session)
    {
        string canonical = TranscriptNormalizer.Normalize(FrenchTranscriptCanonicalizer.Canonicalize(french));
        return DeterministicPilotIntentParser.Parse(canonical, session).Type;
    }

    public static TheoryData<string, PilotIntentType> AtTheParking => new()
    {
        { "Lognes Sol, F-HAST, DR 400, parking Alpha deux, information India, vol local, demande roulage", PilotIntentType.RequestTaxi },
        { "F-HAST, vol VFR pour un départ contrôlé", PilotIntentType.RequestTaxi },
        { "F-HAST, demande la mise en route et le roulage", PilotIntentType.RequestTaxi },
        { "F-HAST, prêt à rouler", PilotIntentType.RequestTaxi },
        { "F-HAST, demande à rouler pour le point d'attente", PilotIntentType.RequestTaxi },
        { "F-HAST, VFR local, circuit de piste, demande roulage", PilotIntentType.RequestTaxi },
        { "F-HAST, bonjour, je souhaite effectuer un tour de piste", PilotIntentType.RequestTaxi },
        { "F-HAST, on voudrait rouler", PilotIntentType.RequestTaxi },
        { "F-HAST, au parking, prêt au roulage", PilotIntentType.RequestTaxi },
        { "F-HAST, départ VFR vers Melun, demande roulage", PilotIntentType.RequestTaxi },
        { "F-HAST, demande instructions de roulage", PilotIntentType.RequestTaxi },
        { "F-HAST, pour des tours de piste", PilotIntentType.RequestTaxi },
        { "F-HAST, demandons roulage pour départ VFR", PilotIntentType.RequestTaxi },
        { "F-HAST, je demande le roulage", PilotIntentType.RequestTaxi },
        { "F-HAST, vol local, information India, roulage", PilotIntentType.RequestTaxi },
        { "F-HAST, demande autorisation de rouler", PilotIntentType.RequestTaxi },
        { "F-HAST, demande de départ", PilotIntentType.RequestTaxi },
        { "Sol, F-HAST, bonjour, pour un vol VFR local", PilotIntentType.RequestTaxi },
        { "Piper Cherokee 3375, demande d'autorisation de rouler", PilotIntentType.RequestTaxi },
        { "Piper Cherokee 365 demande autorisation de rouler", PilotIntentType.RequestTaxi },
        { "Piper Cherokee 365, demande d'autorisation de roulage", PilotIntentType.RequestTaxi },
        { "F-HAST, radio check", PilotIntentType.RadioCheck },
        { "F-HAST, essai radio", PilotIntentType.RadioCheck },
    };

    [Theory]
    [MemberData(nameof(AtTheParking))]
    public void A_vfr_pilot_at_the_parking_is_understood(string french, PilotIntentType expected) =>
        Assert.Equal(expected, Understand(french, Session(FlightPhase.Preflight)));

    public static TheoryData<string, PilotIntentType> AtTheHoldingPoint => new()
    {
        { "F-HAST, au point d'attente piste zéro huit", PilotIntentType.ReportHoldingShort },
        { "F-HAST, point d'attente zéro huit", PilotIntentType.ReportHoldingShort },
        { "F-HAST, au point d'attente piste assignée, près au départ", PilotIntentType.ReadyForDeparture },
        { "F-HAST, au point d'attente, prêts au départ", PilotIntentType.ReadyForDeparture },
        { "F-HAST, prêt pour le départ", PilotIntentType.ReadyForDeparture },
        { "F-HAST, prêts à partir", PilotIntentType.ReadyForDeparture },
        { "F-HAST, prêt au décollage", PilotIntentType.ReadyForDeparture },
        { "F-HAST, demande l'alignement", PilotIntentType.ReadyForDeparture },
        { "F-HAST, demande décollage", PilotIntentType.ReadyForDeparture },
        { "F-HAST, demandons à nous aligner", PilotIntentType.ReadyForDeparture },
        { "F-HAST, prêt à l'alignement piste zéro huit", PilotIntentType.ReadyForDeparture },
        { "F-HAST, en bout de piste, prêt", PilotIntentType.ReadyForDeparture },
        { "F-HAST, prêt immédiat", PilotIntentType.ReadyForDeparture },
        { "F-HAST, prêt au départ circuit de piste", PilotIntentType.ReadyForDeparture },
        { "F-HAST, départ", PilotIntentType.ReadyForDeparture },
        { "F-HAST, on est prêts", PilotIntentType.ReadyForDeparture },
        { "F-HAST, point d'attente zéro huit, prêt", PilotIntentType.ReadyForDeparture },
    };

    [Theory]
    [MemberData(nameof(AtTheHoldingPoint))]
    public void A_vfr_pilot_at_the_holding_point_is_understood(string french, PilotIntentType expected) =>
        Assert.Equal(expected, Understand(french, Session(FlightPhase.HoldingShort)));

    public static TheoryData<string, PilotIntentType> AfterTheLanding => new()
    {
        { "F-HAST, piste dégagée", PilotIntentType.ReportRunwayVacated },
        { "F-HAST, piste libérée", PilotIntentType.ReportRunwayVacated },
        { "F-HAST, on a dégagé la piste", PilotIntentType.ReportRunwayVacated },
        { "F-HAST, demande roulage vers le parking", PilotIntentType.RequestTaxiToStand },
        { "F-HAST, on rentre au parking", PilotIntentType.RequestTaxiToStand },
        { "F-HAST, demande à rejoindre le parking", PilotIntentType.RequestTaxiToStand },
        { "F-HAST, roulage parking Alpha deux", PilotIntentType.RequestTaxiToStand },
    };

    [Theory]
    [MemberData(nameof(AfterTheLanding))]
    public void A_vfr_pilot_after_landing_is_understood(string french, PilotIntentType expected) =>
        Assert.Equal(expected, Understand(french, Session(FlightPhase.Landed)));

    public static TheoryData<string, PilotIntentType> AfterTakeoff => new()
    {
        { "F-HAST, en l'air", PilotIntentType.ReportAirborne },
        { "F-HAST, décollé", PilotIntentType.ReportAirborne },
        { "F-HAST, on vient de décoller", PilotIntentType.ReportAirborne },
        { "F-HAST, je quitte la fréquence", PilotIntentType.LeaveFrequency },
        { "F-HAST, sortie de circuit, quittons la fréquence", PilotIntentType.LeaveFrequency },
        { "F-HAST, quittant le circuit, bonne journée", PilotIntentType.LeaveFrequency },
        { "F-HAST, vent arrière", PilotIntentType.ReportDownwind },
        { "F-HAST, en vent arrière piste zéro huit", PilotIntentType.ReportDownwind },
        { "F-HAST, travers vent arrière main gauche", PilotIntentType.ReportDownwind },
        { "F-HAST, en base", PilotIntentType.ReportBase },
        { "F-HAST, dernier virage", PilotIntentType.ReportFinal },
        { "F-HAST, finale piste zéro huit", PilotIntentType.ReportFinal },
        { "F-HAST, courte finale", PilotIntentType.ReportFinal },
        { "F-HAST, demande un toucher", PilotIntentType.RequestTouchAndGo },
        { "F-HAST, pour un posé décollé", PilotIntentType.RequestTouchAndGo },
        { "F-HAST, remise de gaz", PilotIntentType.GoAround },
        { "F-HAST, on remet les gaz", PilotIntentType.GoAround },
        { "F-HAST, à la verticale d'Écho", PilotIntentType.ReportAtPoint },
        { "F-HAST, passage Écho", PilotIntentType.ReportAtPoint },
    };

    [Theory]
    [MemberData(nameof(AfterTakeoff))]
    public void A_vfr_pilot_in_the_air_is_understood(string french, PilotIntentType expected) =>
        Assert.Equal(expected, Understand(french, Session(FlightPhase.Circuit, onGround: false)));

    public static TheoryData<string, PilotIntentType> IfrAndGeneral => new()
    {
        // clearance delivery
        { "F-HAST, bonjour, demande la clairance IFR vers Lyon", PilotIntentType.RequestIFRClearance },
        { "Prévol, F-HAST, prêts à copier", PilotIntentType.RequestIFRClearance },
        { "F-HAST, demandons notre clairance", PilotIntentType.RequestIFRClearance },
        { "F-HAST, demande de clairance", PilotIntentType.RequestIFRClearance },
        { "F-HAST, je demande ma clairance IFR", PilotIntentType.RequestIFRClearance },
        // pushback and taxi
        { "F-HAST, demande repoussage", PilotIntentType.RequestPushback },
        { "F-HAST, demandons repoussage et mise en route", PilotIntentType.RequestPushback },
        { "F-HAST, prêt pour le repoussage", PilotIntentType.RequestPushback },
        { "F-HAST, repoussage queue vers la droite", PilotIntentType.RequestPushback },
        { "F-HAST, prêts à rouler", PilotIntentType.RequestTaxi },
        { "F-HAST, demandons le roulage", PilotIntentType.RequestTaxi },
        { "F-HAST, demande de roulage", PilotIntentType.RequestTaxi },
        { "F-HAST, demande à rouler", PilotIntentType.RequestTaxi },
        { "F-HAST, roulage s'il vous plaît", PilotIntentType.RequestTaxi },
        { "F-HAST, demande roulage guidé", PilotIntentType.RequestProgressiveTaxi },
        { "F-HAST, on est perdus", PilotIntentType.RequestProgressiveTaxi },
        // frequency and conversation
        { "F-HAST, quelle est la fréquence de la tour", PilotIntentType.RequestFrequency },
        { "F-HAST, demande la fréquence sol", PilotIntentType.RequestFrequency },
        { "F-HAST, répétez", PilotIntentType.SayAgain },
        { "F-HAST, pouvez-vous répéter", PilotIntentType.SayAgain },
        { "F-HAST, dites encore", PilotIntentType.SayAgain },
        { "F-HAST, reçu", PilotIntentType.PilotAcknowledgement },
        { "F-HAST, bien reçu", PilotIntentType.PilotAcknowledgement },
        { "F-HAST, wilco", PilotIntentType.PilotAcknowledgement },
        { "F-HAST, merci", PilotIntentType.PilotAcknowledgement },
        { "F-HAST, impossible", PilotIntentType.Unable },
        { "F-HAST, incapable", PilotIntentType.Unable },
        // emergencies
        { "Mayday mayday mayday, F-HAST, panne moteur", PilotIntentType.Mayday },
        { "Pan pan pan pan, F-HAST, problème technique", PilotIntentType.PanPan },
    };

    [Theory]
    [MemberData(nameof(IfrAndGeneral))]
    public void An_ifr_or_general_request_on_the_ground_is_understood(string french, PilotIntentType expected) =>
        Assert.Equal(expected, Understand(french, Session(FlightPhase.Preflight, FlightRules.Ifr)));

    public static TheoryData<string, PilotIntentType> IfrAirborne => new()
    {
        { "Départ, bonjour, F-HAST, passant deux mille pieds en montée vers niveau soixante", PilotIntentType.CheckIn },
        { "Paris Contrôle, F-HAST, bonjour, niveau cent vingt", PilotIntentType.CheckIn },
        { "F-HAST, avec vous, en montée vers trois mille", PilotIntentType.CheckIn },
        { "F-HAST, demande niveau cent quatre-vingts", PilotIntentType.RequestClimb },
        { "F-HAST, demande à monter", PilotIntentType.RequestClimb },
        { "F-HAST, demandons un niveau supérieur", PilotIntentType.RequestClimb },
        { "F-HAST, demande à descendre", PilotIntentType.RequestDescent },
        { "F-HAST, demandons un niveau inférieur", PilotIntentType.RequestDescent },
        { "F-HAST, demande direct NOVAR", PilotIntentType.RequestDirect },
        { "F-HAST, demandons un raccourci vers NOVAR", PilotIntentType.RequestDirect },
        { "F-HAST, demande évitement météo", PilotIntentType.RequestDeviation },
        { "F-HAST, demande l'approche ILS piste vingt-sept", PilotIntentType.RequestApproach },
        { "F-HAST, demandons l'ILS", PilotIntentType.RequestApproach },
        { "F-HAST, demande une approche à vue", PilotIntentType.RequestApproach },
        { "F-HAST, établis ILS piste vingt-sept", PilotIntentType.ReportEstablished },
        { "F-HAST, établi sur le localizer", PilotIntentType.ReportEstablished },
        { "F-HAST, remise de gaz", PilotIntentType.GoAround },
        { "F-HAST, approche interrompue", PilotIntentType.GoAround },
    };

    [Theory]
    [MemberData(nameof(IfrAirborne))]
    public void An_ifr_pilot_in_the_air_is_understood(string french, PilotIntentType expected) =>
        Assert.Equal(expected, Understand(french, Session(FlightPhase.Departure, FlightRules.Ifr, onGround: false)));
}
