using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StormATC.Domain.Phraseology;

namespace StormATC.Application.Intent;

/// <summary>
/// Rewrites a French pilot transmission into the canonical English keywords and digit runs the
/// deterministic parser and readback evaluator already understand.
/// </summary>
/// <remarks>
/// <para>
/// French radiotelephony uses everyday cardinal numbers ("trois mille cinq cents",
/// "transpondeur trente-deux cinquante-quatre", "cent dix-huit décimale sept") and its own
/// phrases ("roulons point d'attente piste vingt-sept via Bravo", "prêts au départ"). Rather than
/// duplicating every parser rule in French, this class translates the transmission into the
/// shape of the English phraseology: digits for numbers and the parser's English keywords for
/// phrases. Parsing, readback checking and every safety decision then run unchanged.
/// </para>
/// <para>
/// The canonical form is used only for interpretation. The transcript the pilot sees, and the
/// one stored in the conversation, stays the French text that was heard.
/// </para>
/// </remarks>
public static class FrenchTranscriptCanonicalizer
{
    /// <summary>
    /// Phrase rewrites applied in order, on accent-free lowercase text. More specific phrases
    /// come first so that, for example, "maintenons avant la piste" is a hold-short and not a
    /// level report.
    /// </summary>
    private static readonly (Regex Pattern, string Replacement)[] PhraseRules = BuildPhraseRules(
    [
        // --- Spelling alphabet variants split by the hyphen fold. ----------------------------
        ("fox trot", "foxtrot"),
        ("x ray", "xray"),
        ("juliette", "juliett"),
        ("novembre", "november"),
        ("uniforme", "uniform"),
        ("zoulou", "zulu"),

        // --- French military regimes (CAM V, CAM I), as the speech recogniser tends to write them. ---
        ("cam(?:e)? (?:victor|v|5)", "cam victor"),
        ("cam(?:e)? (?:india|inde|i|1)", "cam india"),
        ("cam(?:e)? (?:tango|t)(?= |$)", "cam tango"),
        ("(?:demande(?:ons)? )?(?:la |le |l )?(?:situation tactique|situation|point de situation|image tactique)", "request picture"),
        ("demande(?:ons)? (?:un |une |le |l )?marsa depart", "request marsa departure"),
        ("demande(?:ons)? (?:un |une |le |l )?marsa (?:arrivee|approche)", "request marsa approach"),
        ("(?:patrouille|formation) de", "flight of"),
        ("(?:en )?trail (?:a |de )?", "trail "),

        // --- Military circuit phraseology (Instruction 1950 DSAE/DIRCAM). -----------------------------
        ("trains? sortis? (?:et )?verrouilles?|train sorti", "gear down and locked"),
        ("dernier virage", "turning final"),
        ("demande(?:ons)? (?:le |un |l )?break|pour (?:le |un |l )?break", "request break"),
        ("(?<!montee )(?:point initial|initiale|initial)", "initial"),
        ("q f e|que f e|cue f e", "qfe"),
        // "par" is a French stop word, so a PAR request is rewritten to words that survive.
        ("demande(?:ons)? (?:un |une |le |l )?(?:p a r|par)(?= |$)|p a r|approche (?:radar )?(?:de )?precision|approche par", "request precision radar"),
        ("demande(?:ons)? (?:un |une |le |l )?(?:s r a|sra)(?= |$)|s r a|approche (?:radar )?(?:de )?surveillance|approche sra", "request surveillance radar"),
        ("(?:piste|terrain) en vue|je vois la piste|piste vue", "runway in sight"),

        // --- Courtesy and filler. -------------------------------------------------------------
        ("s il vous plait|s il te plait|svp", "please"),
        ("au revoir|bonne journee|bonne soiree|bon vol|a plus tard|a bientot", ","),
        ("bonjour|bonsoir|salut", "hello"),
        ("a vous|termine", ","),

        // --- Emergencies and go-around. -------------------------------------------------------
        ("pan pan", "panpan"),
        ("(?:on |nous |je )?(?:remise des gaz|remise de gaz|remettons les gaz|remet les gaz|remets les gaz|remettons)", "going around"),
        ("approche interrompue", "missed approach"),

        // --- Conversational controls. ---------------------------------------------------------
        ("(?:pouvez vous |vous pouvez |veuillez |pourriez vous )?(?:repetez|repeter|redites|redire)|dites a nouveau|dites encore|encore une fois|pardon", "say again"),
        ("essai radio|verification radio|comment (?:me )?recevez vous|vous me recevez", "radio check"),
        ("quelle est la frequence|quelle frequence|la frequence please|frequence please", "what is the frequency"),
        ("frequence (?:du |de la |de l )?sol", "ground frequency"),
        ("frequence (?:du |de la |de l )?tour", "tower frequency"),
        ("frequence (?:du |de la |de l )?depart", "departure frequency"),
        ("frequence (?:du |de la |de l )?approche", "approach frequency"),

        // --- Ready / request forms. -----------------------------------------------------------
        ("(?:demande|demandons|demander)(?: a| de)? (?:traverser|la traversee|traversee)", "request to cross"),
        ("(?:pret|prets)(?: a| pour)? traverser", "ready to cross"),
        ("(?:nous sommes |on est |je suis )?(?:prets|pretes|pret|prete) (?:a copier|pour copier)", "ready to copy"),
        ("(?:nous sommes |on est |je suis )?(?:prets|pretes|pret|prete) (?:a rouler|pour rouler)", "ready to taxi"),
        // Speech recognition often writes "prêts" as "près" or "prés", and pilots also say
        // "prêt à partir" or "prêts à décoller": all of them are "ready for departure".
        ("(?:nous sommes |on est |je suis )?(?:prets|pretes|pret|prete|pres|pre) (?:a )?(?:partir|decoller|le depart|au depart|pour le depart|l alignement|alignement|s aligner|aligner)", "ready for departure"),
        ("(?:nous sommes |on est |je suis )?(?:prets|pretes|pret|prete) (?:au|aux|pour le|pour la|pour l|pour)", "ready for"),
        ("(?:nous sommes |on est |je suis )?(?:prets|pretes|pret|prete)(?: immediat)?", "ready"),
        // "Demande d'autorisation de rouler" asks to taxi: the permission is part of the request.
        ("(?:d |de |l )?(?:autorisation|permission)(?: de| d| a| pour)?(?= (?:rouler|roulage|decoller|decollage|atterrir|atterrissage|s aligner|alignement|repoussage|demarrer|mise en route|monter|descendre))", ""),
        ("(?:demande|demandons|je demande|on demande|nous demandons|demander) (?:a |de )?rouler", "request taxi"),
        ("(?:demande|demandons|je demande|on demande|nous demandons|demander) (?:a |de )?monter", "request climb"),
        ("(?:demande|demandons|je demande|on demande|nous demandons|demander) (?:a |de )?descendre", "request descent"),
        ("(?:demande|demandons|je demande|on demande|nous demandons|demander) (?:l )?ils", "request the ils"),
        ("(?:demande|demandons|je demande|on demande|nous demandons|demander|demandez)", "request"),
        ("(?:niveau|altitude) superieur|plus haut", "higher"),
        ("(?:niveau|altitude) inferieur|plus bas", "lower"),

        // --- Clearance delivery. --------------------------------------------------------------
        ("(?:notre |ma |mon |votre |la |une |de |de la )?clairance", "clearance"),
        ("niveau de vol", "flight level"),
        ("(?:mise en route et repoussage|repoussage et mise en route)", "pushback and start"),
        ("mise en route", "start"),

        // --- Ground. --------------------------------------------------------------------------
        ("(?:le |un )?repoussage|repousser|push back", "pushback"),
        ("queue (?:vers la |vers |a |sur la )?gauche", "tail left"),
        ("queue (?:vers la |vers |a |sur la )?droite", "tail right"),
        ("face (?:au |a l |a )?nord", "facing north"),
        ("face (?:au |a l |a )?sud", "facing south"),
        ("face (?:au |a l |a )?est", "facing east"),
        ("face (?:au |a l |a )?ouest", "facing west"),
        ("roulage (?:guide|guidee|assiste|assistee)|(?:un )?guidage", "progressive taxi"),
        ("roulage progressif|guidage (?:au |de |du )?roulage|guidage au sol|roulage detaille|instructions de roulage detaillees|(?:nous sommes|on est|je suis) perdus?", "progressive taxi"),
        ("(?:roulage|roulons|rouler|roule|roulez|on roule|je roule) (?:vers le |vers la |vers |au |jusqu au |pour le )?(?:parking|poste de stationnement|poste|porte)", "taxi to stand"),
        ("(?:on |nous |je )?(?:rentre|rentrons|rejoindre|rejoins|rejoignons|regagner|regagnons) (?:au |le |vers le |a la |la )?(?:parking|poste de stationnement|poste|porte)", "taxi to stand"),
        ("poste de stationnement|parking", "stand"),
        // "Point d'arrêt" was the old wording; the 2017 manual says "point d'attente", but pilots still say both.
        ("(?:au|nous sommes au|on est au|sommes au) point d (?:attente|arret)", "at the holding point"),
        ("point d (?:attente|arret)", "holding point"),
        ("(?:on maintient|nous maintenons|je maintiens|maintenons|maintiens|maintient|maintenir|maintenez|on reste|restons) avant", "hold short of"),
        ("(?:on traverse|nous traversons|traversons|traverser|traversez)", "crossing"),

        // --- Tower. ---------------------------------------------------------------------------
        ("(?:alignons nous|on s aligne|alignons|aligne|alignement|alignez vous) et (?:on )?(?:attendons|attend|attente|attendre|attendez)", "line up and wait"),
        // --- VFR circuit and arrival (aerodrome circuit phraseology). ----------------------------
        ("main gauche", "left hand"),
        ("main droite", "right hand"),
        ("(?:je |on |nous )?(?:rappelle|rappelons)", "reporting"),
        ("(?:j |je |on |nous )?(?:entre|entrons)", "joining"),
        ("fin de vent arriere", "end of downwind"),
        ("vent arriere", "downwind"),
        ("vent traversier", "crosswind"),
        ("(?:dernier virage|courte finale|longue finale|en finale|finale)", "final leg"),
        ("(?:en )?base", "base leg"),
        ("atterrissage complet|arret complet|(?:pour )?un complet", "full stop"),
        ("(?:pour )?(?:une |l )?option", "option"),
        ("(?:pour )?(?:un )?passage bas", "low pass"),
        ("(?:pour )?(?:un |le )?(?:toucher|touche|pose decolle|pose decoller|touch and go)", "touch and go"),
        ("sortie de circuit|quittant le circuit|quitte le circuit", "leaving circuit"),
        ("vol local", "local flight"),
        ("(?:consignes?|instructions?) d (?:integration|arrivee)", "joining instructions"),
        ("(?:estime|estimee|estimes)", "estimate"),
        ("(?:a la )?verticale(?: de| du| d)?", "overhead"),

        ("(?:a )?(?:l )?alignement|(?:a )?(?:nous |m )?aligner|s aligner", "line up"),
        ("tours? de piste", "circuit"),
        ("(?:autorises|autorisees|autorisee|autorise) (?:au |a l |pour le |pour l |a )?decollage", "cleared for takeoff"),
        ("(?:autorises|autorisees|autorisee|autorise) (?:a l |pour l |a )?(?:atterrissage|atterrir|poser|se poser)", "cleared to land"),
        ("(?:autorises|autorisees|autorisee|autorise)(?: a| pour| jusqu a)?", "cleared to"),
        ("(?:nous avons |on a |avons |j ai |a |ai )?(?:degage|libere) (?:de )?(?:la )?piste|piste (?:degagee|liberee)", "runway vacated"),
        ("(?:nous sommes |on est |sommes )?en l air|(?:nous avons |on a |avons |j ai )?decolle|(?:on |nous |je )?(?:vient|viens|venons) de decoller", "airborne"),
        ("(?:nous sommes |on est |sommes )?(?:etablis|etablies|etablie|etabli)", "established"),

        // --- Radar. ---------------------------------------------------------------------------
        ("(?:un )?(?:evitement|contournement)", "deviation"),
        ("(?:un )?raccourci(?: vers| jusqu a| pour)?", "direct to"),
        ("incapable|nous sommes incapables", "unable"),
        ("avec vous", "with you"),
        ("(?:on est |nous sommes |sommes )?en montee", "checking in climbing"),
        ("(?:on est |nous sommes |sommes )?en descente", "checking in descending"),
        ("(?:on |nous |je )?(?:montons|monte|montee initiale)", "climbing"),
        ("(?:on |nous |je )?(?:descendons|descends|descend)", "descending"),
        ("(?:on |nous |je )?(?:maintenons|maintiens|maintient)", "maintaining"),
        ("pour (?:quitter|sortir)", "leaving frequency"),
        ("(?:on |nous |je )?(?:quittons|quitte)", "leaving"),
        ("(?:on |nous |je )?(?:tournons|tourne|tournez|virons|virage)", "turn"),
        ("a gauche|gauche", "left"),
        ("a droite|droite", "right"),
        ("(?:on |nous |je )?(?:contactons|contacte|contacter|passons avec|passe avec|passons sur|passe sur)", "contact"),
        ("avec (?:l )?information|information", "information"),
        ("(?:on |nous |je )?(?:passons|passe)|passage(?! bas)", "passing"),

        // --- Acknowledgements. ----------------------------------------------------------------
        ("bien recu|c est note|recu|note|compris|rogger|roger", "roger"),
        ("j ecoute", "go ahead"),
        ("stand by|standby", "stand by"),
        ("merci beaucoup|merci", "thanks"),
    ]);

