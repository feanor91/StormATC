using System.Globalization;
using StormATC.Application.Abstractions;
using StormATC.Application.Guidance;
using StormATC.Domain;
using StormATC.Domain.Airports;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;

namespace StormATC.Application.Panel;

/// <summary>What the in-simulator panel shows: the conversation and the phraseology guide.</summary>
/// <param name="Language">The radio language, <c>fr</c> or <c>en</c>.</param>
/// <param name="Callsign">The aircraft's callsign, or empty before a flight is loaded.</param>
/// <param name="Controller">The controller currently talking, such as "Tour".</param>
/// <param name="Frequency">The frequency in megahertz, or empty.</param>
/// <param name="Rules">The flight rules, <c>IFR</c> or <c>VFR</c>.</param>
/// <param name="Phase">The stage the flight is in.</param>
/// <param name="Labels">The panel's own texts, in the radio language.</param>
/// <param name="Turns">The most recent transmissions, oldest first.</param>
/// <param name="Phraseology">The calls for the current stage.</param>
/// <param name="Registration">
/// The registration of the aircraft being flown, as the simulator reports it, or the callsign
/// when it does not; the panel's window title.
/// </param>
/// <param name="Voice">The controller voice that spoke last, or null when there is none to judge.</param>
/// <param name="Departure">The departure aerodrome of the flight plan, or empty.</param>
/// <param name="Destination">The destination aerodrome of the flight plan, or empty.</param>
/// <param name="Military">The military page, in a CAM regime; null otherwise.</param>
/// <param name="Suggestion">What the pilot has to say next; null when the option is off.</param>
public sealed record ToolbarPanelState(
    string Language,
    string Callsign,
    string Controller,
    string Frequency,
    string Rules,
    ToolbarPanelPhase Phase,
    ToolbarPanelLabels Labels,
    IReadOnlyList<ToolbarPanelTurn> Turns,
    ToolbarPanelPhraseology Phraseology,
    string Registration = "",
    ToolbarPanelVoice? Voice = null,
    string Departure = "",
    string Destination = "",
    ToolbarPanelMilitary? Military = null,
    ToolbarPanelSuggestion? Suggestion = null);

/// <summary>What the pilot has to say next, shown under the dialogue when the option is on.</summary>
/// <param name="Heading">The label above it ("À dire", "What to say").</param>
/// <param name="Title">What the call is ("Read back the clearance").</param>
/// <param name="Detail">What is still required, or why there is nothing to say.</param>
/// <param name="Example">The words to say, or empty when there is nothing to say.</param>
public sealed record ToolbarPanelSuggestion(string Heading, string Title, string Detail, string Example);

/// <summary>A frequency of the aerodrome, as the military page lists it.</summary>
/// <param name="Service">The service (APP, TWR, PAR…).</param>
/// <param name="Frequency">The frequency in MHz.</param>
/// <param name="Remark">What the aerodrome says about it, or empty.</param>
public sealed record ToolbarPanelFrequency(string Service, string Frequency, string Remark);

/// <summary>The military page: the regime, the base's VHF frequencies, the radar approach and the calls.</summary>
/// <param name="Regime">CAM V or CAM I.</param>
/// <param name="Aerodrome">The base the flight is at, or empty.</param>
/// <param name="Frequencies">The VHF frequencies of the base.</param>
/// <param name="Radar">The state of a PAR or SRA in progress, or empty.</param>
/// <param name="Marsa">The MARSA in force, or empty.</param>
/// <param name="Sections">The military calls, by theme.</param>
/// <param name="Labels">The texts of the page.</param>
public sealed record ToolbarPanelMilitary(
    string Regime,
    string Aerodrome,
    IReadOnlyList<ToolbarPanelFrequency> Frequencies,
    string Radar,
    string Marsa,
    IReadOnlyList<ToolbarPanelStage> Sections,
    ToolbarPanelMilitaryLabels Labels);

