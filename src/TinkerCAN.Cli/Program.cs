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
using TinkerCAN.Lin;

namespace LINTestCLI
{
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

            var frame = LINProtocol.HostSend(id, data, len, enhanced);
            Console.WriteLine($"TX > {LINProtocol.FrameHex(frame)}");
            Console.WriteLine($"     {LINProtocol.DecodeFrame(frame)}");
            port.Send(frame);

            if (port.TryReadFrame(out var rx, 1500))
                Console.WriteLine($"RX < {LINProtocol.FrameHex(rx)}\n     {LINProtocol.DecodeFrame(rx)}");
            else
                Console.WriteLine("RX < (timeout)");
        }

        public static void Read(LINPort port, Args args)
        {
            byte id       = HexParse.Byte(args.Get("id", "0x00"));
            bool enhanced = args.Get("cs", "v2").Equals("v2", StringComparison.OrdinalIgnoreCase);
            int  len      = args.GetInt("len", 8);

            var frame = LINProtocol.ReadSlave(id, len, enhanced);
            Console.WriteLine($"TX > {LINProtocol.FrameHex(frame)}");
            Console.WriteLine($"     {LINProtocol.DecodeFrame(frame)}");
            port.Send(frame);

            if (port.TryReadFrame(out var rx, 1500))
                Console.WriteLine($"RX < {LINProtocol.FrameHex(rx)}\n     {LINProtocol.DecodeFrame(rx)}");
            else
                Console.WriteLine("RX < (timeout — no slave response)");
        }

        public static void Monitor(LINPort port, Args args)
        {
            int timeout = args.GetInt("timeout", 0);
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
                    Console.WriteLine($"[{ts}] #{count,-6} {LINProtocol.FrameHex(rx)}");
                    Console.WriteLine($"            {LINProtocol.DecodeFrame(rx)}");
                }
            }
        }

        public static void SetMode(LINPort port, Args args)
        {
            int mode = args.GetInt("mode", 0);
            int baud = args.GetInt("baud", 0);
            var frame = LINProtocol.ModeCommand(mode, baud);
            Console.WriteLine($"TX > {LINProtocol.FrameHex(frame)}");
            port.Send(frame);
            Console.WriteLine("Mode command sent.");
        }

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

            byte[] baseData = new byte[8];
            if (args.Has("data"))
            {
                byte[] parsed = HexParse.Bytes(args.Get("data"));
                Array.Copy(parsed, baseData, Math.Min(parsed.Length, 8));
            }
            else
            {
                for (int i = 0; i < 8; i++) baseData[i] = 0xFF;
            }
            if (len > 8) len = 8;

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

                    byte[] txFrame = btype == "send"
                        ? LINProtocol.HostSend(id, payloadData, len, enhanced)
                        : LINProtocol.ReadSlave(id, len, enhanced);

                    string pid = LINProtocol.CalcParity(id).ToString("X2");
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
                        Console.WriteLine($"RESPONSE  [{LINProtocol.FrameHex(rx)}]  data=[{rxData}]  cs={rx[14]:X2}");
                    }
                    else if (!rxOnly)
                    {
                        Console.WriteLine("no response");
                    }

                    if (delay > 0) Thread.Sleep(delay);
                }

                if (id == 0xFF) break;
            }

            Console.WriteLine();
            Console.WriteLine($"╚══ Done: {probed} probed, {responded} responded ══╝");
        }

        public static void Sweep(LINPort port, Args args)
        {
            byte id       = HexParse.Byte(args.Get("id", "0x00"));
            bool enhanced = args.Get("cs", "v2").Equals("v2", StringComparison.OrdinalIgnoreCase);
            int  len      = args.GetInt("len", 8);
            int  pos      = args.GetInt("pos", 0);
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
                var frame = LINProtocol.HostSend(id, data, len, enhanced);
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
                            Console.WriteLine($"ID={pid:X2}  PID={LINProtocol.CalcParity(pid):X2}  (binary: {Convert.ToString(LINProtocol.CalcParity(pid), 2):B8})");
                            break;
                        case "checksum":
                            {
                                byte csId = HexParse.Byte(iargs.Get("id", "0x00"));
                                bool enh  = iargs.Get("cs", "v2").Equals("v2", StringComparison.OrdinalIgnoreCase);
                                byte[] d  = HexParse.Bytes(iargs.Get("data", "00"));
                                int csLen = iargs.GetInt("len", d.Length);
                                byte result = LINProtocol.CalcChecksum(csId, d, csLen, enh);
                                Console.WriteLine($"LIN checksum ({(enh ? "V2 enhanced" : "V1 classic")}): {result:X2}");
                            }
                            break;
                        case "frame":
                            {
                                string hexStr = string.Join("", tokens.Skip(1));
                                byte[] raw = HexParse.Bytes(hexStr);
                                if (raw.Length < 16) Array.Resize(ref raw, 16);
                                Console.WriteLine(LINProtocol.DecodeFrame(raw));
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

            if (args.Command == "ports")
            {
                Commands.ListPorts();
                return;
            }

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

            int serialBaud = args.GetInt("serial-baud", 460800);
            int linBaud    = args.GetInt("lin-baud", 0);

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

            bool needsModeArm = args.Command is "send" or "read" or "brute" or "sweep" or "interactive";
            if (needsModeArm)
            {
                if (linBaud > 0 && (linBaud < 4800 || linBaud > 20000))
                    Console.Error.WriteLine($"Warning: --lin-baud {linBaud} is outside supported range 4800-20000");

                int effectiveBaud = linBaud > 0 ? linBaud : 19200;
                var m0 = LINProtocol.ModeCommand(0, effectiveBaud, 28, 100);
                port!.Send(m0);
                Console.WriteLine($"Arm: mode=0  [{LINProtocol.FrameHex(m0)}]");
                Thread.Sleep(100);

                var m1 = LINProtocol.ModeCommand(1, effectiveBaud, 28, 100);
                port.Send(m1);
                Console.WriteLine($"Arm: mode=1  [{LINProtocol.FrameHex(m1)}]");
                Thread.Sleep(100);

                Console.WriteLine($"Dongle armed — LIN bus {effectiveBaud} baud, single-frame send mode");
            }
            else if (linBaud > 0 && args.Command == "monitor")
            {
                var m0 = LINProtocol.ModeCommand(0, linBaud, 28, 100);
                port!.Send(m0);
                Thread.Sleep(100);
                Console.WriteLine($"LIN bus configured: {linBaud} baud");
            }

            try
            {
                switch (args.Command)
                {
                    case "send":        Commands.Send(port!, args);        break;
                    case "read":        Commands.Read(port!, args);        break;
                    case "monitor":     Commands.Monitor(port!, args);     break;
                    case "mode":        Commands.SetMode(port!, args);     break;
                    case "brute":       Commands.Brute(port!, args);       break;
                    case "sweep":       Commands.Sweep(port!, args);       break;
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
                port?.Dispose();
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

  sweep [--id <hex>] [--pos N] [--lo <hex>] [--hi <hex>]
        [--data "<hex bytes>"] [--len N] [--cs v1|v2] [--delay N] [--rx-only]
      Send one ID repeatedly, sweeping a single data byte through [lo..hi].

  interactive [--port <COMx>]
      Drop into a REPL.

EXAMPLES
  lintestcli ports
  lintestcli --port COM3 send --id 0x22 --data "01 02 03 04" --len 4 --cs v2
  lintestcli --port COM3 read --id 0x3C --len 8
  lintestcli --port COM3 brute --type read --rx-only --delay 100
  lintestcli --port COM3 monitor --timeout 30
""");
    }
}