    /// <summary>Single-word vocabulary applied after the phrases.</summary>
    private static readonly Dictionary<string, string> WordMap = new(StringComparer.Ordinal)
    {
        ["piste"] = "runway",
        ["pistes"] = "runway",
        ["niveau"] = "flight level",
        ["fl"] = "flight level",
        ["pieds"] = "feet",
        ["pied"] = "feet",
        ["noeuds"] = "knots",
        ["noeud"] = "knots",
        ["cap"] = "heading",
        ["transpondeur"] = "squawk",
        ["transpo"] = "squawk",
        ["code"] = "squawk",
        ["frequence"] = "frequency",
        ["decimale"] = "decimal",
        ["virgule"] = "decimal",
        ["passant"] = "passing",
        ["initialement"] = "initially",
        ["initiale"] = "initial",
        ["monter"] = "climb",
        ["montez"] = "climb",
        ["montee"] = "climb",
        ["descendre"] = "descend",
        ["descendez"] = "descend",
        ["descente"] = "descent",
        ["maintenir"] = "maintain",
        ["maintenez"] = "maintain",
        ["roulage"] = "taxi",
        ["roulons"] = "taxi",
        ["rouler"] = "taxi",
        ["roule"] = "taxi",
        ["roulez"] = "taxi",
        ["decollage"] = "takeoff",
        ["atterrissage"] = "landing",
        ["approche"] = "approach",
        ["depart"] = "departure",
        ["prevol"] = "delivery",
        ["sol"] = "ground",
        ["tour"] = "tower",
        ["controle"] = "center",
        ["centre"] = "center",
        ["directe"] = "direct",
        ["meteo"] = "weather",
        ["loc"] = "localizer",
        ["localiseur"] = "localizer",
        ["visuelle"] = "visual",
        ["approuve"] = "approved",
        ["approuvee"] = "approved",
        ["impossible"] = "unable",
        ["negatif"] = "negative",
        ["affirme"] = "affirm",
        ["wilco"] = "will comply",
        ["contactez"] = "contact",
        ["urgence"] = "emergency",
        ["et"] = "and",
        ["pour"] = "for",
        ["vers"] = "to",
        ["jusqu"] = "to",
    };

