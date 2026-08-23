using System;
using System.Collections.Generic;

namespace UrDatabase.Services
{
    /// <summary>
    /// Turns whatever a source calls a language into the two-letter code shown on a badge, and
    /// back into a name for the tooltip beside it.
    /// </summary>
    /// <remarks>
    /// Three spellings of the same thing reach this app. Jellyfin reports ISO 639-2 — <c>eng</c>,
    /// <c>fre</c>, and sometimes <c>fra</c>, because that language has two three-letter codes and
    /// which one turns up depends on how the file was tagged. Filenames spell it out in English,
    /// as <c>FRENCH</c> or <c>ITA</c>. The badge has room for two characters. Without one table
    /// answering all of it, the same film shows <c>FRE</c> in one place and <c>FR</c> in another
    /// and reads as two different languages.
    ///
    /// Codes not in the table are not dropped. An unknown three-letter tag is shown as its first
    /// two letters upper-cased, which is right far more often than it is wrong and is in any case
    /// better than silently hiding a track the film actually has. Only its tooltip admits that
    /// nothing more is known about it.
    ///
    /// The table covers the languages a film library in the wild actually carries. It is not an
    /// ISO 639 implementation and does not pretend to be.
    /// </remarks>
    public static class LanguageTag
    {
        /// <summary>Shown for a track whose language the source declined to state.</summary>
        public const string UnknownCode = "UND";

        /// <summary>
        /// Every spelling this app has seen, mapped to a two-letter code and an English name.
        /// Keyed case-insensitively, so <c>ENG</c> and <c>eng</c> are the same entry.
        /// </summary>
        private static readonly Dictionary<string, (string Code, string Name)> Known =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = ("EN", "English"),
                ["eng"] = ("EN", "English"),
                ["english"] = ("EN", "English"),

                // Both ISO 639-2 codes for French are in the wild, and a file tagged with the
                // bibliographic one must not read as a different language from the same film
                // tagged with the terminological one.
                ["fr"] = ("FR", "French"),
                ["fre"] = ("FR", "French"),
                ["fra"] = ("FR", "French"),
                ["french"] = ("FR", "French"),
                ["vf"] = ("FR", "French"),
                ["truefrench"] = ("FR", "French"),

                ["de"] = ("DE", "German"),
                ["ger"] = ("DE", "German"),
                ["deu"] = ("DE", "German"),
                ["german"] = ("DE", "German"),

                ["es"] = ("ES", "Spanish"),
                ["spa"] = ("ES", "Spanish"),
                ["esp"] = ("ES", "Spanish"),
                ["spanish"] = ("ES", "Spanish"),
                ["castellano"] = ("ES", "Spanish"),
                ["latino"] = ("ES", "Spanish (Latin American)"),

                ["it"] = ("IT", "Italian"),
                ["ita"] = ("IT", "Italian"),
                ["italian"] = ("IT", "Italian"),

                ["pt"] = ("PT", "Portuguese"),
                ["por"] = ("PT", "Portuguese"),
                ["portuguese"] = ("PT", "Portuguese"),

                ["nl"] = ("NL", "Dutch"),
                ["dut"] = ("NL", "Dutch"),
                ["nld"] = ("NL", "Dutch"),
                ["dutch"] = ("NL", "Dutch"),

                ["ja"] = ("JA", "Japanese"),
                ["jpn"] = ("JA", "Japanese"),
                ["japanese"] = ("JA", "Japanese"),

                ["ko"] = ("KO", "Korean"),
                ["kor"] = ("KO", "Korean"),
                ["korean"] = ("KO", "Korean"),

                ["zh"] = ("ZH", "Chinese"),
                ["chi"] = ("ZH", "Chinese"),
                ["zho"] = ("ZH", "Chinese"),
                ["cmn"] = ("ZH", "Mandarin"),
                ["yue"] = ("ZH", "Cantonese"),
                ["chinese"] = ("ZH", "Chinese"),
                ["mandarin"] = ("ZH", "Mandarin"),
                ["cantonese"] = ("ZH", "Cantonese"),

                ["ru"] = ("RU", "Russian"),
                ["rus"] = ("RU", "Russian"),
                ["russian"] = ("RU", "Russian"),

                ["hi"] = ("HI", "Hindi"),
                ["hin"] = ("HI", "Hindi"),
                ["hindi"] = ("HI", "Hindi"),

                ["ar"] = ("AR", "Arabic"),
                ["ara"] = ("AR", "Arabic"),
                ["arabic"] = ("AR", "Arabic"),

                ["sv"] = ("SV", "Swedish"),
                ["swe"] = ("SV", "Swedish"),
                ["swedish"] = ("SV", "Swedish"),

                ["da"] = ("DA", "Danish"),
                ["dan"] = ("DA", "Danish"),
                ["danish"] = ("DA", "Danish"),

                ["no"] = ("NO", "Norwegian"),
                ["nor"] = ("NO", "Norwegian"),
                ["nob"] = ("NO", "Norwegian"),
                ["norwegian"] = ("NO", "Norwegian"),

