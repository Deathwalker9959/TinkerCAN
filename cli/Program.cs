// lintestcli — LIN bus command-line utility for LINTest-MI hardware
// Protocol: 16-byte framing over USB serial (CH340 or compatible)
//
// Usage:
//   lintestcli --port COM3 [command] [options]
//
// Commands:
//   ports                             List available serial ports
//   send    --id <hex> --data <hex bytes> [--len N] [--cs v1|v2]
//   read    --id <hex> [--len N] [--cs v1|v2]
//   monitor [--timeout N]             Dump incoming frames to stdout
//   mode    --mode N [--baud N]        Set device operating mode
//   brute   --type send|read [--data <hex bytes>] [--len N] [--cs v1|v2]
//           [--start <hex>] [--end <hex>] [--delay N] [--id-filter <hex list>]
//   interactive                        REPL prompt

using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;

namespace LINTestCLI
{
    // ─────────────────────────────────────────────────────────────────────────
    // LIN protocol helpers (ported from Form1.cs)
    // ─────────────────────────────────────────────────────────────────────────
    static class LIN
    {
        // Extract bit B from byte A
        public static byte Bit(byte a, byte b) => (byte)((a >> b) & 1);

        // LIN 2.x PID parity (adds bits 6 & 7)
        public static byte CalcParity(byte id)
        {
            byte p0 = (byte)((Bit(id, 0) ^ Bit(id, 1) ^ Bit(id, 2) ^ Bit(id, 4)) << 6);
            byte p1 = (byte)((~(Bit(id, 1) ^ Bit(id, 3) ^ Bit(id, 4) ^ Bit(id, 5)) & 1) << 7);
            return (byte)(id | p0 | p1);
        }

        // LIN checksum: V1=classic (data only), V2=enhanced (PID + data)
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