/// <summary>The texts of the military page.</summary>
/// <param name="Tab">Title of the tab.</param>
/// <param name="Regime">Label before the regime.</param>
/// <param name="Frequencies">Title of the list of frequencies.</param>
/// <param name="NoFrequencies">Shown when the base's profile lists none.</param>
/// <param name="VhfOnly">The reminder that MSFS has no UHF radio.</param>
/// <param name="Radar">Title of the radar approach line.</param>
/// <param name="NoRadar">Shown when no radar approach is in progress.</param>
/// <param name="Marsa">Title of the MARSA line.</param>
/// <param name="NoMarsa">Shown when no MARSA is in force.</param>
public sealed record ToolbarPanelMilitaryLabels(
    string Tab,
    string Regime,
    string Frequencies,
    string NoFrequencies,
    string VhfOnly,
    string Radar,
    string NoRadar,
    string Marsa,
    string NoMarsa);

/// <summary>The controller voice that spoke last.</summary>
/// <param name="Description">A short description, such as <c>fr_FR-mls-medium, speaker 42</c>.</param>
/// <param name="CanReject">
/// False for the voice chosen in Settings, which is changed there rather than set aside.
/// </param>
public sealed record ToolbarPanelVoice(string Description, bool CanReject);

/// <summary>The stage of the flight.</summary>
/// <param name="Key">A stable identifier.</param>
/// <param name="Title">The stage's name, in the radio language.</param>
public sealed record ToolbarPanelPhase(string Key, string Title);

/// <summary>One transmission.</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="Time">The time of day, <c>HH:mm</c>, in local time.</param>
/// <param name="Speaker"><c>pilot</c>, <c>atc</c> or <c>system</c>.</param>
/// <param name="Who">The speaker as shown: "Vous", "Tour", …</param>
/// <param name="Text">What was said.</param>
public sealed record ToolbarPanelTurn(string Id, string Time, string Speaker, string Who, string Text);

/// <summary>The calls for the current stage and the ones that are always possible.</summary>
/// <param name="Stage">The stage's identifier.</param>
/// <param name="Title">The stage's name.</param>
/// <param name="Examples">The calls for this stage.</param>
/// <param name="Always">The calls that are possible at any stage.</param>
/// <param name="Stages">Every stage with its calls, so the pilot can read another one.</param>
public sealed record ToolbarPanelPhraseology(
    string Stage,
    string Title,
    IReadOnlyList<PhraseExample> Examples,
    IReadOnlyList<PhraseExample> Always,
    IReadOnlyList<ToolbarPanelStage> Stages);

/// <summary>One stage of the flight and its calls.</summary>
/// <param name="Key">A stable identifier.</param>
/// <param name="Title">The stage's name, in the radio language.</param>
/// <param name="Examples">The calls for this stage.</param>
public sealed record ToolbarPanelStage(string Key, string Title, IReadOnlyList<PhraseExample> Examples);

/// <summary>The panel's own texts.</summary>
/// <param name="TabDialogue">Title of the conversation tab.</param>
/// <param name="TabPhraseology">Title of the phraseology tab.</param>
/// <param name="You">What the pilot's side of a call is called.</param>
/// <param name="Controller">What the controller's side of a call is called.</param>
/// <param name="NoConversation">Shown while nothing has been said.</param>
/// <param name="Always">Title of the list of calls that always apply.</param>
/// <param name="CurrentStage">Label before the name of the current stage.</param>
/// <param name="Say">Label before what the pilot says.</param>
/// <param name="Answer">Label before what the controller answers.</param>
/// <param name="NoFlight">Shown while no flight has been loaded.</param>
/// <param name="Auto">The button that follows the flight's current stage.</param>
/// <param name="ChooseStage">Label above the stage buttons.</param>
/// <param name="Voice">Label before the name of the voice that spoke last.</param>
/// <param name="RejectVoice">The "I don't like this voice" button.</param>
/// <param name="VoiceSetAside">Confirmation after a voice was set aside.</param>
/// <param name="VoiceInSettings">Shown for the voice chosen in Settings.</param>
/// <param name="NoVoiceYet">Shown while no local voice has spoken.</param>
/// <param name="RejectFailed">Shown when the voice could not be set aside.</param>
/// <param name="Charts">The charts tab and its labels.</param>
public sealed record ToolbarPanelLabels(
    string TabDialogue,
    string TabPhraseology,
    string You,
    string Controller,
    string NoConversation,
    string Always,
    string CurrentStage,
    string Say,
    string Answer,
    string NoFlight,
    string Auto,
    string ChooseStage,
    string Voice = "",
    string RejectVoice = "",
    string VoiceSetAside = "",
    string VoiceInSettings = "",
    string NoVoiceYet = "",
    string RejectFailed = "",
    ToolbarPanelChartLabels? Charts = null);

