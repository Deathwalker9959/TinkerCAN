using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace TinkerCAN.Lin
{
    /// <summary>
    /// Applies modifier programs (e.g. "D0=D0+1", "D[0..7]=D0^0xFF") to a data buffer.
    /// </summary>
    public static class Modifier
    {
        /// <summary>
        /// Apply all modifier statements to <paramref name="data"/> in-place.
        /// </summary>
        public static void Apply(string modifier, byte[] data)
        {
            int maxIdx = data.Length - 1;
            foreach (string line in EnumerateStatements(modifier))
            {
                // Spread: D[lo..hi]=expr
                var ms = Regex.Match(line, @"^D\[(\d+)\.\.(\d+)\]\s*=\s*(.+)$", RegexOptions.IgnoreCase);
                if (ms.Success)
                {
                    int lo = int.Parse(ms.Groups[1].Value);
                    int hi = int.Parse(ms.Groups[2].Value);
                    string expr = ms.Groups[3].Value;
                    for (int i = Math.Max(0, lo); i <= Math.Min(hi, maxIdx); i++)
                        try { data[i] = (byte)(Expr.Eval(expr, data) & 0xFF); } catch { }
                    continue;
                }

                // Single: Dx=expr
                var m = Regex.Match(line, @"^D(\d+)\s*=\s*(.+)$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                int idx = int.Parse(m.Groups[1].Value);
                if (idx < 0 || idx > maxIdx) continue;
                try { data[idx] = (byte)(Expr.Eval(m.Groups[2].Value, data) & 0xFF); } catch { }
            }
        }

        static IEnumerable<string> EnumerateStatements(string modifier)
        {
            var cleaned = new StringBuilder();
            foreach (string rawLine in modifier.Replace("\r", "").Split('\n'))
            {
                string line = rawLine;
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("//") || trimmed.StartsWith("#")) continue;
                int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIdx >= 0) line = line[..commentIdx];
                line = line.Trim();
                if (line.Length == 0) continue;
                if (cleaned.Length > 0) cleaned.Append(' ');
                cleaned.Append(line);
            }

            const string pattern = @"(?is)(?<stmt>D(?:\[\d+\.\.\d+\]|\d+)\s*=\s*.*?)(?=(?:\s*[,;]\s*|\s+)?D(?:\[\d+\.\.\d+\]|\d+)\s*=|\s*$)";
            foreach (Match match in Regex.Matches(cleaned.ToString(), pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string stmt = match.Groups["stmt"].Value.Trim().TrimEnd(',', ';');
                if (stmt.Length > 0) yield return stmt;
            }
        }
    }
}
