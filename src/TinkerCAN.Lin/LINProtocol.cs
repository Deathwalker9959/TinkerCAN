using System;
using System.Linq;

namespace TinkerCAN.Lin
{
    /// <summary>
    /// LIN 2.x protocol helpers — parity, checksums, and 16-byte dongle frame builders.
    /// </summary>
    public static class LINProtocol
    {
        static byte Bit(byte a, byte b) => (byte)((a >> b) & 1);

        public static byte CalcParity(byte id)
        {
            byte p0 = (byte)((Bit(id, 0) ^ Bit(id, 1) ^ Bit(id, 2) ^ Bit(id, 4)) << 6);
            byte p1 = (byte)((~(Bit(id, 1) ^ Bit(id, 3) ^ Bit(id, 4) ^ Bit(id, 5)) & 1) << 7);
            return (byte)(id | p0 | p1);
        }

        /// <param name="enhanced">true = V2 enhanced (PID + data), false = V1 classic (data only)</param>
        public static byte CalcChecksum(byte id, byte[] data, int length, bool enhanced)
        {
            uint sum = 0;
            if (enhanced) sum += CalcParity(id);
            for (int i = 0; i < length; i++)
            {
                sum += data[i];
                if ((sum & 0xFF00) != 0) sum = (sum & 0xFF) + 1;
            }
            return (byte)(sum ^ 0xFF);
        }

        /// <summary>
        /// Two's-complement packet checksum so all 16 dongle bytes sum to 0 mod 256.
        /// </summary>
        public static byte PacketChecksum(byte[] frame, int length)
        {
            int sum = 0;
            for (int i = 0; i < length; i++) sum += frame[i];
            return (byte)((~sum & 0xFF) + 1);
        }

        // ── 16-byte dongle frame builders ─────────────────────────────────────

        static byte[] Base16() => new byte[16];
        static byte CsFlag(bool enhanced) => enhanced ? (byte)2 : (byte)1;

        /// <summary>0x11 — configure device mode / LIN baud rate.</summary>
        public static byte[] ModeCommand(int mode, int linBaud, int volume = 28, int offlineTime = 100)
        {
            var f = Base16();
            f[0] = 0x11; f[1] = (byte)mode;
            f[2] = (byte)((linBaud >> 8) & 0xFF);
            f[3] = (byte)(linBaud & 0xFF);
            f[4] = (byte)volume;
            f[5] = (byte)((offlineTime >> 8) & 0xFF);
            f[6] = (byte)(offlineTime & 0xFF);
            f[15] = PacketChecksum(f, 15);
            return f;
        }

        /// <summary>0x22 — master send (host → slave).</summary>
        public static byte[] HostSend(byte id, byte[] data, int length, bool enhanced)
        {
            if (length > 8) throw new ArgumentException("LIN max data length is 8");
            var f = Base16();
            f[0] = 0x22; f[2] = id;
            f[4] = CsFlag(enhanced);
            f[5] = (byte)length;
            for (int i = 0; i < length; i++) f[6 + i] = data[i];
            f[14] = CalcChecksum(id, data, length, enhanced);
            f[15] = PacketChecksum(f, 15);
            return f;
        }

        /// <summary>0x33 — master header, request slave response.</summary>
        public static byte[] ReadSlave(byte id, int length, bool enhanced)
        {
            var f = Base16();
            f[0] = 0x33; f[1] = 1; f[2] = id; f[3] = 1;
            f[4] = CsFlag(enhanced);
            f[5] = (byte)length;
            f[15] = PacketChecksum(f, 15);
            return f;
        }

        /// <summary>0x55 — update slave response data.</summary>
        public static byte[] SlaveUpdate(byte id, int channel, byte[] data, int length, bool enhanced)
        {
            if (length > 8) throw new ArgumentException("LIN max data length is 8");
            var f = Base16();
            f[0] = 0x55; f[1] = (byte)channel; f[2] = id;
            f[4] = CsFlag(enhanced);
            f[5] = (byte)length;
            for (int i = 0; i < length; i++) f[6 + i] = data[i];
            f[14] = CalcChecksum(id, data, length, enhanced);
            f[15] = PacketChecksum(f, 15);
            return f;
        }

        public static string FrameHex(byte[] frame) =>
            string.Join(" ", frame.Select(b => b.ToString("X2")));

        /// <summary>
        /// Decode a 16-byte dongle response frame to a human-readable string.
        /// </summary>
        public static string DecodeFrame(byte[] f)
        {
            if (f.Length < 16) return "(short frame)";
            string cmd = f[0] switch
            {
                0x22 => "SEND", 0x33 => "READ", 0x55 => "SLAVE", 0x11 => "MODE",
                _ => $"CMD={f[0]:X2}"
            };
            byte id = f[2];
            int len = f[5];
            string cs = f[4] == 2 ? "V2" : "V1";
            string data = string.Join(" ", f.Skip(6).Take(Math.Min(len, 8)).Select(b => b.ToString("X2")));
            string pid = CalcParity(id).ToString("X2");
            return $"{cmd,-5} | ID={id:X2}(PID={pid}) | Len={len} | CS={cs} | Data=[{data}] | LINCheck={f[14]:X2}";
        }

        /// <summary>
        /// Validate a received 16-byte dongle response frame (checksum + command byte).
        /// </summary>
        public static bool ValidateResponseFrame(byte[] f)
        {
            if (f.Length < 16) return false;
            // Valid dongle response commands
            if (f[0] != 0x33 && f[0] != 0x44 && f[0] != 0x55 && f[0] != 0xDD) return false;
            int sum = 0;
            for (int i = 0; i < 15; i++) sum += f[i];
            byte expected = (byte)((~sum & 0xFF) + 1);
            return f[15] == expected;
        }
    }
}