/// <summary>The texts of the charts tab.</summary>
/// <param name="Tab">Title of the tab.</param>
/// <param name="Empty">Shown when the folder holds no chart; <c>{0}</c> is the folder.</param>
/// <param name="Back">The button back to the list.</param>
/// <param name="Page">The word before the page number.</param>
/// <param name="Of">The word between the page number and the page count.</param>
/// <param name="Previous">The previous page button.</param>
/// <param name="Next">The next page button.</param>
/// <param name="ZoomIn">The zoom in button.</param>
/// <param name="ZoomOut">The zoom out button.</param>
/// <param name="Fit">The button that fits the page to the window.</param>
/// <param name="Departure">Tag on the departure aerodrome's charts.</param>
/// <param name="Destination">Tag on the destination aerodrome's charts.</param>
/// <param name="Loading">Shown while a page is drawn.</param>
/// <param name="Failed">Shown when a chart could not be drawn.</param>
/// <param name="Unavailable">Shown when StormATC cannot list the charts.</param>
/// <param name="Recent">Title of the charts the pilot opened last.</param>
public sealed record ToolbarPanelChartLabels(
    string Tab,
    string Empty,
    string Back,
    string Page,
    string Of,
    string Previous,
    string Next,
    string ZoomIn,
    string ZoomOut,
    string Fit,
    string Departure,
    string Destination,
    string Loading,
    string Failed,
    string Unavailable,
    string Recent);

/// <summary>Builds the state the in-simulator panel shows from the current session.</summary>
public static class ToolbarPanelStateBuilder
{
    /// <summary>How many transmissions the panel keeps.</summary>
    public const int MaximumTurns = 80;

    /// <summary>Builds the panel state.</summary>
    /// <param name="session">The current session.</param>
    /// <param name="voice">The controller voice that spoke last, when the engine reports one.</param>
    /// <param name="profile">The VFR profile of the aerodrome the flight is at, when loaded: the base's frequencies.</param>
    /// <param name="showSuggestion">True to include what the pilot has to say next.</param>
    public static ToolbarPanelState Build(
        AtcSession session,
        SpokenVoice? voice = null,
        VfrAerodromeProfile? profile = null,
        bool showSuggestion = false)
    {
        ArgumentNullException.ThrowIfNull(session);

        bool french = session.Language == PhraseologyLanguage.French;
        IAtcPhraseology say = AtcPhraseologies.For(session.Language);
        string callsign = session.Callsign is { } own ? own.Original : string.Empty;
        string spoken = session.Callsign is { } owned
            ? say.SpeakCallsign(owned)
            : french ? "l'indicatif" : "callsign";

        PhraseologyStage stage = PhraseologyCatalog.StageFor(session);
        PhraseologyGuide guide = PhraseologyCatalog.For(stage, session.Language, spoken, session.FlightRules);

        string controller = session.CurrentController == ControllerPosition.None
            ? string.Empty
            : say.ControllerName(session.CurrentController);

        string frequency = session.CurrentFrequency is { } tuned
            ? tuned.ToDisplay()
            : string.Empty;

        ToolbarPanelTurn[] turns =
        [
            .. session.Conversation
                .TakeLast(MaximumTurns)
                .Select(turn => ToTurn(turn, say, french)),
        ];

        return new ToolbarPanelState(
            french ? "fr" : "en",
            callsign,
            controller,
            frequency,
            session.FlightRules.ToLabel(),
            new ToolbarPanelPhase(stage.ToString(), guide.Title),
            Labels(french),
            turns,
            new ToolbarPanelPhraseology(
                stage.ToString(),
                guide.Title,
                guide.Examples,
                PhraseologyCatalog.Always(session.Language, spoken, session.FlightRules),
                [
                    .. PhraseologyCatalog.Stages.Select(other =>
                    {
                        PhraseologyGuide otherGuide = PhraseologyCatalog.For(other, session.Language, spoken, session.FlightRules);
                        return new ToolbarPanelStage(other.ToString(), otherGuide.Title, otherGuide.Examples);
                    }),
                ]),
            string.IsNullOrWhiteSpace(session.AircraftState.AtcIdentifier)
                ? callsign
                : session.AircraftState.AtcIdentifier.Trim(),
            voice is null ? null : new ToolbarPanelVoice(voice.Description, voice.CanExclude),
            session.FlightPlan?.DepartureIcao ?? string.Empty,
            session.FlightPlan?.DestinationIcao ?? string.Empty,
            BuildMilitary(session, spoken, french, profile),
            showSuggestion ? BuildSuggestion(session, french) : null);
    }

