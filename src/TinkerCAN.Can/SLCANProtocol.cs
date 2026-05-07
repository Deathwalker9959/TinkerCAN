using System;
using System.Linq;
using System.Text;

namespace TinkerCAN.Can
{
    /// <summary>
    /// SLCAN protocol helpers for WeAct USB2CANFD and compatible adapters.
    /// All frame builders return ASCII byte arrays ready to write to the serial port.
    /// </summary>
    public static class SLCANProtocol
    {
        static byte[] Cmd(string s) => Encoding.ASCII.GetBytes(s + "\r");

        // ── Control commands ─────────────────────────────────────────────────

        public static byte[] Open()              => Cmd("O");
        public static byte[] Close()             => Cmd("C");
        public static byte[] GetVersion()        => Cmd("V");
        public static byte[] GetError()          => Cmd("E");
        public static byte[] SetMode(bool silent)           => Cmd(silent ? "M1" : "M0");
        public static byte[] SetAutoRetransmit(bool enable) => Cmd(enable ? "A1" : "A0");

        // ── Bit-rate presets ─────────────────────────────────────────────────

        static readonly (string Label, string Code)[] NomRates =
        {
            ("5k","SD"), ("10k","S0"), ("20k","S1"), ("33.3k","SC"),
            ("50k","S2"), ("62.5k","SB"), ("75k","SA"), ("83.3k","S9"),
            ("100k","S3"), ("125k","S4"), ("250k","S5"), ("500k","S6"),
            ("800k","S7"), ("1M","S8"),
        };

        static readonly (string Label, string Code)[] FdRates =
        {
            ("1M","Y1"), ("2M","Y2"), ("3M","Y3"), ("4M","Y4"), ("5M","Y5"),
        };

        public static string[] NomRateLabels => NomRates.Select(r => r.Label).ToArray();
        public static string[] FdRateLabels  => FdRates.Select(r => r.Label).ToArray();
        public static int DefaultNomIdx => 9;  // 125k
        public static int DefaultFdIdx  => 1;  // 2M

        public static byte[] SetNomRate(int idx) =>
            idx >= 0 && idx < NomRates.Length ? Cmd(NomRates[idx].Code) : Cmd("S4");

        public static byte[] SetFdRate(int idx) =>
            idx >= 0 && idx < FdRates.Length ? Cmd(FdRates[idx].Code) : Cmd("Y2");

        // ── CANFD DLC ↔ byte-length mapping ─────────────────────────────────

        static readonly int[] FdDlcBytes = { 0,1,2,3,4,5,6,7,8,12,16,20,24,32,48,64 };

        public static int BytesToFdDlc(int byteLen)
        {
            for (int i = 0; i < FdDlcBytes.Length; i++)
                if (FdDlcBytes[i] >= byteLen) return i;
            return 15;
        }

        public static int FdDlcToBytes(int dlc) =>
            (uint)dlc < (uint)FdDlcBytes.Length ? FdDlcBytes[dlc] : 64;

        // ── Frame builders ───────────────────────────────────────────────────

        static string H(byte[] d, int n) => string.Concat(d.Take(n).Select(b => b.ToString("X2")));

        public static byte[] SendStd  (int id, byte[] data, int dlc) => Cmd($"t{id & 0x7FF:X3}{dlc:X}{H(data, dlc)}");
        public static byte[] SendExt  (int id, byte[] data, int dlc) => Cmd($"T{id & 0x1FFFFFFF:X8}{dlc:X}{H(data, dlc)}");
        public static byte[] RemoteStd(int id, int dlc)              => Cmd($"r{id & 0x7FF:X3}{dlc:X}");
        public static byte[] RemoteExt(int id, int dlc)              => Cmd($"R{id & 0x1FFFFFFF:X8}{dlc:X}");

        public static byte[] SendFdStd(int id, byte[] data, int byteLen, bool brs)
        { int d = BytesToFdDlc(byteLen); return Cmd($"{(brs ? 'b' : 'd')}{id & 0x7FF:X3}{d:X}{H(data, FdDlcToBytes(d))}"); }

        public static byte[] SendFdExt(int id, byte[] data, int byteLen, bool brs)
        { int d = BytesToFdDlc(byteLen); return Cmd($"{(brs ? 'B' : 'D')}{id & 0x1FFFFFFF:X8}{d:X}{H(data, FdDlcToBytes(d))}"); }

        // ── Frame parser ─────────────────────────────────────────────────────

        public record RxFrame(
            string Type, int Id, bool Extended, int Dlc,
            int ByteLen, byte[] Data, bool IsRemote, bool IsFd, bool Brs);

        public static RxFrame? ParseFrame(string s)
        {
            if (s.Length < 2) return null;
            char c = s[0];
            bool ext = c == 'T' || c == 'R' || c == 'D' || c == 'B';
            bool fd  = c == 'd' || c == 'D' || c == 'b' || c == 'B';
            bool brs = c == 'b' || c == 'B';
            bool rem = c == 'r' || c == 'R';
            if (c != 't' && c != 'T' && c != 'r' && c != 'R' &&
                c != 'd' && c != 'D' && c != 'b' && c != 'B') return null;

            int idLen = ext ? 8 : 3;
            if (s.Length < 1 + idLen + 1) return null;
            if (!int.TryParse(s.Substring(1, idLen),
                    System.Globalization.NumberStyles.HexNumber, null, out int id)) return null;
            if (!int.TryParse(s.Substring(1 + idLen, 1),
                    System.Globalization.NumberStyles.HexNumber, null, out int dlc)) return null;

            int byteLen = fd ? FdDlcToBytes(dlc) : Math.Min(dlc, 8);
            int ds = 1 + idLen + 1;
            var data = new byte[byteLen];
            for (int i = 0; i < byteLen && ds + i * 2 + 1 < s.Length; i++)
                byte.TryParse(s.Substring(ds + i * 2, 2),
                    System.Globalization.NumberStyles.HexNumber, null, out data[i]);

            string type = fd ? (brs ? "FD+BRS" : "FD") : (rem ? "Remote" : "Data");
            return new RxFrame(type, id, ext, dlc, byteLen, data, rem, fd, brs);
        }
    }
}