    /// <summary>French function words with no meaning for the parser.</summary>
    private static readonly HashSet<string> DroppedWords = new(StringComparer.Ordinal)
    {
        "le", "la", "les", "l", "du", "des", "de", "d", "une", "au", "aux", "a",
        "nous", "on", "je", "j", "notre", "nos", "votre", "vos", "sommes", "avons", "ai",
        "est", "c", "qu", "que", "en", "sur", "par", "ce", "cette", "s", "y", "vous",
    };

    /// <summary>
    /// Words after which a lone "un" is a digit ("piste zéro un", "Alpha un"), not an article.
    /// </summary>
    private static readonly HashSet<string> NumberContextWords = new(StringComparer.Ordinal)
    {
        "runway", "level", "heading", "squawk", "frequency", "decimal", "stand", "gate",
        "qnh", "altitude", "climb", "descend", "climbing", "descending", "maintain",
        "maintaining", "passing", "leaving", "feet", "direct", "via", "information",
        "alfa", "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "fox", "golf", "hotel",
        "india", "juliett", "juliet", "kilo", "lima", "mike", "november", "oscar", "papa",
        "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "whisky",
        "xray", "yankee", "zulu",
    };

    /// <summary>The written flight level "FL120", which speech engines sometimes produce.</summary>
    private static readonly Regex WrittenFlightLevel =
        new(@"(?<![a-z0-9])fl ?(?=[0-9])", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, (NumberWordKind Kind, int Value)> NumberWords =
        new(StringComparer.Ordinal)
        {
            ["zero"] = (NumberWordKind.Zero, 0),
            ["un"] = (NumberWordKind.Unit, 1),
            ["unite"] = (NumberWordKind.Unit, 1),
            ["deux"] = (NumberWordKind.Unit, 2),
            ["trois"] = (NumberWordKind.Unit, 3),
            ["quatre"] = (NumberWordKind.Unit, 4),
            ["cinq"] = (NumberWordKind.Unit, 5),
            ["six"] = (NumberWordKind.Unit, 6),
            ["sept"] = (NumberWordKind.Unit, 7),
            ["huit"] = (NumberWordKind.Unit, 8),
            ["neuf"] = (NumberWordKind.Unit, 9),
            ["dix"] = (NumberWordKind.Teen, 10),
            ["onze"] = (NumberWordKind.Teen, 11),
            ["douze"] = (NumberWordKind.Teen, 12),
            ["treize"] = (NumberWordKind.Teen, 13),
            ["quatorze"] = (NumberWordKind.Teen, 14),
            ["quinze"] = (NumberWordKind.Teen, 15),
            ["seize"] = (NumberWordKind.Teen, 16),
            ["vingt"] = (NumberWordKind.Tens, 20),
            ["vingts"] = (NumberWordKind.Tens, 20),
            ["trente"] = (NumberWordKind.Tens, 30),
            ["quarante"] = (NumberWordKind.Tens, 40),
            ["cinquante"] = (NumberWordKind.Tens, 50),
            ["soixante"] = (NumberWordKind.Tens, 60),
            ["septante"] = (NumberWordKind.Tens, 70),
            ["huitante"] = (NumberWordKind.Tens, 80),
            ["octante"] = (NumberWordKind.Tens, 80),
            ["nonante"] = (NumberWordKind.Tens, 90),
            ["cent"] = (NumberWordKind.Hundred, 100),
            ["cents"] = (NumberWordKind.Hundred, 100),
            ["mille"] = (NumberWordKind.Thousand, 1000),
            ["mil"] = (NumberWordKind.Thousand, 1000),
        };

    private enum NumberWordKind
    {
        Zero,
        Unit,
        Teen,
        Tens,
        Hundred,
        Thousand,
    }

    /// <summary>
    /// Returns the canonical form of a French transmission: English parser keywords and digit
    /// runs. The result is meant for <see cref="TranscriptNormalizer.Normalize"/>, not display.
    /// </summary>
    public static string Canonicalize(string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        if (string.IsNullOrWhiteSpace(transcript))
        {
            return string.Empty;
        }

        string text = Fold(transcript);
        text = WrittenFlightLevel.Replace(text, "flight level ");

        foreach ((Regex pattern, string replacement) in PhraseRules)
        {
            text = pattern.Replace(text, replacement);
        }

        List<string> tokens = Tokenize(text);
        tokens = MapWords(tokens);
        tokens = ConvertNumbers(tokens);
        tokens = SpellRunwaySides(tokens);
        tokens = DropFunctionWords(tokens);

        string result = string.Join(' ', tokens);
        result = SpokenFrequencyDigits(result);
        result = PreferReadyForDeparture(result);
        result = AddCheckInMarker(result);
        return CollapseSpaces(result);
    }

    private static readonly Regex FrequencyWithSpokenDigits = new(
        @"\b(1(?:1[89]|2\d|3[0-7]))((?: \d){1,3})(?![\d.])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// "Contactez Tour cent dix-huit sept cinq" gives 118 7 5: with no "décimale" said, the digits
    /// after a VHF frequency's MHz part are its decimals. Applied only to a frequency change or
    /// request, so a heading, a code or a callsign number is left alone.
    /// </summary>
    private static string SpokenFrequencyDigits(string text) =>
        text.Contains("contact", StringComparison.Ordinal) || text.Contains("frequency", StringComparison.Ordinal)
            ? FrequencyWithSpokenDigits.Replace(text, "$1 decimal$2")
            : text;

    /// <summary>
    /// A runway side said as a letter ("piste vingt-sept L", "piste zéro neuf R") would be dropped as
    /// a French article; it is spelled out here so the parser sees "runway 27 left".
    /// </summary>
    private static List<string> SpellRunwaySides(List<string> tokens)
    {
        for (int index = 0; index < tokens.Count; index++)
        {
            if (tokens[index] != "runway")
            {
                continue;
            }

            int next = index + 1;
            while (next < tokens.Count && next <= index + 2 && tokens[next].Length > 0 && tokens[next].All(char.IsAsciiDigit))
            {
                next++;
            }

            if (next > index + 1 && next < tokens.Count)
            {
                tokens[next] = tokens[next] switch
                {
                    "l" => "left",
                    "r" => "right",
                    "c" => "center",
                    var other => other,
                };
            }
        }

        return tokens;
    }

    /// <summary>
    /// Lowercases, removes accents, and turns apostrophes and hyphens into word breaks. A comma
    /// between digits is the French decimal separator ("118,7") and becomes a point.
    /// </summary>
    internal static string Fold(string transcript)
    {
        string decomposed = transcript.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        for (int index = 0; index < decomposed.Length; index++)
        {
            char c = decomposed[index];
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            char lower = char.ToLowerInvariant(c);
            bool betweenDigits =
                index > 0
                && index + 1 < decomposed.Length
                && char.IsAsciiDigit(decomposed[index - 1])
                && char.IsAsciiDigit(decomposed[index + 1]);

            switch (lower)
            {
                case 'œ':
                    builder.Append("oe");
                    break;
                case 'æ':
                    builder.Append("ae");
                    break;
                case ',' or '.' when betweenDigits:
                    builder.Append('.');
                    break;
                case ',' or ';' or ':' or '!' or '?' or '.':
                    builder.Append(" , ");
                    break;
                case '\'' or '’' or '‘' or '-' or '‐' or '‑' or '–' or '—':
                    builder.Append(' ');
                    break;
                default:
                    if (char.IsLetterOrDigit(lower))
                    {
                        builder.Append(lower);
                    }
                    else if (char.IsWhiteSpace(lower))
                    {
                        // Thin and non-breaking spaces group thousands in French ("3 500").
                        builder.Append(' ');
                    }
                    else
                    {
                        builder.Append(' ');
                    }

                    break;
            }
        }

        return CollapseSpaces(builder.ToString());
    }

    private static List<string> Tokenize(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static List<string> MapWords(List<string> tokens)
    {
        var mapped = new List<string>(tokens.Count);
        foreach (string token in tokens)
        {
            if (WordMap.TryGetValue(token, out string? replacement))
            {
                mapped.AddRange(replacement.Split(' '));
            }
            else
            {
                mapped.Add(token);
            }
        }

        return mapped;
    }

    /// <summary>
    /// Turns French number words into digits. Each complete French number becomes one digit
    /// group, so "trente-deux cinquante-quatre" becomes "32 54" and "deux sept zéro" becomes
    /// "2 7 0"; the parser then reads either as a digit run, exactly as it reads "two seven zero".
    /// </summary>
    private static List<string> ConvertNumbers(List<string> tokens)
    {
        var output = new List<string>(tokens.Count);
        var number = new FrenchNumberBuilder();

        for (int index = 0; index < tokens.Count; index++)
        {
            string token = tokens[index];

            // "vingt et un", "soixante et onze": the connector belongs to the number.
            if (token == "and"
                && number.CanTakeEtConnector
                && index + 1 < tokens.Count
                && tokens[index + 1] is "un" or "onze")
            {
                continue;
            }

            if (!NumberWords.TryGetValue(token, out (NumberWordKind Kind, int Value) word))
            {
                Flush(number, output);
                output.Add(token);
                continue;
            }

            if (token == "un" && !number.IsStarted && IsArticle(tokens, index, output))
            {
                Flush(number, output);
                continue;
            }

            if (!number.TryAdd(word.Kind, word.Value))
            {
                Flush(number, output);
                number.Start(word.Kind, word.Value);
            }
        }

        Flush(number, output);
        return output;

        static void Flush(FrenchNumberBuilder builder, List<string> target)
        {
            if (builder.IsStarted)
            {
                target.Add(builder.Value.ToString(CultureInfo.InvariantCulture));
                builder.Reset();
            }
        }
    }

    /// <summary>
    /// A lone "un" is an article ("demandons un roulage") unless numbers surround it or a word
    /// that announces a number precedes it ("piste zéro un", "Alpha un").
    /// </summary>
    private static bool IsArticle(List<string> tokens, int index, List<string> output)
    {
        string? previous = output.Count > 0 ? output[^1] : null;
        if (previous is not null
            && (previous.All(char.IsAsciiDigit) || NumberContextWords.Contains(previous)))
        {
            return false;
        }

        string? next = index + 1 < tokens.Count ? tokens[index + 1] : null;
        if (next is not null
            && (next.All(char.IsAsciiDigit) || NumberWords.ContainsKey(next)))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// The standard French departure call names the holding point first: "au point d'attente
    /// piste vingt-sept, prêts au départ". The request is the departure; the position only
    /// confirms it, so the holding-point phrase is removed and the runway kept.
    /// </summary>
    private static string PreferReadyForDeparture(string text)
    {
        if (!text.Contains("ready for departure", StringComparison.Ordinal)
            && !text.Contains("ready for takeoff", StringComparison.Ordinal))
        {
            return text;
        }

        return text
            .Replace("at the holding point", string.Empty, StringComparison.Ordinal)
            .Replace("holding point", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// A French check-in is "bonjour" followed by the level ("Paris Départ, bonjour, Air France
    /// cent vingt-trois, passant deux mille pieds"). Mark it as one so the parser does not read
    /// a greeting with a level as a bare initial call.
    /// </summary>
    private static string AddCheckInMarker(string text)
    {
        if (!text.Contains("hello", StringComparison.Ordinal)
            || text.Contains("checking in", StringComparison.Ordinal))
        {
            return text;
        }

        bool reportsLevel =
            text.Contains("climbing", StringComparison.Ordinal)
            || text.Contains("descending", StringComparison.Ordinal)
            || text.Contains("maintaining", StringComparison.Ordinal);

        bool asksForSomething =
            text.Contains("request", StringComparison.Ordinal)
            || text.Contains("ready", StringComparison.Ordinal)
            || text.Contains("clearance", StringComparison.Ordinal)
            || text.Contains("holding point", StringComparison.Ordinal)
            || text.Contains("stand", StringComparison.Ordinal);

        return reportsLevel && !asksForSomething ? $"{text} checking in" : text;
    }

    /// <summary>
    /// Removes French articles and pronouns, except a single letter that names a taxiway
    /// ("via A", "via Alpha, B") — the article "à" and the taxiway "A" fold to the same token.
    /// </summary>
    private static List<string> DropFunctionWords(List<string> tokens)
    {
        var kept = new List<string>(tokens.Count);
        foreach (string token in tokens)
        {
            if (DroppedWords.Contains(token))
            {
                string? previous = kept.Count > 0 ? kept[^1] : null;
                bool namesTaxiway =
                    token.Length == 1
                    && previous is not null
                    && (previous == "via"
                        || (previous.Length == 1 && char.IsAsciiLetter(previous[0]))
                        || PhoneticAlphabet.TryGetLetter(previous, out _));

                if (!namesTaxiway)
                {
                    continue;
                }
            }

            kept.Add(token);
        }

        return kept;
    }

    private static string CollapseSpaces(string value) =>
        string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static (Regex, string)[] BuildPhraseRules((string Pattern, string Replacement)[] rules) =>
        rules
            .Select(rule => (
                new Regex(
                    $"(?<![a-z0-9])(?:{rule.Pattern})(?![a-z0-9])",
                    RegexOptions.CultureInvariant),
                rule.Replacement))
            .ToArray();

    /// <summary>
    /// Accumulates one French cardinal number, word by word, following French number grammar.
    /// </summary>
    private sealed class FrenchNumberBuilder
    {
        private int _thousands = -1;
        private int _hundreds;
        private bool _hasHundreds;
        private int _tens;
        private bool _tensOpen;
        private bool _tensTakesTeen;
        private int _units;
        private bool _hasUnits;
        private bool _lastWasUnitWord;
        private bool _lastWasDix;
        private bool _closed;

        public bool IsStarted { get; private set; }

        public bool CanTakeEtConnector => IsStarted && _tensOpen && !_hasUnits;

        public int Value =>
            (_thousands >= 0 ? _thousands * 1000 : 0) + _hundreds * 100 + _tens + _units;

        private int GroupValue => _hundreds * 100 + _tens + _units;

        private bool GroupHasRoomForTens =>
            (_hasHundreds || _thousands >= 0) && _tens == 0 && !_hasUnits && !_tensOpen;

        public void Reset()
        {
            _thousands = -1;
            _hundreds = 0;
            _hasHundreds = false;
            _tens = 0;
            _tensOpen = false;
            _tensTakesTeen = false;
            _units = 0;
            _hasUnits = false;
            _lastWasUnitWord = false;
            _lastWasDix = false;
            _closed = false;
            IsStarted = false;
        }

        public void Start(NumberWordKind kind, int value)
        {
            Reset();
            IsStarted = true;

            switch (kind)
            {
                case NumberWordKind.Zero:
                    _closed = true;
                    break;
                case NumberWordKind.Unit:
                    _units = value;
                    _hasUnits = true;
                    _lastWasUnitWord = true;
                    break;
                case NumberWordKind.Teen:
                    _units = value;
                    _hasUnits = true;
                    _lastWasDix = value == 10;
                    break;
                case NumberWordKind.Tens:
                    OpenTens(value);
                    break;
                case NumberWordKind.Hundred:
                    _hundreds = 1;
                    _hasHundreds = true;
                    break;
                case NumberWordKind.Thousand:
                    _thousands = 1;
                    break;
            }
        }

        public bool TryAdd(NumberWordKind kind, int value)
        {
            if (!IsStarted || _closed)
            {
                return false;
            }

            bool lastWasUnitWord = _lastWasUnitWord;
            bool lastWasDix = _lastWasDix;
            _lastWasUnitWord = false;
            _lastWasDix = false;

            switch (kind)
            {
                case NumberWordKind.Unit:
                    // "dix-sept", "soixante-dix-huit", "quatre-vingt-dix-neuf".
                    if (lastWasDix && value >= 7)
                    {
                        _units += value;
                        return true;
                    }

                    if (_tensOpen && !_hasUnits)
                    {
                        _units = value;
                        _hasUnits = true;
                        _tensOpen = false;
                        _lastWasUnitWord = true;
                        return true;
                    }

                    // "cent deux", "mille cinq (cents)".
                    if ((_hasHundreds || _thousands >= 0) && _tens == 0 && !_hasUnits && !_tensOpen)
                    {
                        _units = value;
                        _hasUnits = true;
                        _lastWasUnitWord = true;
                        return true;
                    }

                    return Restore(lastWasUnitWord, lastWasDix);

                case NumberWordKind.Teen:
                    // "soixante-dix", "quatre-vingt-douze".
                    if (_tensOpen && _tensTakesTeen && !_hasUnits)
                    {
                        _tens += value;
                        _tensOpen = false;
                        _hasUnits = true;
                        _lastWasDix = value == 10;
                        return true;
                    }

                    if (GroupHasRoomForTens)
                    {
                        _units = value;
                        _hasUnits = true;
                        _lastWasDix = value == 10;
                        return true;
                    }

                    return Restore(lastWasUnitWord, lastWasDix);

                case NumberWordKind.Tens:
                    // "quatre-vingt(s)": the four multiplies the twenty.
                    if (value == 20 && lastWasUnitWord && _units == 4 && _tens == 0)
                    {
                        _units = 0;
                        _hasUnits = false;
                        OpenTens(80);
                        return true;
                    }

                    if (GroupHasRoomForTens)
                    {
                        OpenTens(value);
                        return true;
                    }

                    return Restore(lastWasUnitWord, lastWasDix);

                case NumberWordKind.Hundred:
                    // "trois cents", "mille deux cent".
                    if (lastWasUnitWord && !_hasHundreds && _tens == 0 && _units is >= 2 and <= 9)
                    {
                        _hundreds = _units;
                        _hasHundreds = true;
                        _units = 0;
                        _hasUnits = false;
                        return true;
                    }

                    if (_thousands >= 0 && !_hasHundreds && _tens == 0 && !_hasUnits && !_tensOpen)
                    {
                        _hundreds = 1;
                        _hasHundreds = true;
                        return true;
                    }

                    return Restore(lastWasUnitWord, lastWasDix);

                case NumberWordKind.Thousand:
                    if (_thousands < 0 && GroupValue is >= 1 and <= 999)
                    {
                        _thousands = GroupValue;
                        _hundreds = 0;
                        _hasHundreds = false;
                        _tens = 0;
                        _tensOpen = false;
                        _tensTakesTeen = false;
                        _units = 0;
                        _hasUnits = false;
                        return true;
                    }

                    return Restore(lastWasUnitWord, lastWasDix);

                default:
                    return Restore(lastWasUnitWord, lastWasDix);
            }
        }

        private bool Restore(bool lastWasUnitWord, bool lastWasDix)
        {
            _lastWasUnitWord = lastWasUnitWord;
            _lastWasDix = lastWasDix;
            return false;
        }

        private void OpenTens(int value)
        {
            _tens = value;
            _tensOpen = true;
            _tensTakesTeen = value is 60 or 80;
        }
    }
}