    private static ToolbarPanelSuggestion BuildSuggestion(AtcSession session, bool french)
    {
        AtcGuidance guidance = AtcGuidanceAdvisor.ForSession(session);
        return new ToolbarPanelSuggestion(
            french ? "À dire" : "What to say",
            guidance.Title,
            guidance.Detail,
            guidance.Example);
    }

    private static ToolbarPanelMilitary? BuildMilitary(AtcSession session, string spoken, bool french, VfrAerodromeProfile? profile)
    {
        if (!session.FlightRules.IsMilitary())
        {
            return null;
        }

        ToolbarPanelFrequency[] frequencies =
        [
            .. (profile?.Frequencies ?? [])
                .Where(listed => listed.Frequency.Megahertz is >= 118m and <= 137m)
                .Select(listed => new ToolbarPanelFrequency(listed.Service, listed.Frequency.ToDisplay(), listed.Remark)),
        ];

        string radar = session.RadarApproach is { } approach
            ? (approach.Kind == Domain.Approach.RadarApproachKind.Par ? "PAR" : "SRA")
                + " " + (french ? "piste " : "runway ") + approach.Runway
                + " : " + (approach.Completed
                    ? (french ? "terminée" : "complete")
                    : approach.Established
                        ? (french ? "contact radar" : "radar contact")
                        : (french ? "en attente du contact radar" : "waiting for radar contact"))
            : string.Empty;

        return new ToolbarPanelMilitary(
            session.FlightRules.ToLabel(),
            profile is null ? string.Empty : string.IsNullOrWhiteSpace(profile.Name) ? profile.Icao : profile.Name + " (" + profile.Icao + ")",
            frequencies,
            radar,
            session.Marsa is { } marsa
                ? "MARSA " + (marsa.Kind == Domain.Approach.MarsaKind.Departure ? (french ? "départ" : "departure") : (french ? "approche" : "approach"))
                    + " : " + (french ? "patrouille de " : "flight of ") + marsa.FlightSize
                    + (marsa.TrailMiles is int trail ? (french ? ", trail " : ", trail ") + trail + " NM" : string.Empty)
                : string.Empty,
            [
                .. PhraseologyCatalog.MilitarySections(session.Language, spoken)
                    .Select(section => new ToolbarPanelStage(section.Key, section.Title, section.Examples)),
            ],
            french
                ? new ToolbarPanelMilitaryLabels(
                    "Militaire",
                    "Régime",
                    "Fréquences VHF de la base",
                    "Aucune fréquence dans le profil de cette base.",
                    "Les radios de MSFS vont de 118 à 137 MHz : les fréquences UHF et celles au-dessus de 137 MHz ne peuvent pas être accordées.",
                    "Approche radar",
                    "Aucune approche radar en cours.",
                    "MARSA",
                    "Aucun MARSA en cours.")
                : new ToolbarPanelMilitaryLabels(
                    "Military",
                    "Regime",
                    "VHF frequencies of the base",
                    "No frequency in this base's profile.",
                    "MSFS radios cover 118 to 137 MHz: UHF frequencies and those above 137 MHz cannot be tuned.",
                    "Radar approach",
                    "No radar approach in progress.",
                    "MARSA",
                    "No MARSA in force."));
    }

