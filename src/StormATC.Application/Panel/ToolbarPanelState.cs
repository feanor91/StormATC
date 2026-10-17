using System.Globalization;
using StormATC.Domain;
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
    string Registration = "");

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
    string ChooseStage);

/// <summary>Builds the state the in-simulator panel shows from the current session.</summary>
public static class ToolbarPanelStateBuilder
{
    /// <summary>How many transmissions the panel keeps.</summary>
    public const int MaximumTurns = 80;

    /// <summary>Builds the panel state.</summary>
    /// <param name="session">The current session.</param>
    public static ToolbarPanelState Build(AtcSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        bool french = session.Language == PhraseologyLanguage.French;
        IAtcPhraseology say = AtcPhraseologies.For(session.Language);
        string callsign = session.Callsign is { } own ? own.Original : string.Empty;
        string spoken = session.Callsign is { } owned
            ? say.SpeakCallsign(owned)
            : french ? "l'indicatif" : "callsign";

        PhraseologyStage stage = PhraseologyCatalog.StageFor(session);
        PhraseologyGuide guide = PhraseologyCatalog.For(stage, session.Language, spoken);

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
            session.FlightRules == FlightRules.Vfr ? "VFR" : "IFR",
            new ToolbarPanelPhase(stage.ToString(), guide.Title),
            Labels(french),
            turns,
            new ToolbarPanelPhraseology(
                stage.ToString(),
                guide.Title,
                guide.Examples,
                PhraseologyCatalog.Always(session.Language, spoken),
                [
                    .. PhraseologyCatalog.Stages.Select(other =>
                    {
                        PhraseologyGuide otherGuide = PhraseologyCatalog.For(other, session.Language, spoken);
                        return new ToolbarPanelStage(other.ToString(), otherGuide.Title, otherGuide.Examples);
                    }),
                ]),
            string.IsNullOrWhiteSpace(session.AircraftState.AtcIdentifier)
                ? callsign
                : session.AircraftState.AtcIdentifier.Trim());
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
            "Choisir une phase")
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
            "Choose a phase");
}
