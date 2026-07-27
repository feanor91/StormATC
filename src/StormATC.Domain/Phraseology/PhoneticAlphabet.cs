using System.Text;

namespace StormATC.Domain.Phraseology;

/// <summary>
/// The ICAO/NATO spelling alphabet, used for spelling taxiways, runway designators, and
/// registration-style callsigns.
/// </summary>
/// <remarks>
/// Spellings follow ICAO Annex 10 Volume II, including the aviation forms "Alfa" and "Juliett"
/// rather than the everyday English "Alpha" and "Juliet". The inbound lookup accepts both,
/// because speech-to-text will produce whichever spelling its language model prefers.
/// </remarks>
public static class PhoneticAlphabet
{
    private static readonly string[] Letters =
    [
        "Alfa", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India",
        "Juliett", "Kilo", "Lima", "Mike", "November", "Oscar", "Papa", "Quebec", "Romeo",
        "Sierra", "Tango", "Uniform", "Victor", "Whiskey", "Xray", "Yankee", "Zulu",
    ];

    /// <summary>Maps a spoken phonetic word back to its letter. Accepts common variants.</summary>
    private static readonly Dictionary<string, char> WordToLetter =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["alfa"] = 'A', ["alpha"] = 'A',
            ["bravo"] = 'B',
            ["charlie"] = 'C',
            ["delta"] = 'D',
            ["echo"] = 'E',
            ["foxtrot"] = 'F', ["fox"] = 'F',
            ["golf"] = 'G',
            ["hotel"] = 'H',
            ["india"] = 'I',
            ["juliett"] = 'J', ["juliet"] = 'J',
            ["kilo"] = 'K',
            ["lima"] = 'L',
            ["mike"] = 'M',
            ["november"] = 'N',
            ["oscar"] = 'O',
            ["papa"] = 'P',
            ["quebec"] = 'Q',
            ["romeo"] = 'R',
            ["sierra"] = 'S',
            ["tango"] = 'T',
            ["uniform"] = 'U',
            ["victor"] = 'V',
            ["whiskey"] = 'W', ["whisky"] = 'W',
            ["xray"] = 'X', ["x-ray"] = 'X',
            ["yankee"] = 'Y',
            ["zulu"] = 'Z',
        };

    /// <summary>Returns the phonetic word for a letter, or the character itself if not A–Z.</summary>
    public static string ForLetter(char letter)
    {
        char upper = char.ToUpperInvariant(letter);
        return upper is >= 'A' and <= 'Z'
            ? Letters[upper - 'A']
            : letter.ToString();
    }

    /// <summary>
    /// Spells a string phonetically. <c>"A1"</c> becomes <c>"Alfa one"</c>.
    /// </summary>
    public static string SpellOut(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var parts = new List<string>(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetter(c))
            {
                parts.Add(ForLetter(c));
            }
            else if (char.IsDigit(c))
            {
                parts.Add(SpokenNumbers.DigitsIndividually(c.ToString()));
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Converts phonetic words in free text back into letters, so that a pilot saying
    /// "via alpha and charlie" can be compared against the taxi route <c>[A, C]</c>.
    /// </summary>
    /// <remarks>
    /// Returns only the recognised letters, in order, as a compact string ("AC" in the example
    /// above). Non-phonetic words are ignored, which is what the readback comparator wants:
    /// it is looking for the set of taxiways mentioned, not for a full parse.
    /// </remarks>
    public static string ExtractLetters(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder();
        foreach (string token in SpokenNumbers.Tokenize(text))
        {
            if (WordToLetter.TryGetValue(token, out char letter))
            {
                builder.Append(letter);
            }
            else if (token.Length == 1 && char.IsLetter(token[0]))
            {
                // A bare letter — speech-to-text sometimes emits "A" instead of "Alfa".
                builder.Append(char.ToUpperInvariant(token[0]));
            }
        }

        return builder.ToString();
    }

    /// <summary>Attempts to resolve a single phonetic word to its letter.</summary>
    public static bool TryGetLetter(string word, out char letter) =>
        WordToLetter.TryGetValue(word, out letter);
}