                ["fi"] = ("FI", "Finnish"),
                ["fin"] = ("FI", "Finnish"),
                ["finnish"] = ("FI", "Finnish"),

                ["is"] = ("IS", "Icelandic"),
                ["ice"] = ("IS", "Icelandic"),
                ["isl"] = ("IS", "Icelandic"),

                ["pl"] = ("PL", "Polish"),
                ["pol"] = ("PL", "Polish"),
                ["polish"] = ("PL", "Polish"),

                ["cs"] = ("CS", "Czech"),
                ["cze"] = ("CS", "Czech"),
                ["ces"] = ("CS", "Czech"),
                ["czech"] = ("CS", "Czech"),

                ["sk"] = ("SK", "Slovak"),
                ["slo"] = ("SK", "Slovak"),
                ["slk"] = ("SK", "Slovak"),

                ["hu"] = ("HU", "Hungarian"),
                ["hun"] = ("HU", "Hungarian"),
                ["hungarian"] = ("HU", "Hungarian"),

                ["ro"] = ("RO", "Romanian"),
                ["rum"] = ("RO", "Romanian"),
                ["ron"] = ("RO", "Romanian"),

                ["el"] = ("EL", "Greek"),
                ["gre"] = ("EL", "Greek"),
                ["ell"] = ("EL", "Greek"),
                ["greek"] = ("EL", "Greek"),

                ["tr"] = ("TR", "Turkish"),
                ["tur"] = ("TR", "Turkish"),
                ["turkish"] = ("TR", "Turkish"),

                ["he"] = ("HE", "Hebrew"),
                ["heb"] = ("HE", "Hebrew"),
                ["hebrew"] = ("HE", "Hebrew"),

                ["th"] = ("TH", "Thai"),
                ["tha"] = ("TH", "Thai"),
                ["thai"] = ("TH", "Thai"),

                ["vi"] = ("VI", "Vietnamese"),
                ["vie"] = ("VI", "Vietnamese"),

                ["uk"] = ("UK", "Ukrainian"),
                ["ukr"] = ("UK", "Ukrainian"),

                ["id"] = ("ID", "Indonesian"),
                ["ind"] = ("ID", "Indonesian"),

                ["fa"] = ("FA", "Persian"),
                ["per"] = ("FA", "Persian"),
                ["fas"] = ("FA", "Persian"),

                ["bg"] = ("BG", "Bulgarian"),
                ["bul"] = ("BG", "Bulgarian"),

                ["hr"] = ("HR", "Croatian"),
                ["hrv"] = ("HR", "Croatian"),

                ["sr"] = ("SR", "Serbian"),
                ["srp"] = ("SR", "Serbian"),

                ["ca"] = ("CA", "Catalan"),
                ["cat"] = ("CA", "Catalan"),

                ["ta"] = ("TA", "Tamil"),
                ["tam"] = ("TA", "Tamil"),

                ["te"] = ("TE", "Telugu"),
                ["tel"] = ("TE", "Telugu"),

                ["ms"] = ("MS", "Malay"),
                ["may"] = ("MS", "Malay"),
                ["msa"] = ("MS", "Malay"),

                ["la"] = ("LA", "Latin"),
                ["lat"] = ("LA", "Latin"),

                // Jellyfin's own marker for a track with no language tag at all, and the two
                // spellings mediainfo produces for the same thing.
                ["und"] = (UnknownCode, "Undetermined"),
                ["undetermined"] = (UnknownCode, "Undetermined"),
                ["unknown"] = (UnknownCode, "Undetermined"),
                ["mul"] = ("MUL", "Multiple languages"),
                ["multi"] = ("MUL", "Multiple languages"),
                ["zxx"] = ("ZXX", "No spoken content"),
            };

        /// <summary>True when this text is a language this app recognises by name or by code.</summary>
        public static bool IsKnown(string? value) =>
            !string.IsNullOrWhiteSpace(value) && Known.ContainsKey(value.Trim());

        /// <summary>
        /// The badge text for a language, or null when there is nothing usable to show. An
        /// unrecognised tag is abbreviated rather than discarded — see the remarks on the class.
        /// </summary>
        public static string? Code(string? value)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;

            if (Known.TryGetValue(trimmed, out var known)) return known.Code;

            // Anything longer than a code is a word this table has never heard of, and its first
            // two letters are a guess rather than an abbreviation. Better to say nothing.
            if (trimmed.Length is < 2 or > 3) return null;

            return trimmed.ToUpperInvariant()[..2];
        }

        /// <summary>
        /// The English name of a language, for the tooltip. Falls back to the tag exactly as the
        /// source wrote it, which at least tells the user what their file claims.
        /// </summary>
        public static string Name(string? value)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return "";

            return Known.TryGetValue(trimmed, out var known) ? known.Name : trimmed.ToUpperInvariant();
        }
    }
}
