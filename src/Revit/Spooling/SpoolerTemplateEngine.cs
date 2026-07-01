using System.Text.RegularExpressions;

namespace SpoolTools.Revit.Spooling
{
    /// <summary>Token substitution engine for The Spooler's spool number,
    /// spool name, and sheet number templates. Tokens use <c>{Name}</c> or
    /// <c>{Name:format}</c> syntax and are case-insensitive. Unknown tokens
    /// are left in place so the live preview surfaces typos to the user
    /// rather than silently dropping content.
    ///
    /// Supported tokens:
    /// <list type="bullet">
    ///   <item><c>{Service}</c> — Fabrication Service abbreviation (e.g. CHWS).</item>
    ///   <item><c>{ServiceName}</c> — Fabrication Service full name.</item>
    ///   <item><c>{ID}</c> — User-typed Identifier field (batch-wide).</item>
    ///   <item><c>{N}</c> / <c>{N:fmt}</c> — Sequence number with optional
    ///   <c>int.ToString(fmt)</c> format (e.g. <c>{N:000}</c>, <c>{N:D4}</c>).
    ///   Aliases: <c>{Seq}</c>, <c>{Sequence}</c>.</item>
    ///   <item><c>{Number}</c> — The just-resolved spool number string;
    ///   only meaningful in the name template (where it's pre-populated
    ///   from the number template's output).</item>
    /// </list>
    /// </summary>
    public static class SpoolerTemplateEngine
    {
        // Greedy on the name (letters only) so {N:000} doesn't grab the
        // format spec into the name capture; non-greedy on the format
        // body so a closing brace ends it.
        private static readonly Regex TokenRx = new(
            @"\{([A-Za-z]+)(?::([^}]+))?\}",
            RegexOptions.Compiled);

        public static string Resolve(string template, TemplateContext ctx)
        {
            if (string.IsNullOrEmpty(template)) return string.Empty;
            return TokenRx.Replace(template, m =>
            {
                string name = m.Groups[1].Value;
                string fmt  = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;
                return Substitute(name, fmt, ctx) ?? m.Value;
            });
        }

        private static string? Substitute(string name, string fmt, TemplateContext ctx)
        {
            switch (name.ToLowerInvariant())
            {
                case "service":     return ctx.Service     ?? string.Empty;
                case "servicename": return ctx.ServiceName ?? string.Empty;
                case "id":          return ctx.Identifier  ?? string.Empty;
                case "n":
                case "seq":
                case "sequence":
                    return string.IsNullOrEmpty(fmt)
                        ? ctx.Sequence.ToString()
                        : ctx.Sequence.ToString(fmt);
                case "number":      return ctx.Number      ?? string.Empty;
                default:            return null;   // unknown — leave literal
            }
        }

        /// <summary>Generates a sequence of sheet numbers starting from
        /// <paramref name="start"/>. Trailing digits increment by 1 each
        /// step; padding is inferred from the leading zeros on the
        /// starting value (so <c>S1</c> → S1, S2, S3 and <c>S001</c> →
        /// S001, S002, S003). If no trailing digits are present the
        /// starting value is returned for every step.</summary>
        public static System.Collections.Generic.IEnumerable<string> SheetNumberSequence(
            string start, int count)
        {
            var m = Regex.Match(start ?? string.Empty, @"^(.*?)(\d+)$");
            if (!m.Success)
            {
                for (int i = 0; i < count; i++) yield return start ?? string.Empty;
                yield break;
            }
            string prefix = m.Groups[1].Value;
            string numStr = m.Groups[2].Value;
            int pad = numStr.Length;
            if (!int.TryParse(numStr, out int n)) n = 1;
            for (int i = 0; i < count; i++)
                yield return prefix + (n + i).ToString().PadLeft(pad, '0');
        }
    }

    /// <summary>Per-spool inputs to the token substitution. Service /
    /// ServiceName come from the spool's parts at walk time; Identifier
    /// is the batch-wide user input; Sequence is the auto-incremented
    /// counter; Number is only set when resolving the NAME template
    /// (it's the just-computed number from the NUMBER template).</summary>
    public readonly struct TemplateContext
    {
        public string? Service     { get; init; }
        public string? ServiceName { get; init; }
        public string? Identifier  { get; init; }
        public int     Sequence    { get; init; }
        public string? Number      { get; init; }
    }
}