        // Packet checksum = two's complement of sum of bytes [0..length-1]
        // Matches Form1.cs Check_Sum: (~Sum & 0xFF) + 1
        // Makes total sum of all 16 bytes == 0 mod 256 — dongle validates this.
        public static byte PacketChecksum(byte[] frame, int length)
        {
            int sum = 0;
            for (int i = 0; i < length; i++) sum += frame[i];
            return (byte)((~sum & 0xFF) + 1);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 16-byte device protocol builder
    // ─────────────────────────────────────────────────────────────────────────
    static class Frame
    {
        const byte CMD_MODE  = 0x11;
        const byte CMD_SEND  = 0x22;
        const byte CMD_READ  = 0x33;
        const byte CMD_SLAVE = 0x55;

        static byte[] Base() => new byte[16];

        static byte CsFlag(bool enhanced) => enhanced ? (byte)2 : (byte)1;

        // 0x11 — configure device mode / baud
        public static byte[] ModeCommand(int mode, int baudValue = 0, int volume = 0, int offlineTime = 0)
        {
            var f = Base();
            f[0] = CMD_MODE;
            f[1] = (byte)mode;
            f[2] = (byte)((baudValue >> 8) & 0xFF);
            f[3] = (byte)(baudValue & 0xFF);
            f[4] = (byte)volume;
            f[5] = (byte)((offlineTime >> 8) & 0xFF);
            f[6] = (byte)(offlineTime & 0xFF);
            f[15] = LIN.PacketChecksum(f, 15);
            return f;
        }

        // 0x22 — master send (HOST → slave)
        public static byte[] HostSend(byte id, byte[] data, int length, bool enhanced)
        {
            if (length > 8) throw new ArgumentException("LIN max data length is 8");
            var f = Base();
            f[0] = CMD_SEND;
            f[2] = id;
            f[4] = CsFlag(enhanced);
            f[5] = (byte)length;
            for (int i = 0; i < length; i++) f[6 + i] = data[i];
            f[14] = LIN.CalcChecksum(id, data, length, enhanced);
            f[15] = LIN.PacketChecksum(f, 15);
            return f;
        }

        // 0x33 — master header, request slave response
        public static byte[] ReadSlave(byte id, int length, bool enhanced)
        {
            var f = Base();
            f[0] = CMD_READ;
            f[1] = 1;
            f[2] = id;
            f[3] = 1;
            f[4] = CsFlag(enhanced);
            f[5] = (byte)length;
            f[15] = LIN.PacketChecksum(f, 15);
            return f;
        }

        // 0x55 — update slave response data
        public static byte[] SlaveUpdate(byte id, int channel, byte[] data, int length, bool enhanced)
        {
            if (length > 8) throw new ArgumentException("LIN max data length is 8");
            var f = Base();
            f[0] = CMD_SLAVE;
            f[1] = (byte)channel;
            f[2] = id;
            f[4] = CsFlag(enhanced);
            f[5] = (byte)length;
            for (int i = 0; i < length; i++) f[6 + i] = data[i];
            f[14] = LIN.CalcChecksum(id, data, length, enhanced);
            f[15] = LIN.PacketChecksum(f, 15);
            return f;
        }

        public static string Hex(byte[] frame) =>
            string.Join(" ", frame.Select(b => b.ToString("X2")));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Received frame decoder  (16-byte responses mirror the TX structure)
    // ─────────────────────────────────────────────────────────────────────────
    static class FrameDecoder
    {
        public static string Decode(byte[] f)
        {
            if (f.Length < 16) return "(short frame)";
            string cmd  = f[0] switch { 0x22 => "SEND", 0x33 => "READ", 0x55 => "SLAVE",
                                         0x11 => "MODE", _ => $"CMD={f[0]:X2}" };
            byte   id   = f[2];
            int    len  = f[5];
            string cs   = f[4] == 2 ? "V2" : "V1";
            string data = string.Join(" ", f.Skip(6).Take(Math.Min(len, 8)).Select(b => b.ToString("X2")));
            string chk  = f[14].ToString("X2");
            string pid  = LIN.CalcParity(id).ToString("X2");
            return $"{cmd,-5} | ID={id:X2}(PID={pid}) | Len={len} | CS={cs} | Data=[{data}] | LINCheck={chk}";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Serial link — mirrors LINTest GUI serial setup exactly
    // ─────────────────────────────────────────────────────────────────────────
    class LINPort : IDisposable
    {
        readonly SerialPort _sp;
        // Ring buffer — GUI uses 1MB; we use 64KB which is plenty for CLI use
        readonly byte[] _rxBuf     = new byte[65536];
        int             _rxCount;    // total bytes appended (like RX_count)
        int             _rxSave;     // last aligned boundary  (like RX_Save_count)

        public string PortName => _sp.PortName;

        public LINPort(string port, int baudRate = 460800)
        {
            // GUI serial port configuration — match exactly:
            //   BaudRate=460800, DataBits=8, Parity=None, StopBits=One
            //   Encoding=Default, DtrEnable=true (RtsEnable NOT set — important for CH340)
            //   ReadTimeout=500, ReceivedBytesThreshold=16
            _sp = new SerialPort(port, baudRate, Parity.None, 8, StopBits.One)
            {
                Encoding                = System.Text.Encoding.Default,
                DtrEnable               = true,
                // RtsEnable intentionally omitted — GUI never sets it
                ReadTimeout             = 500,
                ReceivedBytesThreshold  = 16,
                // WriteTimeout not set — GUI default
            };

            // Probe open → sleep 50ms → dispose  (toggles DTR to wake CH340, same as GUI)
            // In .NET 8, Close() == Dispose(), so we use a separate instance for the probe.
            try
            {
                using var probe = new SerialPort(port, baudRate, Parity.None, 8, StopBits.One);
                probe.DtrEnable = true;
                probe.Open();
                Thread.Sleep(50);
            }
            catch { /* probe failure is non-fatal */ }

            // Actual open
            _sp.Open();
            _sp.DiscardInBuffer(); // GUI calls this immediately after open
        }

        public void Send(byte[] frame)
        {
            if (frame.Length != 16) throw new InvalidOperationException("Frame must be 16 bytes");
            _sp.Write(frame, 0, 16);
            _sp.BaseStream.Flush();
        }

        // Read one validated 16-byte response frame (blocks up to timeoutMs).
        // Mirrors Timing_Receive_USB_Task: appends all available bytes to ring buffer,
        // extracts aligned 16-byte chunks, validates checksum and command byte.
        // Dongle response frames have [0] = 0x33 | 0x44 | 0x55 | 0xDD.
        public bool TryReadFrame(out byte[] frame, int timeoutMs = 1000)
        {
            frame = Array.Empty<byte>();
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                // Drain whatever is in the hardware buffer (like GUI's BytesToRead + Read loop)
                int avail = _sp.BytesToRead;
                if (avail > 0)
                {
                    int space = _rxBuf.Length - _rxCount;
                    if (avail > space) avail = space;
                    if (avail > 0)
                    {
                        int n = _sp.Read(_rxBuf, _rxCount, avail);
                        _rxCount += n;
                    }
                }

                // Process every complete 16-byte chunk past _rxSave (same as GUI loop)
                int chunks = (_rxCount - _rxSave) / 16;
                for (int i = 0; i < chunks; i++)
                {
                    byte[] candidate = new byte[16];
                    Buffer.BlockCopy(_rxBuf, _rxSave + i * 16, candidate, 0, 16);

                    // GUI validation: packet checksum + valid dongle command byte
                    byte xorCs = 0;
                    for (int b = 0; b < 15; b++) xorCs ^= candidate[b];
                    bool csOk  = candidate[15] == xorCs;
                    bool cmdOk = candidate[0] == 0x33 || candidate[0] == 0x44
                              || candidate[0] == 0x55 || candidate[0] == 0xDD;

                    if (csOk && cmdOk)
                    {
                        _rxSave = _rxCount / 16 * 16; // advance save pointer
                        // Reset ring buffer when near full (matches GUI's 1048560 check)
                        if (_rxCount >= 65520 && _rxCount == _rxSave)
                        {
                            _rxCount = 0; _rxSave = 0;
                            Array.Clear(_rxBuf, 0, _rxBuf.Length);
                        }
                        frame = candidate;
                        return true;
                    }
                }
                _rxSave = _rxCount / 16 * 16;

                Thread.Sleep(1); // 1ms — mirrors USB_Receive_Task's Thread.Sleep(1)
            }
            return false;
        }

        public void Dispose() => _sp.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helper: parse hex byte array from "AA BB CC" or "AABBCC" strings
    // ─────────────────────────────────────────────────────────────────────────
    static class HexParse
    {
        public static byte[] Bytes(string s)
        {
            s = s.Replace(" ", "").Replace("0x", "").Replace("0X", "");
            if (s.Length % 2 != 0) throw new FormatException($"Odd hex string: {s}");
            var result = new byte[s.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return result;
        }

        public static byte Byte(string s) => Convert.ToByte(s.TrimPrefix("0x").TrimPrefix("0X"), 16);
    }

    static class StringExt
    {
        public static string TrimPrefix(this string s, string prefix) =>
            s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? s[prefix.Length..] : s;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Argument bag (simple key/value + positional)
    // ─────────────────────────────────────────────────────────────────────────
    class Args
    {
        readonly Dictionary<string, string> _opts = new(StringComparer.OrdinalIgnoreCase);
        public string Command { get; }

        public Args(string[] argv)
        {
            int i = 0;
            // First non-flag is the command
            while (i < argv.Length && argv[i].StartsWith("--")) i++;
            Command = i < argv.Length ? argv[i++].ToLower() : "";

            for (int j = 0; j < argv.Length; j++)
            {
                if (argv[j].StartsWith("--") && j + 1 < argv.Length && !argv[j + 1].StartsWith("--"))
                    _opts[argv[j][2..]] = argv[++j];
                else if (argv[j].StartsWith("--"))
                    _opts[argv[j][2..]] = "true";
            }
        }

        public string Get(string key, string def = "") =>
            _opts.TryGetValue(key, out var v) ? v : def;

        public int GetInt(string key, int def = 0) =>
            _opts.TryGetValue(key, out var v) && int.TryParse(v, out int n) ? n : def;

        public bool Has(string key) => _opts.ContainsKey(key);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Commands
    // ─────────────────────────────────────────────────────────────────────────
    static class Commands
    {
        public static void ListPorts()
        {
            var ports = SerialPort.GetPortNames();
            if (ports.Length == 0) { Console.WriteLine("No serial ports found."); return; }
            Console.WriteLine("Available serial ports:");
            foreach (var p in ports) Console.WriteLine($"  {p}");
        }

        public static void Send(LINPort port, Args args)
        {
            byte   id       = HexParse.Byte(args.Get("id", "0x00"));
            bool   enhanced = args.Get("cs", "v2").Equals("v2", StringComparison.OrdinalIgnoreCase);
            string dataStr  = args.Get("data", "00 00 00 00 00 00 00 00");
            byte[] data     = HexParse.Bytes(dataStr);
            int    len      = args.GetInt("len", data.Length);
            if (len > 8) len = 8;
            if (data.Length < len) Array.Resize(ref data, len);

            var frame = Frame.HostSend(id, data, len, enhanced);
            Console.WriteLine($"TX > {Frame.Hex(frame)}");
            Console.WriteLine($"     {FrameDecoder.Decode(frame)}");
            port.Send(frame);

            if (port.TryReadFrame(out var rx, 1500))
                Console.WriteLine($"RX < {Frame.Hex(rx)}\n     {FrameDecoder.Decode(rx)}");
            else
                Console.WriteLine("RX < (timeout)");
        }

        public static void Read(LINPort port, Args args)
        {
            byte id       = HexParse.Byte(args.Get("id", "0x00"));
            bool enhanced = args.Get("cs", "v2").Equals("v2", StringComparison.OrdinalIgnoreCase);
            int  len      = args.GetInt("len", 8);

            var frame = Frame.ReadSlave(id, len, enhanced);
            Console.WriteLine($"TX > {Frame.Hex(frame)}");
            Console.WriteLine($"     {FrameDecoder.Decode(frame)}");
            port.Send(frame);

            if (port.TryReadFrame(out var rx, 1500))
                Console.WriteLine($"RX < {Frame.Hex(rx)}\n     {FrameDecoder.Decode(rx)}");
            else
                Console.WriteLine("RX < (timeout — no slave response)");
        }

        public static void Monitor(LINPort port, Args args)
        {
            int timeout = args.GetInt("timeout", 0); // 0 = indefinite
            Console.WriteLine($"Monitoring on {port.PortName}  (Ctrl+C to stop)");
            Console.CancelKeyPress += (_, e) => e.Cancel = false;
            long  count   = 0;
            var   start   = DateTime.UtcNow;
            while (timeout == 0 || (DateTime.UtcNow - start).TotalSeconds < timeout)
            {
                if (port.TryReadFrame(out var rx, 500))
                {
                    count++;
                    string ts = DateTime.Now.ToString("HH:mm:ss.fff");
                    Console.WriteLine($"[{ts}] #{count,-6} {Frame.Hex(rx)}");
                    Console.WriteLine($"            {FrameDecoder.Decode(rx)}");
                }
            }
        }

        public static void SetMode(LINPort port, Args args)
        {
            int mode = args.GetInt("mode", 0);
            int baud = args.GetInt("baud", 0);
            var frame = Frame.ModeCommand(mode, baud);
            Console.WriteLine($"TX > {Frame.Hex(frame)}");
            port.Send(frame);
            Console.WriteLine("Mode command sent.");
        }

        // ── Brute force ──────────────────────────────────────────────────────
        // Iterates through LIN IDs [start..end] sending frames or reading responses.
        // In "send" mode: sends a host frame with specified data for each ID.
        // In "read" mode: sends a read-request header for each ID and captures any response.
        //
        // Options:
        //   --type   send|read          (default: read)
        //   --start  <hex>              First ID to probe (default: 0x00)
        //   --end    <hex>              Last  ID to probe (default: 0x3F — max LIN ID)
        //   --data   <hex bytes>        Data payload for send mode (default: FF * len)
        //   --len    N                  Data length 1-8 (default: 8)
        //   --cs     v1|v2             Checksum type (default: v2)
        //   --delay  N                  Milliseconds between frames (default: 50)
        //   --id-filter <hex list>      Only probe specific IDs e.g. "0x3C 0x3D"
        //   --rx-only                   Only print IDs that got a response
        //   --repeat N                  Repeat each ID N times (default: 1)
        //   --payload-seq               Increment payload byte[0] each iteration
        public static void Brute(LINPort port, Args args)
        {
            string btype    = args.Get("type", "read").ToLower();
            byte   startId  = HexParse.Byte(args.Get("start", "0x00"));
            byte   endId    = HexParse.Byte(args.Get("end",   "0x3F"));
            int    len      = args.GetInt("len", 8);
            bool   enhanced = args.Get("cs", "v2").Equals("v2", StringComparison.OrdinalIgnoreCase);
            int    delay    = args.GetInt("delay", 50);
            bool   rxOnly   = args.Has("rx-only");
            int    repeat   = Math.Max(1, args.GetInt("repeat", 1));
            bool   paySeq   = args.Has("payload-seq");

            // Base data payload
            byte[] baseData = new byte[8];
            if (args.Has("data"))
            {
                byte[] parsed = HexParse.Bytes(args.Get("data"));
                Array.Copy(parsed, baseData, Math.Min(parsed.Length, 8));
            }
            else
            {
                for (int i = 0; i < 8; i++) baseData[i] = 0xFF; // default fill
            }
            if (len > 8) len = 8;

            // Optional ID filter
            HashSet<byte>? filter = null;
            if (args.Has("id-filter"))
            {
                filter = new HashSet<byte>(
                    args.Get("id-filter").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(HexParse.Byte));
            }

            Console.WriteLine($"╔══ Brute Force: type={btype.ToUpper()} IDs=[{startId:X2}..{endId:X2}] " +
                              $"len={len} cs={(enhanced?"V2":"V1")} delay={delay}ms repeat={repeat} ══╗");
            if (rxOnly) Console.WriteLine("║  (only showing IDs with a response)");
            Console.WriteLine();

            int probed = 0, responded = 0;
            var payloadData = (byte[])baseData.Clone();

            for (byte id = startId; id <= endId; id++)
            {
                if (filter != null && !filter.Contains(id)) continue;

                for (int r = 0; r < repeat; r++)
                {
                    if (paySeq) payloadData[0] = (byte)(probed & 0xFF);

                    byte[] txFrame;
                    if (btype == "send")
                        txFrame = Frame.HostSend(id, payloadData, len, enhanced);
                    else
                        txFrame = Frame.ReadSlave(id, len, enhanced);

                    string pid = LIN.CalcParity(id).ToString("X2");
                    string payload = string.Join(" ", payloadData.Take(len).Select(b => b.ToString("X2")));

                    if (!rxOnly)
                    {
                        Console.Write($"  ID={id:X2} PID={pid} ");
                        if (btype == "send") Console.Write($"data=[{payload}] ");
                        Console.Write("→ ");
                    }

                    port.Send(txFrame);
                    probed++;

                    bool gotReply = port.TryReadFrame(out var rx, delay > 0 ? delay : 50);
                    if (gotReply) responded++;

                    if (gotReply)
                    {
                        if (rxOnly) Console.Write($"  ID={id:X2} PID={pid} → ");
                        string rxData = string.Join(" ", rx.Skip(6).Take(Math.Min((int)rx[5], 8)).Select(b => b.ToString("X2")));
                        Console.WriteLine($"RESPONSE  [{Frame.Hex(rx)}]  data=[{rxData}]  cs={rx[14]:X2}");
                    }
                    else if (!rxOnly)
                    {
                        Console.WriteLine("no response");
                    }

                    if (delay > 0) Thread.Sleep(delay);
                }

                if (id == 0xFF) break; // avoid overflow on byte
            }

            Console.WriteLine();
            Console.WriteLine($"╚══ Done: {probed} probed, {responded} responded ══╝");
        }

        // ── Payload sweep (one ID, iterate all byte values for one data position) ──
        // lintestcli --port COM3 sweep --id 0x22 --pos 0 --len 4 --delay 20
        public static void Sweep(LINPort port, Args args)
        {
            byte id       = HexParse.Byte(args.Get("id", "0x00"));
            bool enhanced = args.Get("cs", "v2").Equals("v2", StringComparison.OrdinalIgnoreCase);
            int  len      = args.GetInt("len", 8);
            int  pos      = args.GetInt("pos", 0);  // byte position to sweep (0-7)
            int  delay    = args.GetInt("delay", 20);
            int  lo       = args.GetInt("lo", 0x00);
            int  hi       = args.GetInt("hi", 0xFF);
            bool rxOnly   = args.Has("rx-only");

            byte[] data = new byte[8];
            if (args.Has("data")) { byte[] d = HexParse.Bytes(args.Get("data")); Array.Copy(d, data, Math.Min(d.Length, 8)); }
            if (pos < 0 || pos >= len) { Console.Error.WriteLine($"--pos must be 0..{len - 1}"); return; }

            Console.WriteLine($"╔══ Payload Sweep: ID={id:X2} pos={pos} range=[{lo:X2}..{hi:X2}] len={len} ══╗");

            int probed = 0, responded = 0;
            for (int v = lo; v <= hi; v++)
            {
                data[pos] = (byte)v;
                var frame = Frame.HostSend(id, data, len, enhanced);
                string payload = string.Join(" ", data.Take(len).Select(b => b.ToString("X2")));

                if (!rxOnly) Console.Write($"  [{payload}] → ");

                port.Send(frame);
                probed++;

                bool gotReply = port.TryReadFrame(out var rx, Math.Max(delay, 30));
                if (gotReply) responded++;

                if (gotReply)
                {
                    if (rxOnly) Console.Write($"  [{payload}] → ");
                    string rxData = string.Join(" ", rx.Skip(6).Take(Math.Min((int)rx[5], 8)).Select(b => b.ToString("X2")));
                    Console.WriteLine($"RESPONSE  data=[{rxData}]");
                }
                else if (!rxOnly)
                {
                    Console.WriteLine("no response");
                }

                if (delay > 0) Thread.Sleep(delay);
            }

            Console.WriteLine($"╚══ Done: {probed} sent, {responded} responded ══╝");
        }

        // ── Interactive REPL ─────────────────────────────────────────────────
        public static void Interactive(LINPort? port, string defaultPort)
        {
            Console.WriteLine("LINTest CLI — interactive mode");
            Console.WriteLine("Commands: send | read | brute | sweep | monitor | mode | ports | help | quit");
            Console.WriteLine($"Port: {(port != null ? port.PortName : "(none — use 'open COM3')")}");
            Console.WriteLine();

            while (true)
            {
                Console.Write("lin> ");
                string? line = Console.ReadLine();
                if (line == null || line.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string cmd = tokens[0].ToLower();

                // Rebuild argv so we can reuse the Args parser
                // e.g.  "send --id 0x22 --data FF FF"  becomes ["send","--id","0x22","--data","FF FF"]
                // We need to preserve quoted groups — simple approach: split on "--" boundaries
                var argv = line.Split(' ');
                var iargs = new Args(argv);

                try
                {
                    switch (cmd)
                    {
                        case "ports":
                            ListPorts();
                            break;
                        case "open":
                            if (tokens.Length < 2) { Console.WriteLine("Usage: open <COMx>"); break; }
                            port?.Dispose();
                            port = new LINPort(tokens[1]);
                            Console.WriteLine($"Opened {port.PortName}");
                            break;
                        case "send":
                            if (port == null) { Console.WriteLine("No port open. Use: open COM3"); break; }
                            Send(port, iargs);
                            break;
                        case "read":
                            if (port == null) { Console.WriteLine("No port open."); break; }
                            Read(port, iargs);
                            break;
                        case "brute":
                            if (port == null) { Console.WriteLine("No port open."); break; }
                            Brute(port, iargs);
                            break;
                        case "sweep":
                            if (port == null) { Console.WriteLine("No port open."); break; }
                            Sweep(port, iargs);
                            break;
                        case "monitor":
                            if (port == null) { Console.WriteLine("No port open."); break; }
                            Monitor(port, iargs);
                            break;
                        case "mode":
                            if (port == null) { Console.WriteLine("No port open."); break; }
                            SetMode(port, iargs);
                            break;
                        case "pid":
                            if (tokens.Length < 2) { Console.WriteLine("Usage: pid <hex-id>"); break; }
                            byte pid = HexParse.Byte(tokens[1]);
                            Console.WriteLine($"ID={pid:X2}  PID={LIN.CalcParity(pid):X2}  (binary: {Convert.ToString(LIN.CalcParity(pid), 2):B8})");
                            break;
                        case "checksum":
                            // checksum --id 0x22 --data "01 02 03 04" --len 4 --cs v2
                            {
                                byte csId = HexParse.Byte(iargs.Get("id", "0x00"));
                                bool enh  = iargs.Get("cs", "v2").Equals("v2", StringComparison.OrdinalIgnoreCase);
                                byte[] d  = HexParse.Bytes(iargs.Get("data", "00"));
                                int csLen = iargs.GetInt("len", d.Length);
                                byte result = LIN.CalcChecksum(csId, d, csLen, enh);
                                Console.WriteLine($"LIN checksum ({(enh ? "V2 enhanced" : "V1 classic")}): {result:X2}");
                            }
                            break;
                        case "frame":
                            // Decode a raw hex frame: frame "22 00 3C 00 02 04 01 02 03 04 00 00 00 00 xx yy"
                            {
                                string hexStr = string.Join("", tokens.Skip(1));
                                byte[] raw = HexParse.Bytes(hexStr);
                                if (raw.Length < 16) Array.Resize(ref raw, 16);
                                Console.WriteLine(FrameDecoder.Decode(raw));
                            }
                            break;
                        case "help":
                            Program.PrintHelp();
                            break;
                        case "quit":
                        case "exit":
                            return;
                        default:
                            Console.WriteLine($"Unknown command: {cmd}. Type 'help'.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }

            port?.Dispose();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Entry point
    // ─────────────────────────────────────────────────────────────────────────
    static class Program
    {
        static void Main(string[] argv)
        {
            if (argv.Length == 0 || argv.Contains("--help") || argv.Contains("-h"))
            {
                PrintHelp();
                return;
            }

            var args = new Args(argv);

            // ── ports command doesn't need a port ──
            if (args.Command == "ports")
            {
                Commands.ListPorts();
                return;
            }

            // ── interactive with no port: still useful for pid/checksum/frame ──
            if (args.Command == "interactive" && !args.Has("port"))
            {
                Commands.Interactive(null, "");
                return;
            }

            string portName = args.Get("port", "");
            if (string.IsNullOrEmpty(portName))
            {
                Console.Error.WriteLine("Error: --port <COMx> is required (or use 'ports' to list).");
                Environment.Exit(1);
            }

            int serialBaud = args.GetInt("serial-baud", 460800); // USB serial speed to adapter (CH340 fixed at 460800)
            int linBaud    = args.GetInt("lin-baud", 0);         // LIN bus baud rate sent via mode command (e.g. 19200)

            LINPort? port = null;
            try
            {
                port = new LINPort(portName, serialBaud);
                Console.WriteLine($"Opened {portName} at {serialBaud} baud (USB serial)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to open {portName}: {ex.Message}");
                Environment.Exit(1);
            }

            // Arm the dongle for host-send mode, mirroring imageButton5_Click in LINTest-M:
            //   0x11 mode=0 (Host connect) → sleep 100ms → 0x11 mode=1 (Single-frame send) → sleep 100ms
            // Without this sequence the dongle silently ignores all 0x22 host-send frames.
            // Only needed for send/read/brute/sweep/interactive — not for monitor or manual mode commands.
            bool needsModeArm = args.Command is "send" or "read" or "brute" or "sweep" or "interactive";
            if (needsModeArm)
            {
                if (linBaud > 0 && (linBaud < 4800 || linBaud > 20000))
                    Console.Error.WriteLine($"Warning: --lin-baud {linBaud} is outside supported range 4800-20000");

                int effectiveBaud = linBaud > 0 ? linBaud : 19200;
                var m0 = Frame.ModeCommand(0, effectiveBaud, 28, 100);
                port.Send(m0);
                Console.WriteLine($"Arm: mode=0  [{Frame.Hex(m0)}]");
                Thread.Sleep(100);

                var m1 = Frame.ModeCommand(1, effectiveBaud, 28, 100);
                port.Send(m1);
                Console.WriteLine($"Arm: mode=1  [{Frame.Hex(m1)}]");
                Thread.Sleep(100);

                Console.WriteLine($"Dongle armed — LIN bus {effectiveBaud} baud, single-frame send mode");
            }
            else if (linBaud > 0 && args.Command == "monitor")
            {
                // For monitor: just set the baud without activating host-send
                var m0 = Frame.ModeCommand(0, linBaud, 28, 100);
                port.Send(m0);
                Thread.Sleep(100);
                Console.WriteLine($"LIN bus configured: {linBaud} baud");
            }

            try
            {
                switch (args.Command)
                {
                    case "send":        Commands.Send(port, args);        break;
                    case "read":        Commands.Read(port, args);        break;
                    case "monitor":     Commands.Monitor(port, args);     break;
                    case "mode":        Commands.SetMode(port, args);     break;
                    case "brute":       Commands.Brute(port, args);       break;
                    case "sweep":       Commands.Sweep(port, args);       break;
                    case "interactive": Commands.Interactive(port, portName); break;
                    default:
                        Console.Error.WriteLine($"Unknown command: '{args.Command}'. Run with --help.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
            }
            finally
            {
                port.Dispose();
            }
        }

        internal static void PrintHelp() =>
            Console.WriteLine("""
lintestcli — LIN bus CLI for LINTest-MI hardware
Protocol: 16-byte frames over USB serial (CH340 or compatible)

USAGE
  lintestcli [--port <COMx>] [--serial-baud N] <command> [options]

GLOBAL OPTIONS
  --port <COMx>          Serial port to use (e.g. COM3)
  --serial-baud N        USB serial baud to dongle (default: 460800 — matches LINTest-MI)
  --lin-baud N           LIN bus baud rate to configure on the dongle (e.g. 19200).
                         Sends a 0x11 init frame before the command. Range: 4800-20000.

COMMANDS
  ports
      List available serial ports.

  send [--id <hex>] [--data "<hex bytes>"] [--len N] [--cs v1|v2]
      Send a LIN master frame (host → slave).
      --id   LIN frame ID 0x00-0x3F (default: 0x00)
      --data Space-separated hex bytes (default: FF×8)
      --len  Number of data bytes 1-8 (default: inferred from --data)
      --cs   Checksum type: v1=classic, v2=enhanced (default: v2)

  read [--id <hex>] [--len N] [--cs v1|v2]
      Send a LIN header requesting a slave response.

  monitor [--timeout N]
      Dump all incoming 16-byte frames to stdout.
      --timeout N  Stop after N seconds (default: run forever)

  mode --mode N [--baud N]
      Send a 0x11 mode-command packet to the adapter.
      --baud  LIN bus baud rate (e.g. 19200). NOT the USB serial speed.
      Modes: 0=Host 1=Slave 2=Monitor 3=RTOS 4=Playback 5=Boot 6=BaudDetect

  brute [--type send|read] [--start <hex>] [--end <hex>]
        [--data "<hex bytes>"] [--len N] [--cs v1|v2]
        [--delay N] [--id-filter "<hex list>"] [--rx-only]
        [--repeat N] [--payload-seq]
      Iterate IDs 0x00-0x3F sending frames or polling for responses.
      --type        send (master write) or read (poll slave) (default: read)
      --start       First ID to probe (default: 0x00)
      --end         Last  ID to probe (default: 0x3F)
      --data        Data payload for send mode
      --len         Data bytes (default: 8)
      --cs          v1 or v2 (default: v2)
      --delay       ms between frames (default: 50)
      --id-filter   Space-quoted list of IDs to probe, e.g. "0x3C 0x3D"
      --rx-only     Only print IDs that got a slave response
      --repeat      Repeat each ID N times (default: 1)
      --payload-seq Increment data[0] each iteration

  sweep [--id <hex>] [--pos N] [--lo <hex>] [--hi <hex>]
        [--data "<hex bytes>"] [--len N] [--cs v1|v2] [--delay N] [--rx-only]
      Send one ID repeatedly, sweeping a single data byte through [lo..hi].
      --id    LIN frame ID to target
      --pos   Data byte index to sweep (0-7) (default: 0)
      --lo    Start value (default: 0x00)
      --hi    End value   (default: 0xFF)

  interactive [--port <COMx>]
      Drop into a REPL. All commands above work interactively, plus:
        open <COMx>                   Open / switch port
        pid <hex-id>                  Show PID (ID + parity bits)
        checksum --id X --data "..." --len N --cs v1|v2
        frame <raw-hex>               Decode a raw 16-byte frame

EXAMPLES
  # List ports
  lintestcli ports

  # Send ID=0x22, 4 bytes, enhanced checksum
  lintestcli --port COM3 send --id 0x22 --data "01 02 03 04" --len 4 --cs v2

  # Poll slave on ID=0x3C
  lintestcli --port COM3 read --id 0x3C --len 8

  # Brute-force read all IDs, print only those that answer
  lintestcli --port COM3 brute --type read --rx-only --delay 100

  # Brute-force send IDs 0x00-0x3F with FF data, classic checksum
  lintestcli --port COM3 brute --type send --data "FF FF FF FF FF FF FF FF" --cs v1

  # Sweep data byte 0 on ID=0x22 looking for responses
  lintestcli --port COM3 sweep --id 0x22 --pos 0 --lo 0x00 --hi 0xFF --rx-only

  # Monitor bus traffic for 30 seconds
  lintestcli --port COM3 monitor --timeout 30

  # Interactive session
  lintestcli --port COM3 interactive
""");
    }
}