    private static ToolbarPanelTurn ToTurn(ConversationTurn turn, IAtcPhraseology say, bool french)
    {
        (string speaker, string who) = turn.Speaker switch
        {
            ConversationSpeaker.Pilot => ("pilot", french ? "Vous" : "You"),
            ConversationSpeaker.Controller => (
                "atc",
                turn.Controller == ControllerPosition.None ? "ATC" : say.ControllerName(turn.Controller)),
            _ => ("system", french ? "Système" : "System"),
        };

        return new ToolbarPanelTurn(
            turn.TurnId.ToString("N", CultureInfo.InvariantCulture),
            turn.Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
            speaker,
            who,
            turn.Text);
    }

    private static ToolbarPanelLabels Labels(bool french) => french
        ? new ToolbarPanelLabels(
            "Dialogue ATC",
            "Phraséologie",
            "Vous",
            "Contrôleur",
            "Aucun échange pour l'instant. Appuyez sur l'alternat et appelez le contrôleur.",
            "Toujours possible",
            "Phase en cours",
            "Vous dites",
            "Le contrôleur répond",
            "Aucun vol chargé : importez un plan de vol ou démarrez un vol VFR local dans StormATC.",
            "Auto",
            "Choisir une phase",
            "Voix",
            "Je n'aime pas cette voix",
            "Voix écartée : le contrôleur en aura une autre à sa prochaine transmission.",
            "C'est la voix choisie dans les paramètres : changez-la dans StormATC.",
            "Aucune voix locale n'a encore parlé.",
            "La voix n'a pas pu être écartée. Voir la page Diagnostic de StormATC.",
            new ToolbarPanelChartLabels(
                "Cartes",
                "Aucune carte. Téléchargez les cartes du SIA depuis les Paramètres de StormATC (Cartes VAC), ou déposez vos PDF dans le dossier : {0}",
                "< Cartes",
                "Page",
                "sur",
                "< Précédente",
                "Suivante >",
                "Zoom +",
                "Zoom −",
                "Ajuster",
                "Départ",
                "Arrivée",
                "Chargement de la carte…",
                "Cette carte n'a pas pu être affichée.",
                "StormATC ne peut pas lister les cartes.",
                "Dernières cartes"))
        : new ToolbarPanelLabels(
            "ATC dialogue",
            "Phraseology",
            "You",
            "Controller",
            "Nothing said yet. Press the push-to-talk key and call the controller.",
            "Always possible",
            "Current phase",
            "You say",
            "The controller answers",
            "No flight loaded: import a flight plan or start a local VFR flight in StormATC.",
            "Auto",
            "Choose a phase",
            "Voice",
            "I don't like this voice",
            "Voice set aside: the controller will use another one next time.",
            "This is the voice chosen in settings: change it in StormATC.",
            "No local voice has spoken yet.",
            "The voice could not be set aside. See StormATC's Diagnostics page.",
            new ToolbarPanelChartLabels(
                "Charts",
                "No chart. Download the SIA charts from StormATC's Settings (VAC charts), or put your PDFs in the folder: {0}",
                "< Charts",
                "Page",
                "of",
                "< Previous",
                "Next >",
                "Zoom +",
                "Zoom −",
                "Fit",
                "Departure",
                "Destination",
                "Loading the chart…",
                "This chart could not be drawn.",
                "StormATC cannot list the charts.",
                "Last charts"));
}
