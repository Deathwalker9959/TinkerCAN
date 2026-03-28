// lingui — LIN bus signal generator GUI
// Connects to LINTest-MI dongle (CH340, 460800 USB serial baud)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LINGui
{
    // ─────────────────────────────────────────────────────────────────────────
    // LIN protocol helpers
    // ─────────────────────────────────────────────────────────────────────────
    static class LIN
    {
        static byte Bit(byte a, byte b) => (byte)((a >> b) & 1);

        public static byte CalcParity(byte id)
        {
            byte p0 = (byte)((Bit(id, 0) ^ Bit(id, 1) ^ Bit(id, 2) ^ Bit(id, 4)) << 6);
            byte p1 = (byte)((~(Bit(id, 1) ^ Bit(id, 3) ^ Bit(id, 4) ^ Bit(id, 5)) & 1) << 7);
            return (byte)(id | p0 | p1);
        }

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

        // Packet checksum: two's complement of sum — matches Form1.cs Check_Sum
        static byte PktCheck(byte[] f, int len)
        {
            int sum = 0;
            for (int i = 0; i < len; i++) sum += f[i];
            return (byte)((~sum & 0xFF) + 1);
        }

        public static byte[] HostSend(byte id, byte[] data, int length, bool enhanced)
        {
            var f = new byte[16];
            f[0] = 0x22; f[2] = id;
            f[4] = enhanced ? (byte)2 : (byte)1;
            f[5] = (byte)length;
            for (int i = 0; i < length; i++) f[6 + i] = data[i];
            f[14] = CalcChecksum(id, data, length, enhanced);
            f[15] = PktCheck(f, 15);
            return f;
        }

        public static byte[] ModeCommand(int mode, int linBaud, int volume = 28, int offlineTime = 100)
        {
            var f = new byte[16];
            f[0] = 0x11; f[1] = (byte)mode;
            f[2] = (byte)((linBaud >> 8) & 0xFF);
            f[3] = (byte)(linBaud & 0xFF);
            f[4] = (byte)volume;
            f[5] = (byte)((offlineTime >> 8) & 0xFF);
            f[6] = (byte)(offlineTime & 0xFF);
            f[15] = PktCheck(f, 15);
            return f;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SLCAN protocol helpers (WeAct USB2CANFD / any SLCAN-compatible adapter)
    // ─────────────────────────────────────────────────────────────────────────
    static class SLCAN
    {
        static byte[] Cmd(string s) => Encoding.ASCII.GetBytes(s + "\r");

        public static byte[] Open()       => Cmd("O");
        public static byte[] Close()      => Cmd("C");
        public static byte[] GetVersion() => Cmd("V");
        public static byte[] GetError()   => Cmd("E");
        public static byte[] SetMode(bool silent) => Cmd(silent ? "M1" : "M0");
        public static byte[] SetAutoRetransmit(bool en) => Cmd(en ? "A1" : "A0");

        // ── Nominal bit-rate presets ──────────────────────────────────────────
        static readonly (string Label, string Code)[] _nomRates =
        {
            ("5k",    "SD"), ("10k",  "S0"), ("20k",  "S1"), ("33.3k", "SC"),
            ("50k",   "S2"), ("62.5k","SB"), ("75k",  "SA"), ("83.3k", "S9"),
            ("100k",  "S3"), ("125k", "S4"), ("250k", "S5"), ("500k",  "S6"),
            ("800k",  "S7"), ("1M",   "S8"),
        };
        static readonly (string Label, string Code)[] _fdRates =
        {
            ("1M","Y1"), ("2M","Y2"), ("3M","Y3"), ("4M","Y4"), ("5M","Y5"),
        };

        public static string[] NomRateLabels => _nomRates.Select(r => r.Label).ToArray();
        public static string[] FdRateLabels  => _fdRates.Select(r => r.Label).ToArray();
        public static int DefaultNomIdx => 9;  // 125k
        public static int DefaultFdIdx  => 1;  // 2M

        public static byte[] SetNomRate(int idx) => idx >= 0 && idx < _nomRates.Length ? Cmd(_nomRates[idx].Code) : Cmd("S4");
        public static byte[] SetFdRate(int idx)  => idx >= 0 && idx < _fdRates.Length  ? Cmd(_fdRates[idx].Code)  : Cmd("Y2");

        // ── CANFD DLC ↔ byte-length mapping ──────────────────────────────────
        static readonly int[] _fdDlcBytes = { 0,1,2,3,4,5,6,7,8,12,16,20,24,32,48,64 };
        public static int BytesToFdDlc(int byteLen)
        {
            for (int i = 0; i < _fdDlcBytes.Length; i++)
                if (_fdDlcBytes[i] >= byteLen) return i;
            return 15;
        }
        public static int FdDlcToBytes(int dlc) => (uint)dlc < (uint)_fdDlcBytes.Length ? _fdDlcBytes[dlc] : 64;

        // ── Frame builders ────────────────────────────────────────────────────
        static string H(byte[] d, int n) => string.Concat(d.Take(n).Select(b => b.ToString("X2")));

        public static byte[] SendStd    (int id, byte[] data, int dlc) => Cmd($"t{id & 0x7FF:X3}{dlc:X}{H(data, dlc)}");
        public static byte[] SendExt    (int id, byte[] data, int dlc) => Cmd($"T{id & 0x1FFFFFFF:X8}{dlc:X}{H(data, dlc)}");
        public static byte[] RemoteStd  (int id, int dlc)              => Cmd($"r{id & 0x7FF:X3}{dlc:X}");
        public static byte[] RemoteExt  (int id, int dlc)              => Cmd($"R{id & 0x1FFFFFFF:X8}{dlc:X}");
        public static byte[] SendFdStd  (int id, byte[] data, int byteLen, bool brs)
        { int d = BytesToFdDlc(byteLen); return Cmd($"{(brs?'b':'d')}{id & 0x7FF:X3}{d:X}{H(data, FdDlcToBytes(d))}"); }
        public static byte[] SendFdExt  (int id, byte[] data, int byteLen, bool brs)
        { int d = BytesToFdDlc(byteLen); return Cmd($"{(brs?'B':'D')}{id & 0x1FFFFFFF:X8}{d:X}{H(data, FdDlcToBytes(d))}"); }

        // ── Frame parser (for received strings, without trailing \r) ──────────
        public record RxFrame(string Type, int Id, bool Extended, int Dlc, int ByteLen, byte[] Data, bool IsRemote, bool IsFd, bool Brs);

        public static RxFrame? ParseFrame(string s)
        {
            if (s.Length < 2) return null;
            char c = s[0];
            bool ext = c == 'T' || c == 'R' || c == 'D' || c == 'B';
            bool fd  = c == 'd' || c == 'D' || c == 'b' || c == 'B';
            bool brs = c == 'b' || c == 'B';
            bool rem = c == 'r' || c == 'R';
            if (c != 't' && c != 'T' && c != 'r' && c != 'R' && c != 'd' && c != 'D' && c != 'b' && c != 'B') return null;

            int idLen = ext ? 8 : 3;
            if (s.Length < 1 + idLen + 1) return null;
            if (!int.TryParse(s.Substring(1, idLen), System.Globalization.NumberStyles.HexNumber, null, out int id)) return null;
            if (!int.TryParse(s.Substring(1 + idLen, 1), System.Globalization.NumberStyles.HexNumber, null, out int dlc)) return null;

            int byteLen = fd ? FdDlcToBytes(dlc) : Math.Min(dlc, 8);
            int ds = 1 + idLen + 1;
            var data = new byte[byteLen];
            for (int i = 0; i < byteLen && ds + i * 2 + 1 < s.Length; i++)
                byte.TryParse(s.Substring(ds + i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out data[i]);

            string type = fd ? (brs ? "FD+BRS" : "FD") : (rem ? "Remote" : "Data");
            return new RxFrame(type, id, ext, dlc, byteLen, data, rem, fd, brs);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Expression evaluator  (+−*/% & | ^ ~, parens, 0x hex, D0..D7 vars)
    // ─────────────────────────────────────────────────────────────────────────
    class Expr
    {
        readonly string _s; int _i;
        Expr(string s) { _s = s; _i = 0; }

        void Ws() { while (_i < _s.Length && _s[_i] == ' ') _i++; }
        char Pk() { Ws(); return _i < _s.Length ? _s[_i] : '\0'; }
        char Eat() { Ws(); return _i < _s.Length ? _s[_i++] : '\0'; }

        int Or()    { int v = Xor(); while (Pk() == '|') { Eat(); v |= Xor(); }  return v; }
        int Xor()   { int v = And(); while (Pk() == '^') { Eat(); v ^= And(); }  return v; }
        int And()   { int v = Add(); while (Pk() == '&') { Eat(); v &= Add(); }  return v; }
        int Add()
        {
            int v = Mul();
            while (Pk() == '+' || Pk() == '-') { char op = Eat(); int r = Mul(); v = op == '+' ? v + r : v - r; }
            return v;
        }
        int Mul()
        {
            int v = Unary();
            while (Pk() == '*' || Pk() == '/' || Pk() == '%')
            { char op = Eat(); int r = Unary(); v = op == '*' ? v * r : op == '/' ? v / r : v % r; }
            return v;
        }
        int Unary() { if (Pk() == '~') { Eat(); return ~Unary(); } if (Pk() == '-') { Eat(); return -Unary(); } return Atom(); }
        int Atom()
        {
            if (Pk() == '(') { Eat(); int v = Or(); if (Pk() == ')') Eat(); return v; }
            Ws(); int s = _i;
            if (_i + 1 < _s.Length && _s[_i] == '0' && (_s[_i + 1] == 'x' || _s[_i + 1] == 'X'))
            { _i += 2; int hs = _i; while (_i < _s.Length && IsHex(_s[_i])) _i++; return Convert.ToInt32(_s[hs.._i], 16); }
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            if (_i == s) throw new Exception($"Unexpected '{Pk()}' in: {_s}");
            return int.Parse(_s[s.._i]);
        }
        static bool IsHex(char c) => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        public static int Eval(string expr, byte[] data)
        {
            expr = Regex.Replace(expr.Trim(), @"\bD(\d+)\b", m =>
            {
                int idx = int.Parse(m.Groups[1].Value);
                return idx >= 0 && idx < data.Length ? data[idx].ToString() : "0";
            }, RegexOptions.IgnoreCase);
            return new Expr(expr).Or();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Runtime state for one multi-signal row
    // ─────────────────────────────────────────────────────────────────────────
    class SigRow
    {
        public byte   Id;
        public byte[] Data         = new byte[8]; // mutable working copy (modifier accumulates here)
        public int    Len;
        public bool   Enhanced;
        public int    IntervalMs;
        public string Modifier     = "";
        public int    GridRow;     // index back into DataGridView
        public long   NextMs;      // TickCount64 when to fire next
        public long   Count;
        // When the user edits the Data cell while running, store the reset value here.
        // Volatile reference: written by UI thread, read + cleared by timer thread.
        public volatile byte[]? PendingData;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Runtime state for one CAN multi-signal row
    // ─────────────────────────────────────────────────────────────────────────
    class CanSigRow
    {
        public int    TypeIdx;   // index into frame-type combo (0-7)
        public int    Id;
        public byte[] Data       = new byte[64];
        public int    DlcOrLen;
        public int    IntervalMs;
        public string Modifier   = "";
        public int    GridRow;
        public long   NextMs;
        public long   Count;
        public volatile byte[]? PendingData;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // JSON config classes
    // ─────────────────────────────────────────────────────────────────────────
    class SignalCfg
    {
        public string Id       { get; set; } = "22";
        public string Data     { get; set; } = "FF FF FF FF FF FF FF FF";
        public int    Len      { get; set; } = 8;
        public bool   Enhanced { get; set; } = true;
        public int    Ms       { get; set; } = 100;
        public string Modifier { get; set; } = "D0=D0+1";
    }

    class MultiSignalCfg
    {
        public bool   Enabled  { get; set; } = true;
        public string Id       { get; set; } = "00";
        public string Data     { get; set; } = "FF FF FF FF FF FF FF FF";
        public int    Len      { get; set; } = 8;
        public bool   Enhanced { get; set; } = true;
        public int    Ms       { get; set; } = 100;
        public string Modifier { get; set; } = "";
    }

    class CanSignalCfg
    {
        public int    FrameTypeIndex { get; set; } = 0;
        public string Id             { get; set; } = "123";
        public string Dlc            { get; set; } = "8";
        public string Data           { get; set; } = "DE AD BE EF 00 00 00 00";
        public int    Ms             { get; set; } = 100;
        public string Modifier       { get; set; } = "";
    }

    class CanMultiSignalCfg
    {
        public bool   Enabled  { get; set; } = true;
        public string Type     { get; set; } = "t";
        public string Id       { get; set; } = "000";
        public string Dlc      { get; set; } = "8";
        public string Data     { get; set; } = "FF FF FF FF FF FF FF FF";
        public int    Ms       { get; set; } = 100;
        public string Modifier { get; set; } = "";
    }

    class LinBruteResultCfg
    {
        public string Id       { get; set; } = "00";
        public string Pid      { get; set; } = "00";
        public string Payload  { get; set; } = "";
        public string Response { get; set; } = "-";
        public string RespData { get; set; } = "";
    }

    class LinBruteCfg
    {
        public string Start            { get; set; } = "00";
        public string End              { get; set; } = "3F";
        public string Step             { get; set; } = "11";
        public int    DelayMs          { get; set; } = 20;
        public int    RxTimeoutMs      { get; set; } = 30;
        public int    Dlc              { get; set; } = 8;
        public bool   Enhanced         { get; set; } = true;
        public bool   ConstantEnabled  { get; set; }
        public string ConstId          { get; set; } = "3C";
        public string ConstData        { get; set; } = "FF FF FF FF FF FF FF FF";
        public int    ConstMs          { get; set; } = 10;
        public bool   BroadcastEnabled { get; set; }
        public string BroadcastData    { get; set; } = "FF FF FF FF FF FF FF FF";
        public bool   ReplayLoop       { get; set; }
        public List<LinBruteResultCfg> Results { get; set; } = new();
    }

    class CanBruteResultCfg
    {
        public string Type     { get; set; } = "t";
        public string Id       { get; set; } = "000";
        public string Dlc      { get; set; } = "8";
        public string Payload  { get; set; } = "";
        public string Ack      { get; set; } = "-";
        public string RespData { get; set; } = "";
    }

    class CanBruteCfg
    {
        public string Start           { get; set; } = "000";
        public string End             { get; set; } = "7FF";
        public string Step            { get; set; } = "01";
        public int    DelayMs         { get; set; } = 5;
        public int    RxTimeoutMs     { get; set; } = 20;
        public int    TypeIndex       { get; set; }
        public int    Dlc             { get; set; } = 8;
        public string Data            { get; set; } = "00 00 00 00 00 00 00 00";
        public bool   ConstantEnabled { get; set; }
        public string ConstId         { get; set; } = "123";
        public string ConstData       { get; set; } = "00 00 00 00 00 00 00 00";
        public int    ConstMs         { get; set; } = 10;
        public bool   ReplayLoop      { get; set; }
        public List<CanBruteResultCfg> Results { get; set; } = new();
    }

    class CanConfigCfg
    {
        public int                    NomRateIndex     { get; set; } = SLCAN.DefaultNomIdx;
        public int                    FdRateIndex      { get; set; } = SLCAN.DefaultFdIdx;
        public bool                   Silent           { get; set; }
        public bool                   AutoRetransmit   { get; set; }
        public CanSignalCfg           Signal           { get; set; } = new();
        public List<CanMultiSignalCfg> MultiSignals    { get; set; } = new();
        public CanBruteCfg            Brute            { get; set; } = new();
    }

    class AppConfig
    {
        public int                  LinBaud      { get; set; } = 19200;
        public SignalCfg            Signal       { get; set; } = new();
        public List<MultiSignalCfg> MultiSignals { get; set; } = new();
        public LinBruteCfg          LinBrute     { get; set; } = new();
        public CanConfigCfg         Can          { get; set; } = new();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Main form
    // ─────────────────────────────────────────────────────────────────────────
    class MainForm : Form
    {
        // ── Connection bar ────────────────────────────────────────────────────
        ComboBox  _cmbPort   = new() { Width = 88, DropDownStyle = ComboBoxStyle.DropDownList };
        TextBox   _txtBaud   = new() { Width = 56, Text = "19200" };
        Button    _btnConn   = Btn("Connect",    76);
        Button    _btnDisc   = Btn("Disconnect", 82);
        Label     _lblStatus = new() { AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(6, 5, 0, 0) };

        // ── Signal tab controls ───────────────────────────────────────────────
        TextBox        _txtId    = new() { Width = 52, Text = "0x22", MaxLength = 6 };
        Label          _lblPid   = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 5, 0, 0) };
        NumericUpDown  _nudLen   = new() { Width = 44, Minimum = 1, Maximum = 8, Value = 8 };
        RadioButton    _rbV2     = new() { Text = "V2 enhanced", Checked = true, AutoSize = true };
        RadioButton    _rbV1     = new() { Text = "V1 classic",  AutoSize = true };
        TextBox[]      _txtD     = Enumerable.Range(0, 8).Select(_ => new TextBox { Width = 36, Text = "FF", MaxLength = 4 }).ToArray();
        TextBox        _txtMod   = new()
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f),
            Text = "// One modifier per line:  Dx = expression\r\n// Supports: + - * / % & | ^ ~  and 0x hex\r\n// Examples:\r\n//   D0=D0+1\r\n//   D1=D0 & 0x0F\r\n\r\nD0=D0+1\r\n",
        };
        NumericUpDown  _nudMs    = new() { Width = 65, Minimum = 1, Maximum = 60000, Value = 100 };
        Button         _btnOnce  = Btn("Send Once", 76);
        Button         _btnStart = Btn("▶ Start",   70);
        Button         _btnStop  = Btn("■ Stop",    60);
        Label          _lblCount = new() { Text = "Sent: 0", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(6, 5, 0, 0) };

        // ── Multi-signal tab controls ─────────────────────────────────────────
        DataGridView _grid       = new();
        Button       _btnMAdd    = Btn("+ Add",     60);
        Button       _btnMDel    = Btn("− Remove",  72);
        Button       _btnMStart  = Btn("▶ Start All", 84);
        Button       _btnMStop   = Btn("■ Stop All",  76);
        Label        _lblMStatus = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(6, 5, 0, 0) };

        // ── Brute-force tab controls ──────────────────────────────────────────
        TextBox       _txtBfStart  = new() { Width = 40, Text = "00",  MaxLength = 4 };
        TextBox       _txtBfEnd    = new() { Width = 40, Text = "3F",  MaxLength = 4 };
        TextBox       _txtBfStep   = new() { Width = 40, Text = "11",  MaxLength = 4 };
        NumericUpDown _nudBfDelay  = new() { Width = 55, Minimum = 0, Maximum = 5000, Value = 20 };
        NumericUpDown _nudBfDlc    = new() { Width = 44, Minimum = 1, Maximum = 8,    Value = 8 };
        RadioButton   _rbBfV2      = new() { Text = "V2 enhanced", Checked = true, AutoSize = true };
        RadioButton   _rbBfV1      = new() { Text = "V1 classic",  AutoSize = true };
        NumericUpDown _nudBfRxTimeout   = new() { Width = 55, Minimum = 5, Maximum = 2000, Value = 30 };
        CheckBox      _chkBfConstant   = new() { Text = "Constant signal:", AutoSize = true, Checked = false };
        TextBox       _txtBfConstId    = new() { Width = 40, Text = "3C", MaxLength = 4 };
        TextBox       _txtBfConstData  = new() { Width = 200, Text = "FF FF FF FF FF FF FF FF", MaxLength = 24 };
        NumericUpDown _nudBfConstMs    = new() { Width = 55, Minimum = 1, Maximum = 5000, Value = 10 };
        ComboBox      _cmbBfSig        = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
        Button        _btnBfLoadSig    = Btn("Load →", 60);
        CheckBox      _chkBfBroadcast  = new() { Text = "Broadcast (same payload all IDs)", AutoSize = true, Checked = false };
        TextBox       _txtBfBcastData  = new() { Width = 200, Text = "FF FF FF FF FF FF FF FF", MaxLength = 24 };
        Button        _btnBfStart   = Btn("▶ Start",    70);
        Button        _btnBfStop    = Btn("■ Stop",     60);
        Button        _btnBfExport  = Btn("Export CSV", 82);
        Button        _btnBfReplay  = Btn("↺ Replay",   70);
        CheckBox      _chkBfReplayLoop = new() { Text = "Loop", AutoSize = true, Checked = false };
        System.Threading.Timer? _bfConstTimer;
        System.Threading.Timer? _bfReplayTimer;
        ProgressBar   _pgsBf       = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };

        // ── SLCAN tab ─────────────────────────────────────────────────────────
        SerialPort?   _canPort;
        readonly object _canLock = new();
        Thread?       _canRxThread;
        volatile bool _canRxRun;
        // Connection / config
        ComboBox  _cmbCanPort      = new() { Width = 88, DropDownStyle = ComboBoxStyle.DropDownList };
        Button    _btnCanConn      = Btn("Connect",    76);
        Button    _btnCanDisc      = Btn("Disconnect", 82);
        Button    _btnCanOpen      = Btn("Open CAN",   72);
        Button    _btnCanClose     = Btn("Close CAN",  76);
        Button    _btnCanVer       = Btn("Version",    62);
        Label     _lblCanStatus    = new() { AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(4, 5, 0, 0) };
        ComboBox  _cmbNomRate      = new() { Width = 68, DropDownStyle = ComboBoxStyle.DropDownList };
        ComboBox  _cmbFdRate       = new() { Width = 52, DropDownStyle = ComboBoxStyle.DropDownList };
        CheckBox  _chkCanSilent    = new() { Text = "Silent",   AutoSize = true };
        CheckBox  _chkCanAutoRetx  = new() { Text = "AutoRetx", AutoSize = true };
        // Signal tab TX
        ComboBox      _cmbCanFrameType = new() { Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
        TextBox       _txtCanId    = new() { Width = 72, Text = "123",  MaxLength = 9 };
        ComboBox      _cmbCanDlc   = new() { Width = 68, DropDownStyle = ComboBoxStyle.DropDownList };
        TextBox       _txtCanData  = new() { Width = 400, Text = "DE AD BE EF 00 00 00 00", MaxLength = 192 };
        NumericUpDown _nudCanMs    = new() { Width = 55, Minimum = 1, Maximum = 10000, Value = 100 };
        TextBox       _txtCanMod   = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
                                             Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(28,28,28), ForeColor = Color.White };
        Button        _btnCanSend  = Btn("Send Once",   78);
        Button        _btnCanLoop  = Btn("▶ Loop",      62);
        Button        _btnCanStop  = Btn("■ Stop",      54);
        System.Threading.Timer? _canTxTimer;
        byte[] _canTxData = new byte[64];
        // Multi-signal tab
        DataGridView  _canGrid      = new();
        Button        _btnCanMAdd   = Btn("+ Add",      60);
        Button        _btnCanMDel   = Btn("− Remove",   72);
        Button        _btnCanMStart = Btn("▶ Start All",84);
        Button        _btnCanMStop  = Btn("■ Stop All", 76);
        Label         _lblCanMStatus = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(6,5,0,0) };
        readonly List<CanSigRow> _canMultiRows = new();
        System.Threading.Timer? _canMultiTimer;
        // Brute force tab
        TextBox       _txtCanBfStart  = new() { Width = 54, Text = "000", MaxLength = 8 };
        TextBox       _txtCanBfEnd    = new() { Width = 54, Text = "7FF", MaxLength = 8 };
        TextBox       _txtCanBfStep   = new() { Width = 40, Text = "01",  MaxLength = 4 };
        NumericUpDown _nudCanBfDelay  = new() { Width = 55, Minimum = 0, Maximum = 5000, Value = 5 };
        NumericUpDown _nudCanBfRxTo   = new() { Width = 55, Minimum = 5, Maximum = 2000, Value = 20 };
        ComboBox      _cmbCanBfType   = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        NumericUpDown _nudCanBfDlc    = new() { Width = 44, Minimum = 0, Maximum = 8, Value = 8 };
        TextBox       _txtCanBfData   = new() { Width = 200, Text = "00 00 00 00 00 00 00 00", MaxLength = 24 };
        CheckBox      _chkCanBfConstant = new() { Text = "Constant signal:", AutoSize = true, Checked = false };
        TextBox       _txtCanBfConstId  = new() { Width = 54, Text = "123", MaxLength = 8 };
        TextBox       _txtCanBfConstData = new() { Width = 200, Text = "00 00 00 00 00 00 00 00", MaxLength = 24 };
        NumericUpDown _nudCanBfConstMs  = new() { Width = 55, Minimum = 1, Maximum = 5000, Value = 10 };
        ComboBox      _cmbCanBfSig      = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
        Button        _btnCanBfLoadSig  = Btn("Load ->", 64);
        ProgressBar   _pgsCanBf       = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
        Label         _lblCanBfStatus = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(6,5,0,0) };
        DataGridView  _grdCanBf       = new();
        Button        _btnCanBfStart  = Btn("▶ Start", 70);
        Button        _btnCanBfStop   = Btn("■ Stop",  60);
        Button        _btnCanBfExport = Btn("Export CSV", 82);
        Button        _btnCanBfReplay = Btn("Replay", 70);
        CheckBox      _chkCanBfReplayLoop = new() { Text = "Loop", AutoSize = true, Checked = false };
        System.Threading.CancellationTokenSource? _canBfCts;
        volatile bool _canBfActive;
        readonly System.Collections.Concurrent.ConcurrentQueue<SLCAN.RxFrame> _canBfRxQ = new();
        System.Threading.Timer? _canBfConstTimer;
        System.Threading.Timer? _canBfReplayTimer;
        // Log tab
        DataGridView  _grdCan         = new();
        Label         _lblCanStats    = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(4,5,0,0) };
        CheckBox      _chkCanGroupById = new() { Text = "Group by ID", AutoSize = true };
        readonly Dictionary<string,int> _canIdRowMap = new();
        long          _canRxCount, _canTxCount;
        Label         _lblBfStatus = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(6, 5, 0, 0) };
        DataGridView  _grdBf       = new();
        System.Threading.CancellationTokenSource? _bfCts;

        // ── Log ───────────────────────────────────────────────────────────────
        RichTextBox _log = new()
        {
            Dock = DockStyle.Fill, ReadOnly = true,
            Font = new Font("Consolas", 8.5f),
            BackColor = Color.FromArgb(18, 18, 18), ForeColor = Color.LimeGreen,
            ScrollBars = RichTextBoxScrollBars.Vertical, WordWrap = false,
        };
        CheckBox _chkScroll = new() { Text = "Auto-scroll", Checked = true, AutoSize = true, Padding = new Padding(4, 3, 0, 0) };

        // ── Runtime state ─────────────────────────────────────────────────────
        SerialPort?              _port;
        readonly object          _portLock   = new();
        System.Windows.Forms.Timer _timer    = new();  // single-signal UI timer
        System.Threading.Timer?  _multiTimer;           // multi-signal thread-pool timer
        readonly List<SigRow>    _multiRows  = new();
        long                     _count;
        readonly byte[]          _data       = new byte[8];

        // ── Constructor ───────────────────────────────────────────────────────
        public MainForm()
        {
            Text          = "LIN / CAN Signal Generator";
            Size          = new Size(1020, 700);
            MinimumSize   = new Size(760, 520);
            Font          = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;

            BuildUI();

            _btnConn.Click   += OnConnect;
            _btnDisc.Click   += OnDisconnect;
            _btnOnce.Click   += (_, _) => DoSend();
            _btnStart.Click  += OnStart;
            _btnStop.Click   += OnStop;
            _txtId.TextChanged += (_, _) => UpdatePidLabel();
            foreach (var tb in _txtD) tb.KeyDown += OnLinSingleDataKeyDown;
            _txtCanData.KeyDown += OnCanSingleDataKeyDown;

            _btnMAdd.Click   += (_, _) => AddMultiRow();
            _btnMDel.Click   += (_, _) => RemoveMultiRow();
            _btnMStart.Click += MultiStart;
            _btnMStop.Click  += MultiStop;

            _btnBfStart.Click  += BruteStart;
            _btnBfStop.Click   += BruteStop;
            _btnBfExport.Click += BruteExport;

            _btnDisc.Enabled  = false;
            _btnOnce.Enabled  = false;
            _btnStart.Enabled = false;
            _btnStop.Enabled  = false;
            _btnMStart.Enabled = false;
            _btnMStop.Enabled  = false;
            _btnBfStart.Enabled = false;
            _btnBfStop.Enabled  = false;

            _timer.Tick += (_, _) => DoSend();

            FormClosed += (_, _) =>
            {
                _timer.Stop();
                _multiTimer?.Dispose();
                _port?.Dispose();
            };

            RefreshPorts();
            UpdatePidLabel();
        }

        // ── UI construction ───────────────────────────────────────────────────
        void BuildUI()
        {
            // ── MenuStrip ────────────────────────────────────────────────────
            var menu  = new MenuStrip();
            var mFile = new ToolStripMenuItem("File");
            var miNew  = new ToolStripMenuItem("New Config",   null, (_, _) => NewConfig())   { ShortcutKeys = Keys.Control | Keys.N };
            var miOpen = new ToolStripMenuItem("Open Config…", null, (_, _) => OpenConfig())  { ShortcutKeys = Keys.Control | Keys.O };
            var miSave = new ToolStripMenuItem("Save Config…", null, (_, _) => SaveConfig())  { ShortcutKeys = Keys.Control | Keys.S };
            var miExit = new ToolStripMenuItem("Exit",         null, (_, _) => Close());
            mFile.DropDownItems.AddRange(new ToolStripItem[] { miNew, miOpen, miSave, new ToolStripSeparator(), miExit });
            menu.Items.Add(mFile);
            MainMenuStrip = menu;

            // ── Connection bar ────────────────────────────────────────────────
            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 36,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                Padding = new Padding(4, 4, 0, 0),
            };
            var btnR = Btn("↻", 26); btnR.Click += (_, _) => RefreshPorts();
            top.Controls.AddRange(new Control[] { L("Port:"), _cmbPort, btnR, L("  LIN baud:"), _txtBaud, _btnConn, _btnDisc, _lblStatus });

            // ── Log panel ─────────────────────────────────────────────────────
            var logPanel = new Panel { Dock = DockStyle.Bottom, Height = 180 };
            var logTlp   = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            logTlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logTlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            logTlp.Controls.Add(_log, 0, 0);
            var logBar   = Flow();
            var btnClear = Btn("Clear", 52); btnClear.Click += (_, _) => _log.Clear();
            logBar.Controls.Add(btnClear); logBar.Controls.Add(_chkScroll);
            logTlp.Controls.Add(logBar, 0, 1);
            var logBox = new GroupBox { Text = "Log", Dock = DockStyle.Fill };
            logBox.Controls.Add(logTlp);
            logPanel.Controls.Add(logBox);

            // ── TabControl ────────────────────────────────────────────────────
            var tabs      = new TabControl { Dock = DockStyle.Fill };
            var linPanel  = new Panel { Dock = DockStyle.Fill };
            var linTabs   = new TabControl { Dock = DockStyle.Fill };
            var tpSig     = new TabPage("Signal");       tpSig.Controls.Add(BuildSignalTab());  linTabs.TabPages.Add(tpSig);
            var tpMulti   = new TabPage("Multi-Signal"); tpMulti.Controls.Add(BuildMultiTab()); linTabs.TabPages.Add(tpMulti);
            var tpBrute   = new TabPage("Brute Force");  tpBrute.Controls.Add(BuildBruteTab()); linTabs.TabPages.Add(tpBrute);
            linPanel.Controls.Add(linTabs);
            var tpLinRoot = new TabPage("LIN");         tpLinRoot.Controls.Add(linPanel);        tabs.TabPages.Add(tpLinRoot);
            var tpCanRoot = new TabPage("CAN / SLCAN"); tpCanRoot.Controls.Add(BuildSlcanTab()); tabs.TabPages.Add(tpCanRoot);

            // ── Add to form in correct dock order ─────────────────────────────
            // WinForms docks in reverse z-order (last-added = lowest z = docked first).
            // To get: menu at top, connection bar below it, log at bottom, tabs fill:
            //   add Fill first, then Bottom, then Top controls, then menu last.
            Controls.Add(tabs);                                              // Fill
            Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
            Controls.Add(logPanel);                                          // Bottom
            Controls.Add(top);                                               // Top — docked second, appears below menu
            Controls.Add(menu);                                              // Top — docked first, appears at very top
            linPanel.Controls.Add(top);
        }

        Panel BuildSignalTab()
        {
            // Use pure DockStyle nesting — avoids TableLayoutPanel AutoSize row
            // collapsing inside a TabPage, which hides the generator buttons.
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };

            // ── Generator (Bottom) ────────────────────────────────────────────
            var genGrp  = new GroupBox { Text = "Generator", Dock = DockStyle.Bottom, Padding = new Padding(6, 14, 6, 4) };
            var genFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            genFlow.Controls.AddRange(new Control[] { L("Interval:"), _nudMs, L("ms  "), _btnOnce, _btnStart, _btnStop, _lblCount });
            genGrp.AutoSize = true;
            genGrp.Controls.Add(genFlow);

            // ── Inner panel: Frame on top, Modifiers fills rest ───────────────
            // SplitContainer: Frame config on top, Modifiers fill the rest (user-resizable)
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                Panel1MinSize = 100,
                Panel2MinSize = 60,
            };

            // ── Top pane: frame config ────────────────────────────────────────
            var fGrp    = new GroupBox { Text = "Frame", Dock = DockStyle.Fill, Padding = new Padding(6, 14, 6, 4) };
            var fLayout = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, RowCount = 4 };
            fLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            fLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            fLayout.Controls.Add(L("ID (hex):"), 0, 0);
            var idRow = Flow(); idRow.Controls.Add(_txtId); idRow.Controls.Add(_lblPid);
            fLayout.SetColumnSpan(idRow, 3); fLayout.Controls.Add(idRow, 1, 0);

            fLayout.Controls.Add(L("Len:"), 0, 1);
            var lenRow = Flow(); lenRow.Controls.Add(_nudLen);
            lenRow.Controls.Add(L("  CS:")); lenRow.Controls.Add(_rbV2); lenRow.Controls.Add(_rbV1);
            fLayout.SetColumnSpan(lenRow, 3); fLayout.Controls.Add(lenRow, 1, 1);

            var dRow0 = DataRow(0); fLayout.SetColumnSpan(dRow0, 4); fLayout.Controls.Add(dRow0, 0, 2);
            var dRow1 = DataRow(4); fLayout.SetColumnSpan(dRow1, 4); fLayout.Controls.Add(dRow1, 0, 3);
            fGrp.Controls.Add(fLayout);
            split.Panel1.Controls.Add(fGrp);

            // ── Bottom pane: modifiers ────────────────────────────────────────
            var mGrp = new GroupBox { Text = "Modifiers  (Dx = expr, applied before each send)", Dock = DockStyle.Fill, Padding = new Padding(6, 14, 6, 4) };
            mGrp.Controls.Add(_txtMod);
            split.Panel2.Controls.Add(mGrp);

            // Set splitter after layout so Panel1MinSize is respected
            split.SplitterDistance = 155;

            // panel: fill first, generator bottom last
            panel.Controls.Add(split);   // Fill — added first
            panel.Controls.Add(genGrp);  // Bottom — added last, docks at bottom
            return panel;
        }

        Panel BuildMultiTab()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };

            // Toolbar
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false, Padding = new Padding(2) };
            bar.Controls.AddRange(new Control[] { _btnMAdd, _btnMDel,
                new Label { Text = "   ", AutoSize = true },
                _btnMStart, _btnMStop, _lblMStatus });
            // Grid — must be added before the toolbar so it gets higher z-order
            // (WinForms docks lowest-z-order first; Fill control must be docked before Top)
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows    = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.RowHeadersVisible     = false;
            _grid.SelectionMode         = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeRowsMode      = DataGridViewAutoSizeRowsMode.AllCells;
            _grid.BackgroundColor       = Color.FromArgb(30, 30, 30);
            _grid.DefaultCellStyle.BackColor  = Color.FromArgb(28, 28, 28);
            _grid.DefaultCellStyle.ForeColor  = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(45, 45, 48);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _grid.GridColor = Color.FromArgb(60, 60, 60);
            _grid.BorderStyle = BorderStyle.None;

            // ── Columns ──────────────────────────────────────────────────────
            var colOn   = new DataGridViewCheckBoxColumn { HeaderText = "On",   Width = 32,  Name = "On" };
            var colId   = new DataGridViewTextBoxColumn  { HeaderText = "ID",   Width = 48,  Name = "Id" };
            var colData = new DataGridViewTextBoxColumn  { HeaderText = "Data (hex)", Width = 200, Name = "Data",
                                                           AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill };
            var colLen  = new DataGridViewTextBoxColumn  { HeaderText = "Len",  Width = 40,  Name = "Len" };
            var colCs   = new DataGridViewComboBoxColumn { HeaderText = "CS",   Width = 50,  Name = "CS",
                                                           FlatStyle = FlatStyle.Flat };
            ((DataGridViewComboBoxColumn)colCs).Items.AddRange("V2", "V1");
            var colMs   = new DataGridViewTextBoxColumn  { HeaderText = "ms",   Width = 60,  Name = "Ms" };
            var colMod  = new DataGridViewTextBoxColumn  { HeaderText = "Modifier", Width = 130, Name = "Mod" };
            var colSent = new DataGridViewTextBoxColumn  { HeaderText = "Sent", Width = 64,  Name = "Sent",
                                                           ReadOnly = true };
            _grid.Columns.AddRange(colOn, colId, colData, colLen, colCs, colMs, colMod, colSent);

            panel.Controls.Add(_grid);  // Fill — added first
            panel.Controls.Add(bar);    // Top  — added last, docks above grid

            // Right-click context menu on multi-signal grid
            var gridCtx = new ContextMenuStrip();
            var ctxSetConst = gridCtx.Items.Add("Set as Brute Constant Signal");
            ctxSetConst.Click += (_, _) =>
            {
                if (_grid.CurrentRow == null) return;
                LoadRowIntoBfConst(_grid.CurrentRow.Index);
            };
            gridCtx.Opening += (_, _) => ctxSetConst.Enabled = _grid.CurrentRow != null && !_grid.CurrentRow.IsNewRow;
            _grid.ContextMenuStrip = gridCtx;

            // Live-edit: push grid changes into running SigRows immediately
            _grid.CellValueChanged += OnGridCellChanged;
            _grid.CurrentCellDirtyStateChanged += (_, _) =>
            {
                // Commit checkbox/combo changes without requiring the user to leave the cell
                if (_grid.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
                    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.EditingControlShowing += OnLinMultiEditingControlShowing;
            _grid.CellEndEdit += OnLinMultiCellEndEdit;

            // Seed two example rows
            AddMultiRow("06", "F0 0F 00 00 00 00 00 00", 2, true, 100, "D0=D0+1");
            AddMultiRow("22", "FF FF FF FF FF FF FF FF", 8, true, 200, "");

            return panel;
        }

        // ── Multi-signal row helpers ──────────────────────────────────────────
        void AddMultiRow(string id = "00", string data = "FF FF FF FF FF FF FF FF",
                         int len = 8, bool enhanced = true, int ms = 100, string mod = "")
        {
            int r = _grid.Rows.Add();
            _grid.Rows[r].Cells["On"].Value   = true;
            _grid.Rows[r].Cells["Id"].Value   = id;
            _grid.Rows[r].Cells["Data"].Value = data;
            _grid.Rows[r].Cells["Len"].Value  = len.ToString();
            _grid.Rows[r].Cells["CS"].Value   = enhanced ? "V2" : "V1";
            _grid.Rows[r].Cells["Ms"].Value   = ms.ToString();
            _grid.Rows[r].Cells["Mod"].Value  = mod;
            _grid.Rows[r].Cells["Sent"].Value = "0";
        }

        void RemoveMultiRow()
        {
            if (_grid.SelectedRows.Count > 0)
                foreach (DataGridViewRow r in _grid.SelectedRows)
                    if (!r.IsNewRow) _grid.Rows.Remove(r);
        }

        void SendLinMultiRowOnce(int rowIndex)
        {
            if (_port?.IsOpen != true) return;
            if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return;
            var row = _grid.Rows[rowIndex];
            if (row.IsNewRow) return;

            byte id = (byte)(ParseHexByte(row.Cells["Id"].Value?.ToString() ?? "00") & 0x3F);
            var data = (row.Cells["Data"].Value?.ToString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => ParseHexByte(t)).Take(8).ToArray();
            if (data.Length < 8) Array.Resize(ref data, 8);
            int len = int.TryParse(row.Cells["Len"].Value?.ToString(), out int l) ? Math.Clamp(l, 1, 8) : 8;
            bool enhanced = (row.Cells["CS"].Value?.ToString() ?? "V2") == "V2";
            byte[] frame = LIN.HostSend(id, data, len, enhanced);

            try
            {
                lock (_portLock)
                {
                    if (_port?.IsOpen != true) return;
                    _port.Write(frame, 0, 16);
                    _port.BaseStream.Flush();
                }
            }
            catch { }
        }

        // ── Port detection (WMI) ──────────────────────────────────────────────
        static Dictionary<string, string> ScanLinTestPorts()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var searcher = new ManagementObjectSearcher("Select * From Win32_PnPEntity");
                foreach (ManagementObject dev in searcher.Get())
                {
                    string? name = dev.GetPropertyValue("Name")?.ToString();
                    if (name == null) continue;
                    bool match = name.Contains("LINTest-MI", StringComparison.OrdinalIgnoreCase)
                              || name.Contains("USB Serial Device", StringComparison.OrdinalIgnoreCase)
                              || name.Contains("CH340",  StringComparison.OrdinalIgnoreCase)
                              || name.Contains("CH341",  StringComparison.OrdinalIgnoreCase);
                    if (!match) continue;
                    var m = Regex.Match(name, @"\(COM(\d+)\)");
                    if (m.Success) result[$"COM{m.Groups[1].Value}"] = name;
                }
            }
            catch { }
            return result;
        }

        void RefreshPorts()
        {
            _cmbPort.Items.Clear();
            var known = ScanLinTestPorts();
            var all   = SerialPort.GetPortNames().OrderBy(x => {
                var d = Regex.Match(x, @"\d+"); return d.Success ? int.Parse(d.Value) : 999;
            });
            int autoSel = -1;
            foreach (var p in all)
            {
                string label = known.TryGetValue(p, out var desc) ? $"{p}  ← {desc}" : p;
                _cmbPort.Items.Add(label);
                if (known.ContainsKey(p) && autoSel < 0) autoSel = _cmbPort.Items.Count - 1;
            }
            if (autoSel >= 0) _cmbPort.SelectedIndex = autoSel;
            else if (_cmbPort.Items.Count > 0) _cmbPort.SelectedIndex = 0;

            if (known.Count > 0)
                AddLog($"LINTest device: {string.Join(", ", known.Select(kv => $"{kv.Key} = {kv.Value}"))}", LogColor.Info);
            else
                AddLog("No LINTest-MI detected via WMI — select port manually.", LogColor.Warn);
        }

        string SelectedPort()
        {
            string item = _cmbPort.SelectedItem?.ToString() ?? "";
            var m = Regex.Match(item, @"^(COM\d+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : item.Trim();
        }

        // ── Connect / Disconnect ──────────────────────────────────────────────
        void OnConnect(object? s, EventArgs e)
        {
            if (_cmbPort.SelectedItem == null) { AddLog("No port selected.", LogColor.Error); return; }
            if (!int.TryParse(_txtBaud.Text, out int linBaud) || linBaud < 4800 || linBaud > 20000)
            { AddLog("LIN baud must be 4800–20000.", LogColor.Error); return; }

            string portName = SelectedPort();
            _btnConn.Enabled = false;
            AddLog($"--- Connecting {portName} @ USB=460800 / LIN={linBaud} ---", LogColor.Warn);

            System.Threading.Tasks.Task.Run(() =>
            {
                SerialPort? p = null;
                try
                {
                    // Probe: DTR toggle to wake CH340 (mirrors Form1.cs lines 2696-2707)
                    try
                    {
                        using var probe = new SerialPort(portName, 460800, Parity.None, 8, StopBits.One);
                        probe.DtrEnable = true;
                        probe.Open();
                        Thread.Sleep(50);
                    }
                    catch { }

                    p = new SerialPort(portName, 460800, Parity.None, 8, StopBits.One);
                    p.Encoding = System.Text.Encoding.Default;
                    p.DtrEnable = true;
                    p.ReadTimeout = 500;
                    p.ReceivedBytesThreshold = 16;
                    p.Open();
                    p.DiscardInBuffer();
                    Invoke(() => AddLog($"  Opened  IsOpen={p.IsOpen}", LogColor.Info));

                    // Arm dongle: mode=0 → 100ms → mode=1 → 100ms
                    var m0 = LIN.ModeCommand(0, linBaud, 28, 100);
                    lock (_portLock) { p.Write(m0, 0, 16); p.BaseStream.Flush(); }
                    Invoke(() => AddLog($"  TX mode=0  [{ByteHex(m0)}]", LogColor.Info));
                    Thread.Sleep(100);

                    var m1 = LIN.ModeCommand(1, linBaud, 28, 100);
                    lock (_portLock) { p.Write(m1, 0, 16); p.BaseStream.Flush(); }
                    Invoke(() => AddLog($"  TX mode=1  [{ByteHex(m1)}]", LogColor.Info));
                    Thread.Sleep(100);

                    _port = p;
                    Invoke(() =>
                    {
                        SetConnected(true);
                        AddLog($"Ready — {portName}  USB=460800  LIN={linBaud}", LogColor.Warn);
                    });
                }
                catch (Exception ex)
                {
                    p?.Dispose();
                    Invoke(() =>
                    {
                        AddLog($"CONNECT FAILED ({ex.GetType().Name}): {ex.Message}", LogColor.Error);
                        _btnConn.Enabled = true;
                    });
                }
            });
        }

        void OnDisconnect(object? s, EventArgs e)
        {
            _timer.Stop();
            _multiTimer?.Dispose(); _multiTimer = null;
            lock (_portLock) { _port?.Dispose(); _port = null; }
            SetConnected(false);
            AddLog("Disconnected.", LogColor.Warn);
        }

        void SetConnected(bool on)
        {
            _btnConn.Enabled   = !on;
            _btnDisc.Enabled   = on;
            _btnOnce.Enabled   = on;
            _btnStart.Enabled  = on;
            _btnMStart.Enabled  = on;
            _btnBfStart.Enabled = on;
            _lblStatus.Text      = on ? $"Connected: {_port?.PortName}" : "Disconnected";
            _lblStatus.ForeColor = on ? Color.LimeGreen : Color.Gray;
        }

        // ── Single-signal timer ───────────────────────────────────────────────
        void OnStart(object? s, EventArgs e)
        {
            _timer.Interval   = (int)_nudMs.Value;
            _timer.Start();
            _btnStart.Enabled = false;
            _btnStop.Enabled  = true;
            AddLog($"Generator running @ {_nudMs.Value} ms", LogColor.Warn);
        }

        void OnStop(object? s, EventArgs e)
        {
            _timer.Stop();
            _btnStart.Enabled = true;
            _btnStop.Enabled  = false;
            AddLog("Generator stopped.", LogColor.Warn);
        }

        void DoSend()
        {
            if (_port == null || !_port.IsOpen) return;
            try
            {
                byte id  = ParseId();
                int  len = (int)_nudLen.Value;

                for (int i = 0; i < 8; i++)
                    _data[i] = ParseHexByte(_txtD[i].Text, 0xFF);

                ApplyModifier(_txtMod.Text, _data);

                // Write back only to boxes the user isn't currently editing
                for (int i = 0; i < 8; i++)
                    if (!_txtD[i].Focused) _txtD[i].Text = _data[i].ToString("X2");

                bool   enh   = _rbV2.Checked;
                byte[] frame = LIN.HostSend(id, _data, len, enh);
                lock (_portLock) { _port.Write(frame, 0, 16); _port.BaseStream.Flush(); }

                _count++;
                _lblCount.Text = $"Sent: {_count}";
                string ts   = DateTime.Now.ToString("HH:mm:ss.fff");
                string data = string.Join(" ", _data.Take(len).Select(b => b.ToString("X2")));
                AddLog($"[{ts}] #{_count,-6} TX  ID={id:X2}(PID={LIN.CalcParity(id):X2}) Len={len} [{data}] cs={frame[14]:X2}", LogColor.TX);
            }
            catch (Exception ex)
            {
                AddLog($"Send error: {ex.Message}", LogColor.Error);
                _timer.Stop(); _btnStart.Enabled = true; _btnStop.Enabled = false;
            }
        }

        // ── Multi-signal scheduler ────────────────────────────────────────────
        void MultiStart(object? s, EventArgs e)
        {
            if (_port == null || !_port.IsOpen) { AddLog("Not connected.", LogColor.Error); return; }

            // Snapshot grid config into SigRow list (must be done on UI thread)
            _multiRows.Clear();
            for (int r = 0; r < _grid.Rows.Count; r++)
            {
                var row = _grid.Rows[r];
                if (row.IsNewRow) continue;
                bool on = row.Cells["On"].Value is true;
                if (!on) continue;

                var sig = new SigRow
                {
                    GridRow    = r,
                    Id         = ParseHexByte(row.Cells["Id"].Value?.ToString() ?? "00"),
                    Len        = int.TryParse(row.Cells["Len"].Value?.ToString(), out int l) ? Math.Clamp(l, 1, 8) : 8,
                    Enhanced   = (row.Cells["CS"].Value?.ToString() ?? "V2") == "V2",
                    IntervalMs = int.TryParse(row.Cells["Ms"].Value?.ToString(), out int ms) ? Math.Max(1, ms) : 100,
                    Modifier   = row.Cells["Mod"].Value?.ToString() ?? "",
                };
                // Parse data bytes
                string rawData = row.Cells["Data"].Value?.ToString() ?? "";
                var bytes = rawData.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                   .Select(t => ParseHexByte(t))
                                   .Take(8).ToArray();
                Array.Copy(bytes, sig.Data, Math.Min(bytes.Length, 8));
                sig.Len    = Math.Min(sig.Len, bytes.Length > 0 ? Math.Max(bytes.Length, sig.Len) : sig.Len);
                sig.NextMs = Environment.TickCount64; // fire immediately
                _multiRows.Add(sig);
            }

            if (_multiRows.Count == 0) { AddLog("No enabled rows.", LogColor.Warn); return; }

            // Reset sent counts in grid
            foreach (var sig in _multiRows)
                _grid.Rows[sig.GridRow].Cells["Sent"].Value = "0";

            _multiTimer?.Dispose();
            _multiTimer = new System.Threading.Timer(MultiTick, null, 0, 5); // 5ms tick

            _btnMStart.Enabled = false;
            _btnMStop.Enabled  = true;
            _lblMStatus.Text   = $"Running {_multiRows.Count} signal(s)";
            AddLog($"Multi-signal started: {_multiRows.Count} active row(s)", LogColor.Warn);
        }

        void MultiStop(object? s, EventArgs e)
        {
            _multiTimer?.Dispose(); _multiTimer = null;
            _btnMStart.Enabled = true;
            _btnMStop.Enabled  = false;
            _lblMStatus.Text   = $"Stopped  ({_multiRows.Sum(r => r.Count)} total sent)";
            AddLog("Multi-signal stopped.", LogColor.Warn);
        }

        void MultiTick(object? _)
        {
            if (_port == null || !_port.IsOpen) return;
            long now = Environment.TickCount64;

            foreach (var sig in _multiRows)
            {
                if (now < sig.NextMs) continue;
                sig.NextMs = now + sig.IntervalMs;

                // If the user edited the Data cell while running, reset the working buffer
                var pending = sig.PendingData;
                if (pending != null) { sig.PendingData = null; Array.Copy(pending, sig.Data, 8); }

                // Apply modifier to sig's own data buffer
                ApplyModifier(sig.Modifier, sig.Data);

                byte[] frame = LIN.HostSend(sig.Id, sig.Data, sig.Len, sig.Enhanced);
                try
                {
                    lock (_portLock)
                    {
                        if (_port == null || !_port.IsOpen) return;
                        _port.Write(frame, 0, 16);
                        _port.BaseStream.Flush();
                    }
                    sig.Count++;

                    // Update grid count — non-blocking post to UI thread
                    int   gridRow = sig.GridRow;
                    long  cnt     = sig.Count;
                    string liveData = string.Join(" ", sig.Data.Select(b => b.ToString("X2")));
                    try { BeginInvoke(() =>
                    {
                        if (gridRow < _grid.Rows.Count)
                        {
                            _grid.Rows[gridRow].Cells["Data"].Value = liveData;
                            _grid.Rows[gridRow].Cells["Sent"].Value = cnt.ToString();
                        }
                    }); }
                    catch { }
                }
                catch { }
            }
        }

        // Live-edit handler: push grid cell changes into the running SigRow instantly
        void OnGridCellChanged(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var sig = _multiRows.FirstOrDefault(r => r.GridRow == e.RowIndex);
            if (sig == null) return; // not currently running — no action needed

            var cell = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            string col = _grid.Columns[e.ColumnIndex].Name;
            string val = cell.Value?.ToString() ?? "";

            switch (col)
            {
                case "Id":
                    sig.Id = ParseHexByte(val);
                    break;
                case "Data":
                    // Reset the accumulator buffer — use PendingData for thread-safe handoff
                    var bytes = val.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                   .Select(t => ParseHexByte(t)).Take(8).ToArray();
                    var pending = new byte[8];
                    Array.Copy(bytes, pending, Math.Min(bytes.Length, 8));
                    sig.PendingData = pending; // timer thread picks this up on next tick
                    break;
                case "Len":
                    if (int.TryParse(val, out int l)) sig.Len = Math.Clamp(l, 1, 8);
                    break;
                case "CS":
                    sig.Enhanced = val == "V2";
                    break;
                case "Ms":
                    if (int.TryParse(val, out int ms)) sig.IntervalMs = Math.Max(1, ms);
                    break;
                case "Mod":
                    sig.Modifier = val;
                    break;
            }
        }

        // ── Modifier evaluator ────────────────────────────────────────────────
        // Supports:
        //   D0=expr          — set single byte
        //   D[0..7]=expr     — spread: set bytes 0-7 (inclusive range, any size array)
        //   D[2..5]=expr     — spread subset
        void OnLinMultiCellEndEdit(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (_grid.Columns[e.ColumnIndex].Name != "Mod") return;
            ApplyLinGridModifierPreview(e.RowIndex);
        }

        void ApplyLinGridModifierPreview(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return;
            var row = _grid.Rows[rowIndex];
            if (row.IsNewRow) return;

            string modifier = row.Cells["Mod"].Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(modifier)) return;

            var data = new byte[8];
            var bytes = (row.Cells["Data"].Value?.ToString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => ParseHexByte(t)).Take(8).ToArray();
            Array.Copy(bytes, data, Math.Min(bytes.Length, data.Length));
            ApplyModifier(modifier, data);

            string updated = string.Join(" ", data.Select(b => b.ToString("X2")));
            string current = row.Cells["Data"].Value?.ToString() ?? "";
            if (!string.Equals(current, updated, StringComparison.OrdinalIgnoreCase))
                row.Cells["Data"].Value = updated;
        }

        static IEnumerable<string> EnumerateModifierStatements(string modifier)
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

        static void ApplyModifier(string modifier, byte[] data)
        {
            int maxIdx = data.Length - 1;
            foreach (string line in EnumerateModifierStatements(modifier))
            {
                // Spread: D[lo..hi]=expr  (e.g. D[0..7]=D[0]+1)
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

                // Single: D0=expr .. D63=expr
                var m = Regex.Match(line, @"^D(\d+)\s*=\s*(.+)$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                int idx = int.Parse(m.Groups[1].Value);
                if (idx < 0 || idx > maxIdx) continue;
                try { data[idx] = (byte)(Expr.Eval(m.Groups[2].Value, data) & 0xFF); } catch { }
            }
        }

        // ── Brute-force tab builder ───────────────────────────────────────────
        Panel BuildBruteTab()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };

            // ── Config + control bar (Top) ────────────────────────────────────
            var cfgGrp  = new GroupBox { Text = "Config", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 14, 6, 6) };
            var cfgStack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };

            // Row 1a — scan parameters
            var cfgRow1a = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            cfgRow1a.Controls.AddRange(new Control[]
            {
                L("ID from:"), _txtBfStart, L("to:"), _txtBfEnd,
                L("  Step:"), _txtBfStep, L("(hex)"),
                L("  Delay:"), _nudBfDelay, L("ms"),
                L("  RX Timeout:"), _nudBfRxTimeout, L("ms"),
                L("  DLC:"), _nudBfDlc,
                L("  CS:"), _rbBfV2, _rbBfV1,
            });

            // Row 1b — action buttons
            var cfgRow1b = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            cfgRow1b.Controls.AddRange(new Control[] { _btnBfStart, _btnBfStop, _btnBfExport });

            // Row 2 — constant signal + broadcast
            var cfgRow2 = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            cfgRow2.Controls.AddRange(new Control[]
            {
                _chkBfConstant, L("ID:"), _txtBfConstId, L("Data:"), _txtBfConstData, L("every"), _nudBfConstMs, L("ms"),
                L("  From signal:"), _cmbBfSig, _btnBfLoadSig,
            });

            // Row 3 — broadcast
            var cfgRow3 = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            cfgRow3.Controls.AddRange(new Control[]
            {
                _chkBfBroadcast, L("Data:"), _txtBfBcastData,
            });

            _btnBfLoadSig.Click += (_, _) => LoadSigIntoBfConst();
            _cmbBfSig.DropDown  += (_, _) => RefreshSigCombo();

            cfgStack.Controls.Add(cfgRow1a);
            cfgStack.Controls.Add(cfgRow1b);
            cfgStack.Controls.Add(cfgRow2);
            cfgStack.Controls.Add(cfgRow3);
            cfgGrp.Controls.Add(cfgStack);

            // ── Progress bar ──────────────────────────────────────────────────
            var prgPanel = new Panel { Dock = DockStyle.Top, Height = 22, Padding = new Padding(4, 2, 4, 2) };
            prgPanel.Controls.Add(_pgsBf);

            // ── Status label ──────────────────────────────────────────────────
            var statPanel = new Panel { Dock = DockStyle.Top, Height = 22 };
            statPanel.Controls.Add(_lblBfStatus);

            // ── Results grid (Fill) ───────────────────────────────────────────
            _grdBf.Dock                    = DockStyle.Fill;
            _grdBf.AllowUserToAddRows      = false;
            _grdBf.AllowUserToDeleteRows   = false;
            _grdBf.ReadOnly                = true;
            _grdBf.RowHeadersVisible       = false;
            _grdBf.SelectionMode           = DataGridViewSelectionMode.FullRowSelect;
            _grdBf.BackgroundColor         = Color.FromArgb(28, 28, 28);
            _grdBf.DefaultCellStyle.BackColor        = Color.FromArgb(28, 28, 28);
            _grdBf.DefaultCellStyle.ForeColor        = Color.White;
            _grdBf.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(36, 36, 36);
            _grdBf.ColumnHeadersDefaultCellStyle.BackColor  = Color.FromArgb(45, 45, 48);
            _grdBf.ColumnHeadersDefaultCellStyle.ForeColor  = Color.White;
            _grdBf.GridColor   = Color.FromArgb(60, 60, 60);
            _grdBf.BorderStyle = BorderStyle.None;

            _grdBf.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID",      Name = "BfId",    Width = 48  });
            _grdBf.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "PID",     Name = "BfPid",   Width = 48  });
            _grdBf.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Payload (8 bytes sent)", Name = "BfPayload", Width = 200,
                                                                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grdBf.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Response",Name = "BfResp",  Width = 72  });
            _grdBf.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Resp Data",Name = "BfRespData", Width = 180 });

            // ── Replay bar (Top, below progress) ────────────────────────────────
            var replayPanel = new Panel { Dock = DockStyle.Top, Height = 28 };
            var replayFlow  = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Padding = new Padding(2, 2, 0, 0) };
            replayFlow.Controls.AddRange(new Control[] { _btnBfReplay, _chkBfReplayLoop });
            replayPanel.Controls.Add(replayFlow);

            _btnBfReplay.Click += BruteReplay;

            // Right-click on results grid: replay row
            var bfCtx = new ContextMenuStrip();
            var ctxReplay = bfCtx.Items.Add("↺ Replay this frame");
            ctxReplay.Click += (_, _) => BruteReplaySingle();
            var ctxLoadConst = bfCtx.Items.Add("Set as Constant Signal");
            ctxLoadConst.Click += (_, _) =>
            {
                if (_grdBf.CurrentRow == null || _grdBf.CurrentRow.IsNewRow) return;
                _txtBfConstId.Text   = _grdBf.CurrentRow.Cells["BfId"].Value?.ToString()      ?? "00";
                _txtBfConstData.Text = _grdBf.CurrentRow.Cells["BfPayload"].Value?.ToString() ?? "FF FF FF FF FF FF FF FF";
                _chkBfConstant.Checked = true;
                AddLog($"Set row ID={_txtBfConstId.Text} as Brute Constant Signal.", LogColor.Info);
            };
            bfCtx.Opening += (_, _) =>
            {
                bool hasRow = _grdBf.CurrentRow != null && !_grdBf.CurrentRow.IsNewRow;
                ctxReplay.Enabled   = hasRow;
                ctxLoadConst.Enabled = hasRow;
            };
            _grdBf.ContextMenuStrip = bfCtx;

            // Dock order: Fill first, then Top controls last (each added Top docks above previous)
            panel.Controls.Add(_grdBf);      // Fill
            panel.Controls.Add(replayPanel); // Top
            panel.Controls.Add(statPanel);   // Top
            panel.Controls.Add(prgPanel);    // Top
            panel.Controls.Add(cfgGrp);      // Top
            return panel;
        }

        // ── Signal-to-BruteForce helpers ─────────────────────────────────────
        // Populate the "From signal" combo with current multi-signal rows
        void RefreshSigCombo()
        {
            _cmbBfSig.Items.Clear();
            for (int r = 0; r < _grid.Rows.Count; r++)
            {
                if (_grid.Rows[r].IsNewRow) continue;
                string id   = _grid.Rows[r].Cells["Id"].Value?.ToString()   ?? "??";
                string data = _grid.Rows[r].Cells["Data"].Value?.ToString() ?? "";
                string preview = data.Length > 23 ? data[..23] + "…" : data;
                _cmbBfSig.Items.Add($"[{r}] ID={id}  {preview}");
            }
            if (_cmbBfSig.Items.Count > 0) _cmbBfSig.SelectedIndex = 0;
        }

        void LoadRowIntoBfConst(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return;
            var row = _grid.Rows[rowIndex];
            _txtBfConstId.Text   = row.Cells["Id"].Value?.ToString()   ?? "00";
            _txtBfConstData.Text = row.Cells["Data"].Value?.ToString() ?? "FF FF FF FF FF FF FF FF";
            if (int.TryParse(row.Cells["Ms"].Value?.ToString(), out int ms))
                _nudBfConstMs.Value = Math.Max(_nudBfConstMs.Minimum, Math.Min(_nudBfConstMs.Maximum, ms));
            _chkBfConstant.Checked = true;
            // Switch to brute tab so user sees the result
            if (Parent is TabControl tc) { /* already on brute tab */ }
            AddLog($"Loaded row {rowIndex} (ID={_txtBfConstId.Text}) into Brute Constant Signal.", LogColor.Info);
        }

        void LoadSigIntoBfConst()
        {
            int sel = _cmbBfSig.SelectedIndex;
            if (sel < 0) { RefreshSigCombo(); return; }
            // The combo was built from non-new rows in order — find the actual row index
            int mapped = 0;
            for (int r = 0; r < _grid.Rows.Count; r++)
            {
                if (_grid.Rows[r].IsNewRow) continue;
                if (mapped == sel) { LoadRowIntoBfConst(r); return; }
                mapped++;
            }
        }

        // ── Brute-force replay ───────────────────────────────────────────────
        void BruteReplaySingle()
        {
            if (_port == null || !_port.IsOpen) { AddLog("Not connected.", LogColor.Error); return; }
            if (_grdBf.CurrentRow == null || _grdBf.CurrentRow.IsNewRow) { AddLog("No row selected.", LogColor.Warn); return; }

            string idStr   = _grdBf.CurrentRow.Cells["BfId"].Value?.ToString()      ?? "00";
            string payload = _grdBf.CurrentRow.Cells["BfPayload"].Value?.ToString() ?? "";
            byte   id      = (byte)(ParseHexByte(idStr) & 0x3F);
            byte[] data    = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(t => ParseHexByte(t)).Take(8).ToArray();
            if (data.Length < 8) Array.Resize(ref data, 8);
            bool enh = _rbBfV2.Checked;
            var frame = LIN.HostSend(id, data, data.Length, enh);
            lock (_portLock) { _port.Write(frame, 0, 16); _port.BaseStream.Flush(); }
            AddLog($"Replayed ID=0x{id:X2}  {payload}", LogColor.Info);
        }

        void BruteReplay(object? s, EventArgs e)
        {
            if (_port == null || !_port.IsOpen) { AddLog("Not connected.", LogColor.Error); return; }
            if (_grdBf.CurrentRow == null || _grdBf.CurrentRow.IsNewRow) { AddLog("No row selected.", LogColor.Warn); return; }

            if (!_chkBfReplayLoop.Checked)
            {
                BruteReplaySingle();
                return;
            }

            // Loop mode: toggle — if already running, stop
            if (_bfReplayTimer != null)
            {
                _bfReplayTimer.Dispose(); _bfReplayTimer = null;
                _bfConstTimer?.Dispose(); _bfConstTimer = null;
                _btnBfReplay.Text = "↺ Replay";
                AddLog("Replay loop stopped.", LogColor.Warn);
                return;
            }

            string idStr   = _grdBf.CurrentRow.Cells["BfId"].Value?.ToString()      ?? "00";
            string payload = _grdBf.CurrentRow.Cells["BfPayload"].Value?.ToString() ?? "";
            byte   id      = (byte)(ParseHexByte(idStr) & 0x3F);
            byte[] data    = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(t => ParseHexByte(t)).Take(8).ToArray();
            if (data.Length < 8) Array.Resize(ref data, 8);
            bool enh  = _rbBfV2.Checked;
            var frame = LIN.HostSend(id, data, data.Length, enh);
            int intervalMs = (int)_nudBfDelay.Value > 0 ? (int)_nudBfDelay.Value : 20;

            _btnBfReplay.Text = "■ Stop Loop";
            AddLog($"Replay loop started: ID=0x{id:X2}  {payload}  every {intervalMs}ms", LogColor.Warn);

            _bfReplayTimer = new System.Threading.Timer(_ =>
            {
                lock (_portLock)
                {
                    if (_port?.IsOpen == true) { _port.Write(frame, 0, 16); _port.BaseStream.Flush(); }
                }
            }, null, 0, intervalMs);

            _bfConstTimer?.Dispose(); _bfConstTimer = null;
            if (_chkBfConstant.Checked)
            {
                byte constId = (byte)(ParseHexByte(_txtBfConstId.Text, 0x3C) & 0x3F);
                byte[] constBytes = _txtBfConstData.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                        .Select(t => ParseHexByte(t)).Take(8).ToArray();
                if (constBytes.Length < 8) Array.Resize(ref constBytes, 8);
                int constDlc = Math.Min((int)_nudBfDlc.Value, 8);
                var constFrame = LIN.HostSend(constId, constBytes, constDlc, enh);
                int constMs = (int)_nudBfConstMs.Value;

                void SendConst()
                {
                    try
                    {
                        lock (_portLock)
                        {
                            if (_port?.IsOpen == true) { _port.Write(constFrame, 0, 16); _port.BaseStream.Flush(); }
                        }
                    }
                    catch { }
                }

                SendConst();
                _bfConstTimer = new System.Threading.Timer(_ => SendConst(), null, constMs, constMs);
            }
        }

        // ── Brute-force runner ────────────────────────────────────────────────
        void BruteStart(object? s, EventArgs e)
        {
            if (_port == null || !_port.IsOpen) { AddLog("Not connected.", LogColor.Error); return; }

            byte idStart    = (byte)(ParseHexByte(_txtBfStart.Text, 0x00) & 0x3F);
            byte idEnd      = (byte)(ParseHexByte(_txtBfEnd.Text,   0x3F) & 0x3F);
            int  step       = Math.Max(1, (int)ParseHexByte(_txtBfStep.Text, 0x11));
            int  delay      = (int)_nudBfDelay.Value;
            int  rxTimeout  = (int)_nudBfRxTimeout.Value;
            int  dlc        = (int)_nudBfDlc.Value;
            bool enh        = _rbBfV2.Checked;
            bool broadcast  = _chkBfBroadcast.Checked;
            byte[] bcastBytes = _txtBfBcastData.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(t => ParseHexByte(t)).Take(8).ToArray();
            if (bcastBytes.Length < 8) Array.Resize(ref bcastBytes, 8);

            if (idEnd < idStart) { AddLog("ID end must be ≥ ID start.", LogColor.Error); return; }

            // Stop any running timers — brute force has exclusive TX
            _timer.Stop(); _btnStart.Enabled = _port != null; _btnStop.Enabled = false;
            _multiTimer?.Dispose(); _multiTimer = null;
            _btnMStart.Enabled = false; _btnMStop.Enabled = false;

            _grdBf.Rows.Clear();
            _pgsBf.Value = 0;
            _lblBfStatus.Text = "Starting…";
            _btnBfStart.Enabled = false;
            _btnBfStop.Enabled  = true;

            _bfCts = new System.Threading.CancellationTokenSource();
            var ct  = _bfCts.Token;

            // Start constant signal timer if enabled
            _bfConstTimer?.Dispose(); _bfConstTimer = null;
            if (_chkBfConstant.Checked)
            {
                byte constId   = (byte)(ParseHexByte(_txtBfConstId.Text, 0x3C) & 0x3F);
                byte[] constBytes = _txtBfConstData.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                        .Select(t => ParseHexByte(t)).Take(8).ToArray();
                if (constBytes.Length < 8) Array.Resize(ref constBytes, 8);
                int constDlc   = Math.Min(dlc, 8);
                var constFrame = LIN.HostSend(constId, constBytes, constDlc, enh);
                int constMs    = (int)_nudBfConstMs.Value;
                void SendConst()
                {
                    try
                    {
                        if (ct.IsCancellationRequested) return;
                        lock (_portLock)
                        {
                            if (_port?.IsOpen == true) { _port.Write(constFrame, 0, 16); _port.BaseStream.Flush(); }
                        }
                    }
                    catch { }
                }
                SendConst();
                _bfConstTimer = new System.Threading.Timer(_ =>
                {
                    SendConst();
                }, null, constMs, constMs);
            }

            // Total iterations
            int total;
            if (broadcast)
                total = idEnd - idStart + 1;
            else
            { int bps = 0; for (int v = 0; v <= 0xFF; v += step) bps++; total = (idEnd - idStart + 1) * bps; }

            System.Threading.Tasks.Task.Run(() =>
            {
                int done = 0;
                try
                {
                    // Flush any stale RX data
                    lock (_portLock) { if (_port?.IsOpen == true) _port.DiscardInBuffer(); }

                    for (byte id = idStart; id <= idEnd && !ct.IsCancellationRequested; id++)
                    {
                        if (broadcast)
                        {
                            // Broadcast: send fixed payload to this ID once
                            var data  = bcastBytes.Take(dlc).ToArray();
                            var frame = LIN.HostSend(id, data, dlc, enh);
                            bool gotResp  = false;
                            var  respData = Array.Empty<byte>();
                            lock (_portLock) { if (_port == null || !_port.IsOpen) return; _port.Write(frame, 0, 16); _port.BaseStream.Flush(); }
                            if (delay > 0) Thread.Sleep(delay);
                            (gotResp, respData) = TryReadFrame(rxTimeout);
                            done++;
                            int pct = (int)(done * 100L / total);
                            string pid = LIN.CalcParity(id).ToString("X2");
                            string pay = string.Join(" ", data.Select(b => b.ToString("X2")));
                            string rsp = gotResp ? "YES" : "-";
                            string rsd = gotResp ? string.Join(" ", respData.Skip(6).Take(respData[5] > 0 && respData[5] <= 8 ? respData[5] : 0).Select(b => b.ToString("X2"))) : "";
                            try { BeginInvoke(() => { int r = _grdBf.Rows.Add(); _grdBf.Rows[r].Cells["BfId"].Value = id.ToString("X2"); _grdBf.Rows[r].Cells["BfPid"].Value = pid; _grdBf.Rows[r].Cells["BfPayload"].Value = pay; _grdBf.Rows[r].Cells["BfResp"].Value = rsp; _grdBf.Rows[r].Cells["BfRespData"].Value = rsd; if (gotResp) _grdBf.Rows[r].DefaultCellStyle.ForeColor = Color.LimeGreen; _pgsBf.Value = Math.Min(100, pct); _lblBfStatus.Text = $"ID=0x{id:X2}  {done}/{total}"; TryScrollToLastRow(_grdBf); }); } catch { return; }
                            continue;
                        }

                        for (int byteVal = 0; byteVal <= 0xFF && !ct.IsCancellationRequested; byteVal += step)
                        {
                            byte bv   = (byte)byteVal;
                            var data  = Enumerable.Repeat(bv, dlc).ToArray();
                            var frame = LIN.HostSend(id, data, dlc, enh);

                            bool gotResp  = false;
                            var  respData = Array.Empty<byte>();

                            lock (_portLock)
                            {
                                if (_port == null || !_port.IsOpen) return;
                                _port.Write(frame, 0, 16);
                                _port.BaseStream.Flush();
                            }

                            if (delay > 0) Thread.Sleep(delay);

                            // Try to read a response frame
                            (gotResp, respData) = TryReadFrame(rxTimeout);

                            done++;
                            int pct    = (int)(done * 100L / total);
                            string pid = LIN.CalcParity(id).ToString("X2");
                            string pay = string.Join(" ", data.Select(b => b.ToString("X2")));
                            string rsp = gotResp ? "YES" : "-";
                            string rsd = gotResp ? string.Join(" ", respData.Skip(6).Take(respData[5] > 0 && respData[5] <= 8 ? respData[5] : 0).Select(b => b.ToString("X2"))) : "";

                            try
                            {
                                BeginInvoke(() =>
                                {
                                    int r = _grdBf.Rows.Add();
                                    _grdBf.Rows[r].Cells["BfId"].Value      = id.ToString("X2");
                                    _grdBf.Rows[r].Cells["BfPid"].Value     = pid;
                                    _grdBf.Rows[r].Cells["BfPayload"].Value = pay;
                                    _grdBf.Rows[r].Cells["BfResp"].Value    = rsp;
                                    _grdBf.Rows[r].Cells["BfRespData"].Value = rsd;
                                    if (gotResp) _grdBf.Rows[r].DefaultCellStyle.ForeColor = Color.LimeGreen;
                                    _pgsBf.Value      = Math.Min(100, pct);
                                    _lblBfStatus.Text = $"ID=0x{id:X2}  Byte=0x{bv:X2}  {done}/{total}";
                                    TryScrollToLastRow(_grdBf);
                                });
                            }
                            catch { return; }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { try { BeginInvoke(() => AddLog($"Brute error: {ex.Message}", LogColor.Error)); } catch { } }
                finally
                {
                    _bfConstTimer?.Dispose(); _bfConstTimer = null;
                    try
                    {
                        BeginInvoke(() =>
                        {
                            _pgsBf.Value       = ct.IsCancellationRequested ? _pgsBf.Value : 100;
                            _lblBfStatus.Text   = ct.IsCancellationRequested ? $"Stopped at {done}/{total}" : $"Done — {done} frames sent";
                            _btnBfStart.Enabled = _port?.IsOpen == true;
                            _btnBfStop.Enabled  = false;
                            _btnMStart.Enabled  = _port?.IsOpen == true;
                            _btnStart.Enabled   = _port?.IsOpen == true;
                        });
                    }
                    catch { }
                }
            }, ct);
        }

        void BruteStop(object? s, EventArgs e)
        {
            _bfCts?.Cancel();
            _bfConstTimer?.Dispose(); _bfConstTimer = null;
            _bfReplayTimer?.Dispose(); _bfReplayTimer = null;
            _btnBfReplay.Text = "↺ Replay";
            _btnBfStop.Enabled = false;
        }

        void BruteExport(object? s, EventArgs e)
        {
            using var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv|All files|*.*", DefaultExt = "csv", Title = "Export Brute Force Results" };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("ID,PID,Payload,Response,RespData");
                foreach (DataGridViewRow r in _grdBf.Rows)
                {
                    if (r.IsNewRow) continue;
                    sb.AppendLine(string.Join(",",
                        r.Cells["BfId"].Value, r.Cells["BfPid"].Value,
                        $"\"{r.Cells["BfPayload"].Value}\"",
                        r.Cells["BfResp"].Value, r.Cells["BfRespData"].Value));
                }
                File.WriteAllText(dlg.FileName, sb.ToString());
                AddLog($"Exported {_grdBf.Rows.Count} rows to {dlg.FileName}", LogColor.Info);
            }
            catch (Exception ex) { AddLog($"Export failed: {ex.Message}", LogColor.Error); }
        }

        // Try to read one validated 16-byte response frame from the dongle.
        // Response frames have cmd byte 0x33/0x44/0x55/0xDD and valid packet checksum.
        (bool ok, byte[] frame) TryReadFrame(int timeoutMs = 300)
        {
            var   buf      = new byte[16];
            int   read     = 0;
            var   deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (read < 16 && DateTime.UtcNow < deadline)
            {
                int avail;
                lock (_portLock)
                {
                    if (_port == null || !_port.IsOpen) return (false, buf);
                    avail = _port.BytesToRead;
                    if (avail > 0)
                    {
                        int n = _port.Read(buf, read, Math.Min(avail, 16 - read));
                        read += n;
                    }
                }
                if (read < 16) Thread.Sleep(1);
            }
            if (read < 16) return (false, buf);

            byte cmd = buf[0];
            if (cmd != 0x33 && cmd != 0x44 && cmd != 0x55 && cmd != 0xDD) return (false, buf);
            int sum = 0; for (int i = 0; i < 15; i++) sum += buf[i];
            byte cs = (byte)((~sum & 0xFF) + 1);
            return (buf[15] == cs, buf);
        }

        // ── Config load / save ────────────────────────────────────────────────
        void NewConfig()
        {
            _txtId.Text = "22";
            for (int i = 0; i < 8; i++) _txtD[i].Text = "FF";
            _nudLen.Value = 8; _rbV2.Checked = true; _nudMs.Value = 100;
            _txtMod.Text  = "D0=D0+1\r\n";
            _grid.Rows.Clear();
            AddMultiRow("06", "F0 0F 00 00 00 00 00 00", 2, true, 100, "D0=D0+1");
            _txtBfStart.Text = "00";
            _txtBfEnd.Text = "3F";
            _txtBfStep.Text = "11";
            _nudBfDelay.Value = 20;
            _nudBfRxTimeout.Value = 30;
            _nudBfDlc.Value = 8;
            _rbBfV2.Checked = true;
            _chkBfConstant.Checked = false;
            _txtBfConstId.Text = "3C";
            _txtBfConstData.Text = "FF FF FF FF FF FF FF FF";
            _nudBfConstMs.Value = 10;
            _chkBfBroadcast.Checked = false;
            _txtBfBcastData.Text = "FF FF FF FF FF FF FF FF";
            _chkBfReplayLoop.Checked = false;
            _grdBf.Rows.Clear();

            _cmbNomRate.SelectedIndex = SLCAN.DefaultNomIdx;
            _cmbFdRate.SelectedIndex  = SLCAN.DefaultFdIdx;
            _chkCanSilent.Checked     = false;
            _chkCanAutoRetx.Checked   = false;
            _cmbCanFrameType.SelectedIndex = 0;
            CanUpdateDlcCombo();
            _txtCanId.Text   = "123";
            SetCanSignalDlc(8);
            _txtCanData.Text = "DE AD BE EF 00 00 00 00";
            _nudCanMs.Value  = 100;
            _txtCanMod.Text  = "";
            _canGrid.Rows.Clear();
            CanAddMultiRow("t", "123", "8", "DE AD BE EF 00 00 00 00", 100, "D0=D0+1");
            CanAddMultiRow("T", "1FFFFFFF", "8", "FF FF FF FF FF FF FF FF", 200, "");
            _txtCanBfStart.Text = "000";
            _txtCanBfEnd.Text = "7FF";
            _txtCanBfStep.Text = "01";
            _nudCanBfDelay.Value = 5;
            _nudCanBfRxTo.Value = 20;
            _cmbCanBfType.SelectedIndex = 0;
            _nudCanBfDlc.Value = 8;
            _txtCanBfData.Text = "00 00 00 00 00 00 00 00";
            _chkCanBfConstant.Checked = false;
            _txtCanBfConstId.Text = "123";
            _txtCanBfConstData.Text = "00 00 00 00 00 00 00 00";
            _nudCanBfConstMs.Value = 10;
            _chkCanBfReplayLoop.Checked = false;
            _grdCanBf.Rows.Clear();
            AddLog("Config reset to defaults.", LogColor.Info);
        }

        void OpenConfig()
        {
            using var dlg = new OpenFileDialog { Filter = "App Config (*.json)|*.json|All files|*.*", Title = "Open Config" };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            try
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(dlg.FileName))!;
                _txtBaud.Text = cfg.LinBaud.ToString();

                // Single signal
                _txtId.Text   = cfg.Signal.Id;
                var bytes = cfg.Signal.Data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < 8; i++) _txtD[i].Text = i < bytes.Length ? bytes[i] : "00";
                _nudLen.Value = Math.Clamp(cfg.Signal.Len, 1, 8);
                (_rbV2.Checked, _rbV1.Checked) = (cfg.Signal.Enhanced, !cfg.Signal.Enhanced);
                _nudMs.Value = Math.Clamp(cfg.Signal.Ms, 1, 60000);
                _txtMod.Text = cfg.Signal.Modifier.Replace("\n", "\r\n");

                // Multi-signal
                _grid.Rows.Clear();
                foreach (var ms in cfg.MultiSignals)
                    AddMultiRow(ms.Id, ms.Data, ms.Len, ms.Enhanced, ms.Ms, ms.Modifier);

                _txtBfStart.Text = cfg.LinBrute.Start;
                _txtBfEnd.Text = cfg.LinBrute.End;
                _txtBfStep.Text = cfg.LinBrute.Step;
                _nudBfDelay.Value = Math.Clamp(cfg.LinBrute.DelayMs, (int)_nudBfDelay.Minimum, (int)_nudBfDelay.Maximum);
                _nudBfRxTimeout.Value = Math.Clamp(cfg.LinBrute.RxTimeoutMs, (int)_nudBfRxTimeout.Minimum, (int)_nudBfRxTimeout.Maximum);
                _nudBfDlc.Value = Math.Clamp(cfg.LinBrute.Dlc, (int)_nudBfDlc.Minimum, (int)_nudBfDlc.Maximum);
                (_rbBfV2.Checked, _rbBfV1.Checked) = (cfg.LinBrute.Enhanced, !cfg.LinBrute.Enhanced);
                _chkBfConstant.Checked = cfg.LinBrute.ConstantEnabled;
                _txtBfConstId.Text = cfg.LinBrute.ConstId;
                _txtBfConstData.Text = cfg.LinBrute.ConstData;
                _nudBfConstMs.Value = Math.Clamp(cfg.LinBrute.ConstMs, (int)_nudBfConstMs.Minimum, (int)_nudBfConstMs.Maximum);
                _chkBfBroadcast.Checked = cfg.LinBrute.BroadcastEnabled;
                _txtBfBcastData.Text = cfg.LinBrute.BroadcastData;
                _chkBfReplayLoop.Checked = cfg.LinBrute.ReplayLoop;
                _grdBf.Rows.Clear();
                foreach (var row in cfg.LinBrute.Results)
                {
                    int r = _grdBf.Rows.Add();
                    _grdBf.Rows[r].Cells["BfId"].Value = row.Id;
                    _grdBf.Rows[r].Cells["BfPid"].Value = row.Pid;
                    _grdBf.Rows[r].Cells["BfPayload"].Value = row.Payload;
                    _grdBf.Rows[r].Cells["BfResp"].Value = row.Response;
                    _grdBf.Rows[r].Cells["BfRespData"].Value = row.RespData;
                    if (string.Equals(row.Response, "YES", StringComparison.OrdinalIgnoreCase))
                        _grdBf.Rows[r].DefaultCellStyle.ForeColor = Color.LimeGreen;
                }

                // CAN / SLCAN
                _cmbNomRate.SelectedIndex = Math.Clamp(cfg.Can.NomRateIndex, 0, Math.Max(0, _cmbNomRate.Items.Count - 1));
                _cmbFdRate.SelectedIndex  = Math.Clamp(cfg.Can.FdRateIndex, 0, Math.Max(0, _cmbFdRate.Items.Count - 1));
                _chkCanSilent.Checked     = cfg.Can.Silent;
                _chkCanAutoRetx.Checked   = cfg.Can.AutoRetransmit;

                _cmbCanFrameType.SelectedIndex = Math.Clamp(cfg.Can.Signal.FrameTypeIndex, 0, Math.Max(0, _cmbCanFrameType.Items.Count - 1));
                CanUpdateDlcCombo();
                _txtCanId.Text   = cfg.Can.Signal.Id;
                SetCanSignalDlc(int.TryParse(cfg.Can.Signal.Dlc, out int canDlc) ? canDlc : 8);
                _txtCanData.Text = cfg.Can.Signal.Data;
                _nudCanMs.Value  = Math.Clamp(cfg.Can.Signal.Ms, 1, (int)_nudCanMs.Maximum);
                _txtCanMod.Text  = cfg.Can.Signal.Modifier.Replace("\n", "\r\n");

                _canGrid.Rows.Clear();
                foreach (var row in cfg.Can.MultiSignals)
                    CanAddMultiRow(row.Type, row.Id, row.Dlc, row.Data, row.Ms, row.Modifier);
                for (int r = 0; r < _canGrid.Rows.Count && r < cfg.Can.MultiSignals.Count; r++)
                    _canGrid.Rows[r].Cells["COn"].Value = cfg.Can.MultiSignals[r].Enabled;

                _txtCanBfStart.Text = cfg.Can.Brute.Start;
                _txtCanBfEnd.Text = cfg.Can.Brute.End;
                _txtCanBfStep.Text = cfg.Can.Brute.Step;
                _nudCanBfDelay.Value = Math.Clamp(cfg.Can.Brute.DelayMs, (int)_nudCanBfDelay.Minimum, (int)_nudCanBfDelay.Maximum);
                _nudCanBfRxTo.Value = Math.Clamp(cfg.Can.Brute.RxTimeoutMs, (int)_nudCanBfRxTo.Minimum, (int)_nudCanBfRxTo.Maximum);
                _cmbCanBfType.SelectedIndex = Math.Clamp(cfg.Can.Brute.TypeIndex, 0, Math.Max(0, _cmbCanBfType.Items.Count - 1));
                _nudCanBfDlc.Value = Math.Clamp(cfg.Can.Brute.Dlc, (int)_nudCanBfDlc.Minimum, (int)_nudCanBfDlc.Maximum);
                _txtCanBfData.Text = cfg.Can.Brute.Data;
                _chkCanBfConstant.Checked = cfg.Can.Brute.ConstantEnabled;
                _txtCanBfConstId.Text = cfg.Can.Brute.ConstId;
                _txtCanBfConstData.Text = cfg.Can.Brute.ConstData;
                _nudCanBfConstMs.Value = Math.Clamp(cfg.Can.Brute.ConstMs, (int)_nudCanBfConstMs.Minimum, (int)_nudCanBfConstMs.Maximum);
                _chkCanBfReplayLoop.Checked = cfg.Can.Brute.ReplayLoop;
                _grdCanBf.Rows.Clear();
                foreach (var row in cfg.Can.Brute.Results)
                {
                    int r = _grdCanBf.Rows.Add();
                    _grdCanBf.Rows[r].Cells["CBfType"].Value = row.Type;
                    _grdCanBf.Rows[r].Cells["CBfId"].Value = row.Id;
                    _grdCanBf.Rows[r].Cells["CBfDlc"].Value = row.Dlc;
                    _grdCanBf.Rows[r].Cells["CBfPay"].Value = row.Payload;
                    _grdCanBf.Rows[r].Cells["CBfAck"].Value = row.Ack;
                    _grdCanBf.Rows[r].Cells["CBfResp"].Value = row.RespData;
                    if (string.Equals(row.Ack, "YES", StringComparison.OrdinalIgnoreCase))
                        _grdCanBf.Rows[r].DefaultCellStyle.ForeColor = Color.LimeGreen;
                }

                AddLog($"Config loaded: {dlg.FileName}", LogColor.Info);
            }
            catch (Exception ex) { AddLog($"Load failed: {ex.Message}", LogColor.Error); }
        }

        void SaveConfig()
        {
            using var dlg = new SaveFileDialog { Filter = "App Config (*.json)|*.json|All files|*.*", Title = "Save Config", DefaultExt = "json" };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            try
            {
                var cfg = new AppConfig
                {
                    LinBaud = int.TryParse(_txtBaud.Text, out int b) ? b : 19200,
                    Signal  = new SignalCfg
                    {
                        Id       = _txtId.Text.Trim(),
                        Data     = string.Join(" ", _txtD.Select(t => t.Text.PadLeft(2, '0'))),
                        Len      = (int)_nudLen.Value,
                        Enhanced = _rbV2.Checked,
                        Ms       = (int)_nudMs.Value,
                        Modifier = _txtMod.Text.Replace("\r\n", "\n"),
                    },
                    MultiSignals = Enumerable.Range(0, _grid.Rows.Count)
                        .Where(r => !_grid.Rows[r].IsNewRow)
                        .Select(r => new MultiSignalCfg
                        {
                            Enabled  = _grid.Rows[r].Cells["On"].Value is true,
                            Id       = _grid.Rows[r].Cells["Id"].Value?.ToString()   ?? "00",
                            Data     = _grid.Rows[r].Cells["Data"].Value?.ToString() ?? "",
                            Len      = int.TryParse(_grid.Rows[r].Cells["Len"].Value?.ToString(), out int l) ? l : 8,
                            Enhanced = (_grid.Rows[r].Cells["CS"].Value?.ToString() ?? "V2") == "V2",
                            Ms       = int.TryParse(_grid.Rows[r].Cells["Ms"].Value?.ToString(), out int ms) ? ms : 100,
                            Modifier = _grid.Rows[r].Cells["Mod"].Value?.ToString() ?? "",
                        }).ToList(),
                    LinBrute = new LinBruteCfg
                    {
                        Start            = _txtBfStart.Text.Trim(),
                        End              = _txtBfEnd.Text.Trim(),
                        Step             = _txtBfStep.Text.Trim(),
                        DelayMs          = (int)_nudBfDelay.Value,
                        RxTimeoutMs      = (int)_nudBfRxTimeout.Value,
                        Dlc              = (int)_nudBfDlc.Value,
                        Enhanced         = _rbBfV2.Checked,
                        ConstantEnabled  = _chkBfConstant.Checked,
                        ConstId          = _txtBfConstId.Text.Trim(),
                        ConstData        = _txtBfConstData.Text.Trim(),
                        ConstMs          = (int)_nudBfConstMs.Value,
                        BroadcastEnabled = _chkBfBroadcast.Checked,
                        BroadcastData    = _txtBfBcastData.Text.Trim(),
                        ReplayLoop       = _chkBfReplayLoop.Checked,
                        Results          = Enumerable.Range(0, _grdBf.Rows.Count)
                            .Where(r => !_grdBf.Rows[r].IsNewRow)
                            .Select(r => new LinBruteResultCfg
                            {
                                Id       = _grdBf.Rows[r].Cells["BfId"].Value?.ToString() ?? "00",
                                Pid      = _grdBf.Rows[r].Cells["BfPid"].Value?.ToString() ?? "00",
                                Payload  = _grdBf.Rows[r].Cells["BfPayload"].Value?.ToString() ?? "",
                                Response = _grdBf.Rows[r].Cells["BfResp"].Value?.ToString() ?? "-",
                                RespData = _grdBf.Rows[r].Cells["BfRespData"].Value?.ToString() ?? "",
                            }).ToList(),
                    },
                    Can = new CanConfigCfg
                    {
                        NomRateIndex   = _cmbNomRate.SelectedIndex >= 0 ? _cmbNomRate.SelectedIndex : SLCAN.DefaultNomIdx,
                        FdRateIndex    = _cmbFdRate.SelectedIndex >= 0 ? _cmbFdRate.SelectedIndex : SLCAN.DefaultFdIdx,
                        Silent         = _chkCanSilent.Checked,
                        AutoRetransmit = _chkCanAutoRetx.Checked,
                        Signal         = new CanSignalCfg
                        {
                            FrameTypeIndex = _cmbCanFrameType.SelectedIndex >= 0 ? _cmbCanFrameType.SelectedIndex : 0,
                            Id             = _txtCanId.Text.Trim(),
                            Dlc            = _cmbCanDlc.SelectedItem?.ToString() ?? "8",
                            Data           = _txtCanData.Text.Trim(),
                            Ms             = (int)_nudCanMs.Value,
                            Modifier       = _txtCanMod.Text.Replace("\r\n", "\n"),
                        },
                        MultiSignals = Enumerable.Range(0, _canGrid.Rows.Count)
                            .Where(r => !_canGrid.Rows[r].IsNewRow)
                            .Select(r => new CanMultiSignalCfg
                            {
                                Enabled  = _canGrid.Rows[r].Cells["COn"].Value is true,
                                Type     = _canGrid.Rows[r].Cells["CType"].Value?.ToString() ?? "t",
                                Id       = _canGrid.Rows[r].Cells["CId"].Value?.ToString() ?? "000",
                                Dlc      = _canGrid.Rows[r].Cells["CDlc"].Value?.ToString() ?? "8",
                                Data     = _canGrid.Rows[r].Cells["CData"].Value?.ToString() ?? "",
                                Ms       = int.TryParse(_canGrid.Rows[r].Cells["CMs"].Value?.ToString(), out int ms) ? ms : 100,
                                Modifier = _canGrid.Rows[r].Cells["CMod"].Value?.ToString() ?? "",
                            }).ToList(),
                        Brute = new CanBruteCfg
                        {
                            Start           = _txtCanBfStart.Text.Trim(),
                            End             = _txtCanBfEnd.Text.Trim(),
                            Step            = _txtCanBfStep.Text.Trim(),
                            DelayMs         = (int)_nudCanBfDelay.Value,
                            RxTimeoutMs     = (int)_nudCanBfRxTo.Value,
                            TypeIndex       = _cmbCanBfType.SelectedIndex >= 0 ? _cmbCanBfType.SelectedIndex : 0,
                            Dlc             = (int)_nudCanBfDlc.Value,
                            Data            = _txtCanBfData.Text.Trim(),
                            ConstantEnabled = _chkCanBfConstant.Checked,
                            ConstId         = _txtCanBfConstId.Text.Trim(),
                            ConstData       = _txtCanBfConstData.Text.Trim(),
                            ConstMs         = (int)_nudCanBfConstMs.Value,
                            ReplayLoop      = _chkCanBfReplayLoop.Checked,
                            Results         = Enumerable.Range(0, _grdCanBf.Rows.Count)
                                .Where(r => !_grdCanBf.Rows[r].IsNewRow)
                                .Select(r => new CanBruteResultCfg
                                {
                                    Type     = _grdCanBf.Rows[r].Cells["CBfType"].Value?.ToString() ?? "t",
                                    Id       = _grdCanBf.Rows[r].Cells["CBfId"].Value?.ToString() ?? "000",
                                    Dlc      = _grdCanBf.Rows[r].Cells["CBfDlc"].Value?.ToString() ?? "8",
                                    Payload  = _grdCanBf.Rows[r].Cells["CBfPay"].Value?.ToString() ?? "",
                                    Ack      = _grdCanBf.Rows[r].Cells["CBfAck"].Value?.ToString() ?? "-",
                                    RespData = _grdCanBf.Rows[r].Cells["CBfResp"].Value?.ToString() ?? "",
                                }).ToList(),
                        },
                    },
                };
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(cfg, opts));
                AddLog($"Config saved: {dlg.FileName}", LogColor.Info);
            }
            catch (Exception ex) { AddLog($"Save failed: {ex.Message}", LogColor.Error); }
        }

        // ── Parse helpers ─────────────────────────────────────────────────────
        byte ParseId()
        {
            string s = _txtId.Text.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            return (byte)(Convert.ToByte(s, 16) & 0x3F);
        }

        static byte ParseHexByte(string? s, byte fallback = 0)
        {
            if (s == null) return fallback;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            return s.Length > 0 && s.All(c => "0123456789ABCDEFabcdef".Contains(c))
                ? Convert.ToByte(s.Length > 2 ? s[^2..] : s, 16)
                : fallback;
        }

        static int ParseHexInt(string? s, int fallback = 0)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int value) ? value : fallback;
        }

        void UpdatePidLabel()
        {
            try { _lblPid.Text = $"  PID={LIN.CalcParity(ParseId()):X2}"; }
            catch { _lblPid.Text = "  PID=??"; }
        }

        void OnLinSingleDataKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (_port?.IsOpen == true) DoSend();
        }

        void OnCanSingleDataKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (_canPort?.IsOpen == true) CanSendTxFrame();
        }

        void OnLinMultiEditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
        {
            if (e.Control is not TextBox tb) return;
            tb.KeyDown -= OnLinMultiDataEditorKeyDown;
            if (_grid.CurrentCell != null && _grid.Columns[_grid.CurrentCell.ColumnIndex].Name == "Data")
                tb.KeyDown += OnLinMultiDataEditorKeyDown;
        }

        void OnCanMultiEditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
        {
            if (e.Control is not TextBox tb) return;
            tb.KeyDown -= OnCanMultiDataEditorKeyDown;
            if (_canGrid.CurrentCell != null && _canGrid.Columns[_canGrid.CurrentCell.ColumnIndex].Name == "CData")
                tb.KeyDown += OnCanMultiDataEditorKeyDown;
        }

        void OnLinMultiDataEditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (_grid.CurrentCell == null) return;
            int rowIndex = _grid.CurrentCell.RowIndex;
            _grid.EndEdit();
            SendLinMultiRowOnce(rowIndex);
        }

        void OnCanMultiDataEditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (_canGrid.CurrentCell == null) return;
            int rowIndex = _canGrid.CurrentCell.RowIndex;
            _canGrid.EndEdit();
            SendCanMultiRowOnce(rowIndex);
        }

        static string ByteHex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));

        static void TryScrollToLastRow(DataGridView grid)
        {
            if (grid.Rows.Count == 0 || !grid.IsHandleCreated) return;
            if (grid.DisplayedRowCount(false) == 0) return;
            try { grid.FirstDisplayedScrollingRowIndex = grid.Rows.Count - 1; }
            catch (InvalidOperationException) { }
            catch (ArgumentOutOfRangeException) { }
        }

        // ── Log ───────────────────────────────────────────────────────────────
        enum LogColor { TX, Info, Warn, Error }

        void AddLog(string msg, LogColor lc)
        {
            if (InvokeRequired) { Invoke(() => AddLog(msg, lc)); return; }
            Color c = lc switch
            {
                LogColor.TX    => Color.LimeGreen,
                LogColor.Info  => Color.Cyan,
                LogColor.Warn  => Color.Yellow,
                LogColor.Error => Color.OrangeRed,
                _              => Color.White,
            };
            _log.SelectionStart  = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor  = c;
            _log.AppendText(msg + "\n");
            if (_chkScroll.Checked) _log.ScrollToCaret();
        }

        // ── UI factory helpers ────────────────────────────────────────────────
        static Label           L(string t)  => new() { Text = t, AutoSize = true, Padding = new Padding(4, 5, 0, 0) };
        static Button          Btn(string t, int w = 80) => new() { Text = t, Width = w, Height = 26, Margin = new Padding(2) };
        static GroupBox        Grp(string t) => new() { Text = t, Dock = DockStyle.Fill, Padding = new Padding(6, 14, 6, 4), AutoSize = true };
        static FlowLayoutPanel Flow()        => new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0), WrapContents = false };

        FlowLayoutPanel DataRow(int start)
        {
            var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 2, 0, 2) };
            for (int i = start; i < start + 4; i++)
            {
                p.Controls.Add(new Label { Text = $"D{i}:", AutoSize = true, Padding = new Padding(4, 4, 2, 0) });
                p.Controls.Add(_txtD[i]);
            }
            return p;
        }

        // ══════════════════════════════════════════════════════════════════════
        // SLCAN TAB
        // ══════════════════════════════════════════════════════════════════════

        static byte[] CanBuildFrameFromRow(CanSigRow sig, byte[] data)
        {
            bool remote = sig.TypeIdx == 2 || sig.TypeIdx == 3;
            bool fd     = sig.TypeIdx >= 4;
            bool brs    = sig.TypeIdx == 6 || sig.TypeIdx == 7;
            bool ext    = sig.TypeIdx == 1 || sig.TypeIdx == 3 || sig.TypeIdx == 5 || sig.TypeIdx == 7;
            if (remote) return ext ? SLCAN.RemoteExt(sig.Id, sig.DlcOrLen) : SLCAN.RemoteStd(sig.Id, sig.DlcOrLen);
            if (fd)     return ext ? SLCAN.SendFdExt(sig.Id, data, sig.DlcOrLen, brs) : SLCAN.SendFdStd(sig.Id, data, sig.DlcOrLen, brs);
            return ext ? SLCAN.SendExt(sig.Id, data, Math.Min(sig.DlcOrLen,8)) : SLCAN.SendStd(sig.Id, data, Math.Min(sig.DlcOrLen,8));
        }

        static byte[] BuildCanBruteFrame(int id, byte[] data, int dlc, int typeIdx)
        {
            bool fd  = typeIdx >= 2;
            bool ext = typeIdx == 1 || typeIdx == 3 || typeIdx == 5;
            bool brs = typeIdx == 4 || typeIdx == 5;
            if (fd) return ext ? SLCAN.SendFdExt(id, data, dlc, brs) : SLCAN.SendFdStd(id, data, dlc, brs);
            return ext ? SLCAN.SendExt(id, data, Math.Min(dlc, 8)) : SLCAN.SendStd(id, data, Math.Min(dlc, 8));
        }

        static string CanBruteTypeCode(int typeIdx) => typeIdx switch
        {
            1 => "T",
            2 => "d",
            3 => "D",
            4 => "b",
            5 => "B",
            _ => "t",
        };

        static int CanBruteTypeIndexFromCode(string code) => code switch
        {
            "T" => 1,
            "d" => 2,
            "D" => 3,
            "b" => 4,
            "B" => 5,
            _   => 0,
        };

        static int CanSignalTypeIndexFromCode(string code) => code switch
        {
            "T" => 1,
            "r" => 2,
            "R" => 3,
            "d" => 4,
            "D" => 5,
            "b" => 6,
            "B" => 7,
            _   => 0,
        };

        void SetCanSignalDlc(int dlc)
        {
            string wanted = dlc.ToString();
            for (int i = 0; i < _cmbCanDlc.Items.Count; i++)
                if ((_cmbCanDlc.Items[i]?.ToString() ?? "") == wanted) { _cmbCanDlc.SelectedIndex = i; return; }
        }

        static string CanDisplayTypeFromIndex(int typeIdx) => typeIdx switch
        {
            2 or 3 => "Remote",
            4 or 5 => "FD",
            6 or 7 => "FD+BRS",
            _      => "Data",
        };

        static string CanDisplayTypeFromBruteIndex(int typeIdx) => typeIdx switch
        {
            2 or 3 => "FD",
            4 or 5 => "FD+BRS",
            _      => "Data",
        };

        static string CanFormatId(int id, bool ext) => ext ? id.ToString("X8") : id.ToString("X3");
        static string CanFormatData(byte[] data, bool fd, int dlcOrLen)
        {
            int byteLen = fd ? SLCAN.FdDlcToBytes(SLCAN.BytesToFdDlc(dlcOrLen)) : Math.Min(dlcOrLen, 8);
            return string.Join(" ", data.Take(byteLen).Select(b => b.ToString("X2")));
        }

        Panel BuildSlcanTab()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };

            // ── Top bar: connection + CAN config ──────────────────────────────
            var topGrp  = new GroupBox { Text = "Connection & Config", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6,14,6,4) };
            var topFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            _btnCanConn.Click  += CanConnect;
            _btnCanDisc.Click  += CanDisconnect;
            _btnCanOpen.Click  += CanOpen;
            _btnCanClose.Click += CanClose;
            _btnCanVer.Click   += (_, _) => CanSendRaw(SLCAN.GetVersion());
            _btnCanDisc.Enabled = false; _btnCanOpen.Enabled = false; _btnCanClose.Enabled = false;
            _cmbNomRate.Items.AddRange(SLCAN.NomRateLabels); _cmbNomRate.SelectedIndex = SLCAN.DefaultNomIdx;
            _cmbFdRate.Items.AddRange(SLCAN.FdRateLabels);   _cmbFdRate.SelectedIndex  = SLCAN.DefaultFdIdx;
            _cmbCanPort.DropDown += (_, _) => { var cur = _cmbCanPort.Text; _cmbCanPort.Items.Clear(); _cmbCanPort.Items.AddRange(SerialPort.GetPortNames()); if (_cmbCanPort.Items.Contains(cur)) _cmbCanPort.Text = cur; };
            topFlow.Controls.AddRange(new Control[] {
                L("Port:"), _cmbCanPort, _btnCanConn, _btnCanDisc,
                L("  Nom:"), _cmbNomRate, L("FD:"), _cmbFdRate, _chkCanSilent, _chkCanAutoRetx,
                new Label { Width = 8 },
                _btnCanOpen, _btnCanClose, _btnCanVer, _lblCanStatus,
            });
            topGrp.Controls.Add(topFlow);

            // ── Inner TabControl ──────────────────────────────────────────────
            var inner = new TabControl { Dock = DockStyle.Fill };
            var tpSig  = new TabPage("Signal");      tpSig.Controls.Add(BuildCanSignalTab());  inner.TabPages.Add(tpSig);
            var tpMul  = new TabPage("Multi-Signal");tpMul.Controls.Add(BuildCanMultiTab());   inner.TabPages.Add(tpMul);
            var tpBf   = new TabPage("Brute Force"); tpBf.Controls.Add(BuildCanBruteTab());    inner.TabPages.Add(tpBf);
            var tpLog  = new TabPage("Log");         tpLog.Controls.Add(BuildCanLogTab());     inner.TabPages.Add(tpLog);

            // Dock order
            panel.Controls.Add(inner);
            panel.Controls.Add(topGrp);

            _cmbCanPort.Items.AddRange(SerialPort.GetPortNames());
            if (_cmbCanPort.Items.Count > 0) _cmbCanPort.SelectedIndex = 0;
            CanUpdateStats();
            return panel;
        }

        // ── Signal sub-tab ────────────────────────────────────────────────────
        Panel BuildCanSignalTab()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };

            // Frame config groupbox
            var fGrp  = new GroupBox { Text = "Frame", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6,14,6,6) };
            var fFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = true };

            _cmbCanFrameType.Items.AddRange(new object[] {
                "t  Standard Data (11-bit)",  "T  Extended Data (29-bit)",
                "r  Standard Remote (11-bit)", "R  Extended Remote (29-bit)",
                "d  CANFD Standard (no BRS)",  "D  CANFD Extended (no BRS)",
                "b  CANFD Standard + BRS",     "B  CANFD Extended + BRS",
            });
            _cmbCanFrameType.SelectedIndex = 0;
            _cmbCanFrameType.SelectedIndexChanged += (_, _) => CanUpdateDlcCombo();
            CanUpdateDlcCombo();

            fFlow.Controls.AddRange(new Control[] {
                L("Type:"), _cmbCanFrameType,
                L("  ID:"), _txtCanId,
                L("  DLC:"), _cmbCanDlc,
                L("  Data:"), _txtCanData,
            });
            fGrp.Controls.Add(fFlow);

            // Send buttons bar
            var genGrp  = new GroupBox { Text = "Generate", Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(6,14,6,4) };
            var genFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Padding = new Padding(0,0,0,0) };
            _btnCanSend.Click += (_, _) => CanSendTxFrame();
            _btnCanLoop.Click += (_, _) => CanStartLoop();
            _btnCanStop.Click += (_, _) => CanStopLoop();
            _btnCanStop.Enabled = false;
            genFlow.Controls.AddRange(new Control[] {
                _btnCanSend, _btnCanLoop, _btnCanStop,
                L("  Interval:"), _nudCanMs, L("ms"),
            });
            genGrp.Controls.Add(genFlow);

            // Modifiers groupbox (fill)
            var mGrp = new GroupBox { Text = "Modifiers  (D0=D0+1 | D[0..7]=D[0]^0xFF | D5=D3&0x0F)", Dock = DockStyle.Fill, Padding = new Padding(6,14,6,6) };
            mGrp.Controls.Add(_txtCanMod);

            // Dock order
            panel.Controls.Add(mGrp);
            panel.Controls.Add(genGrp);
            panel.Controls.Add(fGrp);
            return panel;
        }

        // ── Multi-signal sub-tab ──────────────────────────────────────────────
        Panel BuildCanMultiTab()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };

            // Grid
            _canGrid.Dock                   = DockStyle.Fill;
            _canGrid.AllowUserToAddRows     = false;
            _canGrid.AllowUserToDeleteRows  = false;
            _canGrid.RowHeadersVisible      = false;
            _canGrid.SelectionMode          = DataGridViewSelectionMode.FullRowSelect;
            _canGrid.BackgroundColor        = Color.FromArgb(28,28,28);
            _canGrid.DefaultCellStyle.BackColor = Color.FromArgb(28,28,28);
            _canGrid.DefaultCellStyle.ForeColor = Color.White;
            _canGrid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(36,36,36);
            _canGrid.ColumnHeadersDefaultCellStyle.BackColor   = Color.FromArgb(45,45,48);
            _canGrid.ColumnHeadersDefaultCellStyle.ForeColor   = Color.White;
            _canGrid.GridColor   = Color.FromArgb(60,60,60);
            _canGrid.BorderStyle = BorderStyle.None;
            _canGrid.EditMode    = DataGridViewEditMode.EditOnEnter;

            var colOn   = new DataGridViewCheckBoxColumn { HeaderText = "On",   Width = 32,  Name = "COn" };
            var colType = new DataGridViewComboBoxColumn { HeaderText = "Type", Width = 60,  Name = "CType", FlatStyle = FlatStyle.Flat };
            colType.Items.AddRange("t","T","d","D","b","B");
            var colId   = new DataGridViewTextBoxColumn  { HeaderText = "ID",   Width = 60,  Name = "CId" };
            var colDlc  = new DataGridViewTextBoxColumn  { HeaderText = "DLC",  Width = 44,  Name = "CDlc" };
            var colData = new DataGridViewTextBoxColumn  { HeaderText = "Data (hex)", Width = 220, Name = "CData",
                                                           AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill };
            var colMs   = new DataGridViewTextBoxColumn  { HeaderText = "ms",   Width = 52,  Name = "CMs" };
            var colMod  = new DataGridViewTextBoxColumn  { HeaderText = "Modifier", Width = 130, Name = "CMod" };
            var colSent = new DataGridViewTextBoxColumn  { HeaderText = "Sent", Width = 60,  Name = "CSent", ReadOnly = true };
            _canGrid.Columns.AddRange(colOn, colType, colId, colDlc, colData, colMs, colMod, colSent);

            // Live edit
            _canGrid.CellValueChanged += OnCanGridCellChanged;
            _canGrid.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (_canGrid.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
                    _canGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _canGrid.EditingControlShowing += OnCanMultiEditingControlShowing;
            _canGrid.CellEndEdit += OnCanMultiCellEndEdit;

            // Toolbar
            var bar = new Panel { Dock = DockStyle.Top, Height = 28 };
            var bFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Padding = new Padding(2,2,0,0) };
            _btnCanMAdd.Click   += (_, _) => CanAddMultiRow();
            _btnCanMDel.Click   += (_, _) => CanRemoveMultiRow();
            _btnCanMStart.Click += CanMultiStart;
            _btnCanMStop.Click  += CanMultiStop;
            _btnCanMStop.Enabled = false;
            bFlow.Controls.AddRange(new Control[] { _btnCanMAdd, _btnCanMDel, _btnCanMStart, _btnCanMStop, _lblCanMStatus });
            bar.Controls.Add(bFlow);

            // Seed rows
            CanAddMultiRow("t", "123", "8", "DE AD BE EF 00 00 00 00", 100, "D0=D0+1");
            CanAddMultiRow("T", "1FFFFFFF", "8", "FF FF FF FF FF FF FF FF", 200, "");

            panel.Controls.Add(_canGrid);
            panel.Controls.Add(bar);
            return panel;
        }

        // ── Brute force sub-tab ───────────────────────────────────────────────
        Panel BuildCanBruteTab()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };

            _cmbCanBfType.Items.AddRange(new object[] {
                "t  Standard (11-bit)", "T  Extended (29-bit)",
                "d  CANFD Std (no BRS)", "D  CANFD Ext (no BRS)",
                "b  CANFD Std + BRS",    "B  CANFD Ext + BRS",
            });
            _cmbCanBfType.SelectedIndex = 0;

            var cfgGrp  = new GroupBox { Text = "Config", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6,14,6,4) };
            var cfgStack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };

            var row1 = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            row1.Controls.AddRange(new Control[] {
                L("ID from:"), _txtCanBfStart, L("to:"), _txtCanBfEnd,
                L("  Step:"), _txtCanBfStep, L("(hex)"),
                L("  Delay:"), _nudCanBfDelay, L("ms"),
                L("  RX Timeout:"), _nudCanBfRxTo, L("ms"),
                L("  DLC:"), _nudCanBfDlc,
                L("  Type:"), _cmbCanBfType,
            });
            var row2 = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            row2.Controls.AddRange(new Control[] {
                L("Payload:"), _txtCanBfData,
                L("  (HEX or 'INC')"),
            });
            var row3 = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            row3.Controls.AddRange(new Control[] {
                _chkCanBfConstant, L("ID:"), _txtCanBfConstId, L("Data:"), _txtCanBfConstData, L("every"), _nudCanBfConstMs, L("ms"),
                L("  From signal:"), _cmbCanBfSig, _btnCanBfLoadSig,
            });
            var row4 = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            row4.Controls.AddRange(new Control[] { _btnCanBfStart, _btnCanBfStop, _btnCanBfExport });

            _btnCanBfStart.Click  += CanBruteStart;
            _btnCanBfStop.Click   += CanBruteStop;
            _btnCanBfExport.Click += CanBruteExport;
            _btnCanBfStop.Enabled  = false;
            _btnCanBfReplay.Click += CanBruteReplay;
            _btnCanBfLoadSig.Click += (_, _) => LoadCanSigIntoBfConst();
            _cmbCanBfSig.DropDown += (_, _) => RefreshCanSigCombo();

            cfgStack.Controls.Add(row1);
            cfgStack.Controls.Add(row2);
            cfgStack.Controls.Add(row3);
            cfgStack.Controls.Add(row4);
            cfgGrp.Controls.Add(cfgStack);

            var prgPanel  = new Panel { Dock = DockStyle.Top, Height = 22, Padding = new Padding(4,2,4,2) };
            prgPanel.Controls.Add(_pgsCanBf);
            var statPanel = new Panel { Dock = DockStyle.Top, Height = 22 };
            statPanel.Controls.Add(_lblCanBfStatus);
            var replayPanel = new Panel { Dock = DockStyle.Top, Height = 28 };
            var replayFlow  = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Padding = new Padding(2, 2, 0, 0) };
            replayFlow.Controls.AddRange(new Control[] { _btnCanBfReplay, _chkCanBfReplayLoop });
            replayPanel.Controls.Add(replayFlow);

            // Results grid
            _grdCanBf.Dock                   = DockStyle.Fill;
            _grdCanBf.AllowUserToAddRows     = false;
            _grdCanBf.AllowUserToDeleteRows  = false;
            _grdCanBf.ReadOnly               = true;
            _grdCanBf.RowHeadersVisible      = false;
            _grdCanBf.SelectionMode          = DataGridViewSelectionMode.FullRowSelect;
            _grdCanBf.BackgroundColor        = Color.FromArgb(28,28,28);
            _grdCanBf.DefaultCellStyle.BackColor = Color.FromArgb(28,28,28);
            _grdCanBf.DefaultCellStyle.ForeColor = Color.White;
            _grdCanBf.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(36,36,36);
            _grdCanBf.ColumnHeadersDefaultCellStyle.BackColor   = Color.FromArgb(45,45,48);
            _grdCanBf.ColumnHeadersDefaultCellStyle.ForeColor   = Color.White;
            _grdCanBf.GridColor   = Color.FromArgb(60,60,60);
            _grdCanBf.BorderStyle = BorderStyle.None;
            _grdCanBf.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type",    Name = "CBfType", Width = 48 });
            _grdCanBf.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID",      Name = "CBfId",   Width = 72 });
            _grdCanBf.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "DLC",     Name = "CBfDlc",  Width = 44 });
            _grdCanBf.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Payload", Name = "CBfPay",  Width = 200, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grdCanBf.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ACK",     Name = "CBfAck",  Width = 48 });
            _grdCanBf.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Resp Data",Name = "CBfResp",Width = 180 });

            // Right-click on brute results
            var bfCtx = new ContextMenuStrip();
            var ctxReplay = bfCtx.Items.Add("Replay this frame");
            ctxReplay.Click += (_, _) => CanBruteReplaySingle();
            var ctxBfLoad = bfCtx.Items.Add("Load into Signal TX");
            ctxBfLoad.Click += (_, _) =>
            {
                if (_grdCanBf.CurrentRow?.IsNewRow != false) return;
                _cmbCanFrameType.SelectedIndex = CanSignalTypeIndexFromCode(_grdCanBf.CurrentRow.Cells["CBfType"].Value?.ToString() ?? "t");
                if (int.TryParse(_grdCanBf.CurrentRow.Cells["CBfDlc"].Value?.ToString(), out int dlc))
                    SetCanSignalDlc(dlc);
                _txtCanId.Text   = _grdCanBf.CurrentRow.Cells["CBfId"].Value?.ToString() ?? "000";
                _txtCanData.Text = _grdCanBf.CurrentRow.Cells["CBfPay"].Value?.ToString() ?? "";
            };
            var ctxConst = bfCtx.Items.Add("Set as Constant Signal");
            ctxConst.Click += (_, _) =>
            {
                if (_grdCanBf.CurrentRow?.IsNewRow != false) return;
                _txtCanBfConstId.Text     = _grdCanBf.CurrentRow.Cells["CBfId"].Value?.ToString() ?? "000";
                _txtCanBfConstData.Text   = _grdCanBf.CurrentRow.Cells["CBfPay"].Value?.ToString() ?? "";
                _chkCanBfConstant.Checked = true;
            };
            bfCtx.Opening += (_, _) =>
            {
                bool hasRow = _grdCanBf.CurrentRow?.IsNewRow == false;
                ctxReplay.Enabled = hasRow;
                ctxBfLoad.Enabled = hasRow;
                ctxConst.Enabled  = hasRow;
            };
            _grdCanBf.ContextMenuStrip = bfCtx;

            panel.Controls.Add(_grdCanBf);
            panel.Controls.Add(replayPanel);
            panel.Controls.Add(statPanel);
            panel.Controls.Add(prgPanel);
            panel.Controls.Add(cfgGrp);
            return panel;
        }

        // ── Log sub-tab ───────────────────────────────────────────────────────
        Panel BuildCanLogTab()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };

            // Stats + controls bar
            var bar   = new Panel { Dock = DockStyle.Top, Height = 28 };
            var bFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Padding = new Padding(2,2,0,0) };
            var btnClear = Btn("Clear", 52);
            btnClear.Click += (_, _) => { _grdCan.Rows.Clear(); _canRxCount = 0; _canTxCount = 0; _canIdRowMap.Clear(); CanUpdateStats(); };
            bFlow.Controls.AddRange(new Control[] { _lblCanStats, btnClear, _chkCanGroupById });
            bar.Controls.Add(bFlow);

            // Log grid
            _grdCan.Dock                   = DockStyle.Fill;
            _grdCan.AllowUserToAddRows     = false;
            _grdCan.AllowUserToDeleteRows  = false;
            _grdCan.ReadOnly               = true;
            _grdCan.RowHeadersVisible      = false;
            _grdCan.SelectionMode          = DataGridViewSelectionMode.FullRowSelect;
            _grdCan.BackgroundColor        = Color.FromArgb(28,28,28);
            _grdCan.DefaultCellStyle.BackColor = Color.FromArgb(28,28,28);
            _grdCan.DefaultCellStyle.ForeColor = Color.White;
            _grdCan.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(36,36,36);
            _grdCan.ColumnHeadersDefaultCellStyle.BackColor   = Color.FromArgb(45,45,48);
            _grdCan.ColumnHeadersDefaultCellStyle.ForeColor   = Color.White;
            _grdCan.GridColor   = Color.FromArgb(60,60,60);
            _grdCan.BorderStyle = BorderStyle.None;

            _grdCan.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "#",     Name = "CnSeq",  Width = 52 });
            _grdCan.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Time",  Name = "CnTime", Width = 86 });
            _grdCan.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Dir",   Name = "CnDir",  Width = 36 });
            _grdCan.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type",  Name = "CnType", Width = 68 });
            _grdCan.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID",    Name = "CnId",   Width = 78 });
            _grdCan.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "DLC",   Name = "CnDlc",  Width = 38 });
            _grdCan.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Count", Name = "CnCnt",  Width = 52 });
            _grdCan.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Data",  Name = "CnData", Width = 200, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

            var canCtx = new ContextMenuStrip();
            var ctxReSend = canCtx.Items.Add("↺ Re-send this frame");
            ctxReSend.Click  += (_, _) => CanReplaySelected();
            var ctxLoadTx = canCtx.Items.Add("Load into Signal TX");
            ctxLoadTx.Click  += (_, _) => CanLoadRowIntoTx();
            canCtx.Opening   += (_, _) => { bool ok = _grdCan.CurrentRow?.IsNewRow == false; ctxReSend.Enabled = ok; ctxLoadTx.Enabled = ok; };
            _grdCan.ContextMenuStrip = canCtx;

            panel.Controls.Add(_grdCan);
            panel.Controls.Add(bar);
            return panel;
        }

        // ── SLCAN helpers ─────────────────────────────────────────────────────
        void CanUpdateDlcCombo()
        {
            bool fd = _cmbCanFrameType.SelectedIndex >= 4;
            _cmbCanDlc.Items.Clear();
            if (fd) { foreach (var e in new[]{"0","1","2","3","4","5","6","7","8","12","16","20","24","32","48","64"}) _cmbCanDlc.Items.Add(e); }
            else    { for (int i = 0; i <= 8; i++) _cmbCanDlc.Items.Add(i.ToString()); }
            _cmbCanDlc.SelectedIndex = Math.Min(_cmbCanDlc.Items.Count - 1, 8);
        }

        void CanUpdateStats()
        {
            if (InvokeRequired) { BeginInvoke(CanUpdateStats); return; }
            _lblCanStats.Text = $"RX: {_canRxCount}   TX: {_canTxCount}    ";
        }

        void CanSendRaw(byte[] cmd)
        {
            lock (_canLock) { if (_canPort?.IsOpen == true) _canPort.Write(cmd, 0, cmd.Length); }
        }

        // ── Connect / Disconnect ──────────────────────────────────────────────
        void CanConnect(object? s, EventArgs e)
        {
            if (_cmbCanPort.Text.Length == 0) { AddLog("Select a CAN port.", LogColor.Error); return; }
            try
            {
                var p = new SerialPort(_cmbCanPort.Text, 115200, Parity.None, 8, StopBits.One)
                    { ReadTimeout = 100, WriteTimeout = 500 };
                p.Open();
                lock (_canLock) { _canPort = p; }
                _btnCanConn.Enabled = false; _btnCanDisc.Enabled = true;
                _btnCanOpen.Enabled = true;  _btnCanClose.Enabled = true;
                _lblCanStatus.Text = $"● {_cmbCanPort.Text}"; _lblCanStatus.ForeColor = Color.LimeGreen;
                _canRxRun = true;
                _canRxThread = new Thread(CanRxLoop) { IsBackground = true, Name = "CanRx" };
                _canRxThread.Start();
                AddLog($"CAN port {_cmbCanPort.Text} connected.", LogColor.Info);
                CanOpen(null, EventArgs.Empty);
            }
            catch (Exception ex) { AddLog($"CAN connect: {ex.Message}", LogColor.Error); }
        }

        void CanDisconnect(object? s, EventArgs e)
        {
            CanStopLoop(); CanMultiStop(null, EventArgs.Empty); CanBruteStop(null, EventArgs.Empty);
            try { CanSendRaw(SLCAN.Close()); Thread.Sleep(40); } catch { }
            _canRxRun = false;
            lock (_canLock) { try { _canPort?.Close(); } catch { } _canPort = null; }
            _btnCanConn.Enabled = true; _btnCanDisc.Enabled = false;
            _btnCanOpen.Enabled = false; _btnCanClose.Enabled = false;
            _lblCanStatus.Text = "Disconnected"; _lblCanStatus.ForeColor = Color.Gray;
            AddLog("CAN port disconnected.", LogColor.Warn);
        }

        void CanOpen(object? s, EventArgs e)
        {
            Task.Run(() => {
                CanSendRaw(SLCAN.SetMode(_chkCanSilent.Checked));          Thread.Sleep(30);
                CanSendRaw(SLCAN.SetAutoRetransmit(_chkCanAutoRetx.Checked)); Thread.Sleep(30);
                CanSendRaw(SLCAN.SetNomRate(_cmbNomRate.SelectedIndex));    Thread.Sleep(30);
                CanSendRaw(SLCAN.SetFdRate(_cmbFdRate.SelectedIndex));      Thread.Sleep(30);
                CanSendRaw(SLCAN.Open());
                BeginInvoke(() => AddLog($"CAN opened — Nom:{_cmbNomRate.Text}  FD:{_cmbFdRate.Text}", LogColor.Warn));
            });
        }

        void CanClose(object? s, EventArgs e)
        {
            CanStopLoop(); CanMultiStop(null, EventArgs.Empty); CanBruteStop(null, EventArgs.Empty);
            CanSendRaw(SLCAN.Close());
            AddLog("CAN channel closed.", LogColor.Warn);
        }

        // ── TX frame builder (from Signal tab UI) ─────────────────────────────
        byte[]? CanBuildFrame()
        {
            if (!int.TryParse(_txtCanId.Text.TrimStart('0','x','X'), System.Globalization.NumberStyles.HexNumber, null, out int id))
                if (!int.TryParse(_txtCanId.Text, out id)) { AddLog("Invalid CAN ID.", LogColor.Error); return null; }

            int typeIdx = _cmbCanFrameType.SelectedIndex;
            bool remote = typeIdx == 2 || typeIdx == 3;
            bool fd     = typeIdx >= 4;
            bool brs    = typeIdx == 6 || typeIdx == 7;
            bool ext    = typeIdx == 1 || typeIdx == 3 || typeIdx == 5 || typeIdx == 7;

            int dlcOrLen = int.TryParse(_cmbCanDlc.SelectedItem?.ToString(), out int dv) ? dv : 8;
            if (remote) return ext ? SLCAN.RemoteExt(id, dlcOrLen) : SLCAN.RemoteStd(id, dlcOrLen);

            var tokens = _txtCanData.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var data   = new byte[64];
            for (int i = 0; i < Math.Min(tokens.Length, 64); i++)
                byte.TryParse(tokens[i], System.Globalization.NumberStyles.HexNumber, null, out data[i]);
            Array.Copy(data, _canTxData, 64);

            if (fd)  return ext ? SLCAN.SendFdExt(id, data, dlcOrLen, brs) : SLCAN.SendFdStd(id, data, dlcOrLen, brs);
            int dlc = Math.Min(dlcOrLen, 8);
            return ext ? SLCAN.SendExt(id, data, dlc) : SLCAN.SendStd(id, data, dlc);
        }

        void CanSendTxFrame()
        {
            var frame = CanBuildFrame();
            if (frame == null) return;
            lock (_canLock)
            {
                if (_canPort?.IsOpen != true) { AddLog("CAN not connected.", LogColor.Error); return; }
                _canPort.Write(frame, 0, frame.Length);
            }
            int typeIdx = _cmbCanFrameType.SelectedIndex;
            bool fd  = typeIdx >= 4;
            bool brs = typeIdx == 6 || typeIdx == 7;
            bool ext = typeIdx == 1 || typeIdx == 3 || typeIdx == 5 || typeIdx == 7;
            string ftype = fd ? (brs ? "FD+BRS" : "FD") : (typeIdx == 2 || typeIdx == 3 ? "Remote" : "Data");
            string idStr = ext ? _txtCanId.Text.PadLeft(8,'0') : _txtCanId.Text.PadLeft(3,'0');
            string dStr  = string.Join(" ", _txtCanData.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(fd ? 64 : 8));
            Interlocked.Increment(ref _canTxCount);
            CanAddRow("TX", ftype, idStr.ToUpper(), _cmbCanDlc.SelectedItem?.ToString() ?? "8", dStr);
        }

        void CanAddRow(string dir, string type, string id, string dlc, string data)
        {
            if (InvokeRequired) { BeginInvoke(() => CanAddRow(dir, type, id, dlc, data)); return; }

            // Group-by-ID: key = dir:type:id
            if (_chkCanGroupById.Checked)
            {
                string key = $"{dir}:{id}";
                if (_canIdRowMap.TryGetValue(key, out int ri) && ri < _grdCan.Rows.Count)
                {
                    var rr = _grdCan.Rows[ri];
                    rr.Cells["CnTime"].Value = DateTime.Now.ToString("HH:mm:ss.fff");
                    rr.Cells["CnData"].Value = data;
                    long cnt = (long.TryParse(rr.Cells["CnCnt"].Value?.ToString(), out long cv) ? cv : 0) + 1;
                    rr.Cells["CnCnt"].Value  = cnt;
                    rr.Cells["CnSeq"].Value  = _canRxCount + _canTxCount;
                    CanUpdateStats(); return;
                }
                int nr = _grdCan.Rows.Add();
                _canIdRowMap[key] = nr;
                var row = _grdCan.Rows[nr];
                row.Cells["CnSeq"].Value  = _canRxCount + _canTxCount;
                row.Cells["CnTime"].Value = DateTime.Now.ToString("HH:mm:ss.fff");
                row.Cells["CnDir"].Value  = dir;
                row.Cells["CnType"].Value = type;
                row.Cells["CnId"].Value   = id;
                row.Cells["CnDlc"].Value  = dlc;
                row.Cells["CnCnt"].Value  = 1;
                row.Cells["CnData"].Value = data;
                row.DefaultCellStyle.ForeColor = dir == "TX" ? Color.CornflowerBlue : Color.LimeGreen;
                CanUpdateStats(); return;
            }

            int r = _grdCan.Rows.Add();
            var rw = _grdCan.Rows[r];
            rw.Cells["CnSeq"].Value  = _canRxCount + _canTxCount;
            rw.Cells["CnTime"].Value = DateTime.Now.ToString("HH:mm:ss.fff");
            rw.Cells["CnDir"].Value  = dir;
            rw.Cells["CnType"].Value = type;
            rw.Cells["CnId"].Value   = id;
            rw.Cells["CnDlc"].Value  = dlc;
            rw.Cells["CnCnt"].Value  = 1;
            rw.Cells["CnData"].Value = data;
            rw.DefaultCellStyle.ForeColor = dir == "TX" ? Color.CornflowerBlue : Color.LimeGreen;
            TryScrollToLastRow(_grdCan);
            CanUpdateStats();
        }

        // ── Single-signal TX loop ─────────────────────────────────────────────
        void CanStartLoop()
        {
            if (_canPort?.IsOpen != true) { AddLog("CAN not connected.", LogColor.Error); return; }
            var frame = CanBuildFrame(); if (frame == null) return;

            string modifier = _txtCanMod.Text.Trim();
            var tokens   = _txtCanData.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var workData = new byte[64];
            for (int i = 0; i < Math.Min(tokens.Length, 64); i++)
                byte.TryParse(tokens[i], System.Globalization.NumberStyles.HexNumber, null, out workData[i]);

            int typeIdx = _cmbCanFrameType.SelectedIndex;
            string idText = _txtCanId.Text;
            int dlcVal = int.TryParse(_cmbCanDlc.SelectedItem?.ToString(), out int dv) ? dv : 8;
            bool remote = typeIdx==2||typeIdx==3, fd = typeIdx>=4, brs = typeIdx==6||typeIdx==7, ext = typeIdx==1||typeIdx==3||typeIdx==5||typeIdx==7;
            string ftype = fd?(brs?"FD+BRS":"FD"):(remote?"Remote":"Data");

            _btnCanLoop.Enabled = false; _btnCanStop.Enabled = true;
            int ms = (int)_nudCanMs.Value;
            AddLog($"CAN TX loop started every {ms}ms", LogColor.Warn);

            _canTxTimer = new System.Threading.Timer(_ =>
            {
                if (modifier.Length > 0) ApplyModifier(modifier, workData);
                if (!int.TryParse(idText.TrimStart('0','x','X'), System.Globalization.NumberStyles.HexNumber, null, out int idVal)) idVal = 0;
                byte[] f;
                if      (remote && !ext) f = SLCAN.RemoteStd(idVal, dlcVal);
                else if (remote &&  ext) f = SLCAN.RemoteExt(idVal, dlcVal);
                else if (fd && !ext)     f = SLCAN.SendFdStd(idVal, workData, dlcVal, brs);
                else if (fd &&  ext)     f = SLCAN.SendFdExt(idVal, workData, dlcVal, brs);
                else if (!ext)           f = SLCAN.SendStd(idVal, workData, Math.Min(dlcVal,8));
                else                     f = SLCAN.SendExt(idVal, workData, Math.Min(dlcVal,8));
                lock (_canLock) { if (_canPort?.IsOpen == true) { _canPort.Write(f, 0, f.Length); Interlocked.Increment(ref _canTxCount); } }
                CanAddRow("TX", ftype, CanFormatId(idVal, ext), dlcVal.ToString(), CanFormatData(workData, fd, dlcVal));
                try { BeginInvoke(CanUpdateStats); } catch { }
            }, null, 0, ms);
        }

        void CanStopLoop()
        {
            _canTxTimer?.Dispose(); _canTxTimer = null;
            if (InvokeRequired) { BeginInvoke(CanStopLoop); return; }
            _btnCanLoop.Enabled = true; _btnCanStop.Enabled = false;
        }

        // ── Multi-signal ──────────────────────────────────────────────────────
        void CanAddMultiRow(string type="t", string id="000", string dlc="8",
                            string data="FF FF FF FF FF FF FF FF", int ms=100, string mod="")
        {
            int r = _canGrid.Rows.Add();
            _canGrid.Rows[r].Cells["COn"].Value   = true;
            _canGrid.Rows[r].Cells["CType"].Value = type;
            _canGrid.Rows[r].Cells["CId"].Value   = id;
            _canGrid.Rows[r].Cells["CDlc"].Value  = dlc;
            _canGrid.Rows[r].Cells["CData"].Value = data;
            _canGrid.Rows[r].Cells["CMs"].Value   = ms.ToString();
            _canGrid.Rows[r].Cells["CMod"].Value  = mod;
            _canGrid.Rows[r].Cells["CSent"].Value = "0";
        }

        void CanRemoveMultiRow()
        {
            if (_canMultiTimer != null) { AddLog("Stop multi first.", LogColor.Warn); return; }
            if (_canGrid.SelectedRows.Count == 0) return;
            _canGrid.Rows.Remove(_canGrid.SelectedRows[0]);
        }

        void SendCanMultiRowOnce(int rowIndex)
        {
            if (_canPort?.IsOpen != true) return;
            if (rowIndex < 0 || rowIndex >= _canGrid.Rows.Count) return;
            var row = _canGrid.Rows[rowIndex];
            if (row.IsNewRow) return;

            string typeStr = row.Cells["CType"].Value?.ToString() ?? "t";
            int typeIdx = typeStr switch { "T"=>1,"r"=>2,"R"=>3,"d"=>4,"D"=>5,"b"=>6,"B"=>7,_=>0 };
            int id = ParseHexInt(row.Cells["CId"].Value?.ToString() ?? "0", 0);
            int dlc = int.TryParse(row.Cells["CDlc"].Value?.ToString(), out int dl) ? dl : 8;
            var data = new byte[64];
            var toks = (row.Cells["CData"].Value?.ToString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < Math.Min(toks.Length, 64); i++)
                byte.TryParse(toks[i], System.Globalization.NumberStyles.HexNumber, null, out data[i]);

            var sig = new CanSigRow { TypeIdx = typeIdx, Id = id, DlcOrLen = dlc };
            byte[] frame = CanBuildFrameFromRow(sig, data);

            try
            {
                lock (_canLock)
                {
                    if (_canPort?.IsOpen != true) return;
                    _canPort.Write(frame, 0, frame.Length);
                    Interlocked.Increment(ref _canTxCount);
                }
                bool ext = typeIdx == 1 || typeIdx == 3 || typeIdx == 5 || typeIdx == 7;
                CanAddRow("TX", CanDisplayTypeFromIndex(typeIdx), CanFormatId(id, ext), dlc.ToString(), string.Join(" ", data.Take(typeIdx >= 4 ? SLCAN.FdDlcToBytes(SLCAN.BytesToFdDlc(dlc)) : Math.Min(dlc, 8)).Select(b => b.ToString("X2"))));
            }
            catch { }
        }

        void CanMultiStart(object? s, EventArgs e)
        {
            if (_canPort?.IsOpen != true) { AddLog("CAN not connected.", LogColor.Error); return; }
            _canMultiRows.Clear();
            for (int r = 0; r < _canGrid.Rows.Count; r++)
            {
                var row = _canGrid.Rows[r];
                if (row.IsNewRow || !(row.Cells["COn"].Value is true)) continue;
                string typeStr = row.Cells["CType"].Value?.ToString() ?? "t";
                int typeIdx2 = typeStr switch { "T"=>1,"r"=>2,"R"=>3,"d"=>4,"D"=>5,"b"=>6,"B"=>7,_=>0 };
                var sig = new CanSigRow
                {
                    TypeIdx    = typeIdx2,
                    GridRow    = r,
                    Id         = (int)(ParseHexByte(row.Cells["CId"].Value?.ToString() ?? "0") | (int.TryParse(row.Cells["CId"].Value?.ToString(), System.Globalization.NumberStyles.HexNumber, null, out int fullId) ? fullId : 0)),
                    DlcOrLen   = int.TryParse(row.Cells["CDlc"].Value?.ToString(), out int dl) ? dl : 8,
                    IntervalMs = int.TryParse(row.Cells["CMs"].Value?.ToString(),  out int ms) ? Math.Max(1,ms) : 100,
                    Modifier   = row.Cells["CMod"].Value?.ToString() ?? "",
                };
                // Parse ID properly (up to 29-bit hex)
                if (int.TryParse(row.Cells["CId"].Value?.ToString(), System.Globalization.NumberStyles.HexNumber, null, out int pid)) sig.Id = pid;
                var bytes = (row.Cells["CData"].Value?.ToString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < Math.Min(bytes.Length, 64); i++)
                    byte.TryParse(bytes[i], System.Globalization.NumberStyles.HexNumber, null, out sig.Data[i]);
                sig.NextMs = Environment.TickCount64;
                _canMultiRows.Add(sig);
            }
            if (_canMultiRows.Count == 0) { AddLog("No enabled rows.", LogColor.Warn); return; }
            foreach (var sig in _canMultiRows) _canGrid.Rows[sig.GridRow].Cells["CSent"].Value = "0";
            _canMultiTimer?.Dispose();
            _canMultiTimer = new System.Threading.Timer(CanMultiTick, null, 5, 5);
            _btnCanMStart.Enabled = false; _btnCanMStop.Enabled = true;
            _lblCanMStatus.Text = $"Running {_canMultiRows.Count} signal(s)";
        }

        void CanMultiStop(object? s, EventArgs e)
        {
            _canMultiTimer?.Dispose(); _canMultiTimer = null;
            if (InvokeRequired) { BeginInvoke(() => CanMultiStop(s, e ?? EventArgs.Empty)); return; }
            _btnCanMStart.Enabled = true; _btnCanMStop.Enabled = false;
            _lblCanMStatus.Text = $"Stopped ({_canMultiRows.Sum(r => r.Count)} total sent)";
        }

        void CanMultiTick(object? _)
        {
            long now = Environment.TickCount64;
            foreach (var sig in _canMultiRows)
            {
                if (now < sig.NextMs) continue;
                sig.NextMs = now + sig.IntervalMs;
                var pending = Interlocked.Exchange(ref sig.PendingData, null);
                if (pending != null) Array.Copy(pending, sig.Data, Math.Min(pending.Length, sig.Data.Length));
                if (sig.Modifier.Length > 0) ApplyModifier(sig.Modifier, sig.Data);
                var frame = CanBuildFrameFromRow(sig, sig.Data);
                lock (_canLock) { if (_canPort?.IsOpen == true) { _canPort.Write(frame, 0, frame.Length); Interlocked.Increment(ref _canTxCount); sig.Count++; } }
                bool ext = sig.TypeIdx == 1 || sig.TypeIdx == 3 || sig.TypeIdx == 5 || sig.TypeIdx == 7;
                bool fd = sig.TypeIdx >= 4;
                CanAddRow("TX", CanDisplayTypeFromIndex(sig.TypeIdx), CanFormatId(sig.Id, ext), sig.DlcOrLen.ToString(), CanFormatData(sig.Data, fd, sig.DlcOrLen));
                int gridRow = sig.GridRow; long cnt = sig.Count;
                string liveData = CanFormatData(sig.Data, fd, sig.DlcOrLen);
                try { BeginInvoke(() =>
                {
                    if (gridRow < _canGrid.Rows.Count)
                    {
                        _canGrid.Rows[gridRow].Cells["CData"].Value = liveData;
                        _canGrid.Rows[gridRow].Cells["CSent"].Value = cnt.ToString();
                    }
                }); } catch { }
            }
        }

        void OnCanGridCellChanged(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var sig = _canMultiRows.FirstOrDefault(r => r.GridRow == e.RowIndex);
            if (sig == null) return;
            string col = _canGrid.Columns[e.ColumnIndex].Name;
            string val = _canGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString() ?? "";
            switch (col)
            {
                case "CId":
                    if (int.TryParse(val, System.Globalization.NumberStyles.HexNumber, null, out int id)) sig.Id = id; break;
                case "CData":
                    var bytes2 = val.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => { byte.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out byte b); return b; }).ToArray();
                    var pend = new byte[64]; Array.Copy(bytes2, pend, Math.Min(bytes2.Length,64));
                    sig.PendingData = pend; break;
                case "CDlc": if (int.TryParse(val, out int dl)) sig.DlcOrLen = dl; break;
                case "CMs":  if (int.TryParse(val, out int ms)) sig.IntervalMs = Math.Max(1,ms); break;
                case "CMod": sig.Modifier = val; break;
            }
        }

        // ── CAN Brute Force ───────────────────────────────────────────────────
        void OnCanMultiCellEndEdit(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (_canGrid.Columns[e.ColumnIndex].Name != "CMod") return;
            ApplyCanGridModifierPreview(e.RowIndex);
        }

        void ApplyCanGridModifierPreview(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _canGrid.Rows.Count) return;
            var row = _canGrid.Rows[rowIndex];
            if (row.IsNewRow) return;

            string modifier = row.Cells["CMod"].Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(modifier)) return;

            string typeStr = row.Cells["CType"].Value?.ToString() ?? "t";
            int typeIdx = typeStr switch { "T"=>1,"r"=>2,"R"=>3,"d"=>4,"D"=>5,"b"=>6,"B"=>7,_=>0 };
            int dlc = int.TryParse(row.Cells["CDlc"].Value?.ToString(), out int dl) ? dl : 8;
            var data = new byte[64];
            var bytes = (row.Cells["CData"].Value?.ToString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => { byte.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out byte b); return b; })
                .Take(64).ToArray();
            Array.Copy(bytes, data, Math.Min(bytes.Length, data.Length));
            ApplyModifier(modifier, data);

            string updated = CanFormatData(data, typeIdx >= 4, dlc);
            string current = row.Cells["CData"].Value?.ToString() ?? "";
            if (!string.Equals(current, updated, StringComparison.OrdinalIgnoreCase))
                row.Cells["CData"].Value = updated;
        }

        void CanBruteStart(object? s, EventArgs e)
        {
            if (_canPort?.IsOpen != true) { AddLog("CAN not connected.", LogColor.Error); return; }

            int idStart = ParseHexInt(_txtCanBfStart.Text, 0);
            int idEnd   = ParseHexInt(_txtCanBfEnd.Text, 0x7FF);
            int step    = ParseHexInt(_txtCanBfStep.Text, 1);
            step = Math.Max(1, step);
            int delay   = (int)_nudCanBfDelay.Value;
            int rxTo    = (int)_nudCanBfRxTo.Value;
            int dlc     = (int)_nudCanBfDlc.Value;
            int typeIdx = _cmbCanBfType.SelectedIndex;
            bool ext    = typeIdx == 1 || typeIdx == 3 || typeIdx == 5;
            bool useInc = _txtCanBfData.Text.Trim().Equals("INC", StringComparison.OrdinalIgnoreCase);
            var  dataToks = _txtCanBfData.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var  staticData = new byte[64];
            if (!useInc) { for (int i = 0; i < Math.Min(dataToks.Length,64); i++) byte.TryParse(dataToks[i], System.Globalization.NumberStyles.HexNumber, null, out staticData[i]); }
            bool sweepPayload = useInc || (dataToks.Length > 0 && dataToks.All(t => string.Equals(t, dataToks[0], StringComparison.OrdinalIgnoreCase)));
            int  sweepStart   = sweepPayload && !useInc && dataToks.Length > 0 ? staticData[0] : 0;
            if (idEnd < idStart) { AddLog("CAN brute-force end must be >= start.", LogColor.Error); return; }

            CanStopLoop();
            CanMultiStop(null, EventArgs.Empty);
            _canBfReplayTimer?.Dispose(); _canBfReplayTimer = null;
            _btnCanBfReplay.Text = "Replay";
            _btnCanSend.Enabled  = false;
            _btnCanLoop.Enabled  = false;
            _btnCanMStart.Enabled = false;

            _grdCanBf.Rows.Clear();
            _pgsCanBf.Value = 0; _lblCanBfStatus.Text = "Starting…";
            _btnCanBfStart.Enabled = false; _btnCanBfStop.Enabled = true;
            _canBfCts = new CancellationTokenSource();
            var ct = _canBfCts.Token;

            int idCount = 0; for (int id2 = idStart; id2 <= idEnd; id2++) idCount++;
            int valueCount = 0; for (int v = sweepStart; v <= 0xFF; v += step) valueCount++;
            int total = sweepPayload ? idCount * valueCount : idCount;

            _canBfConstTimer?.Dispose(); _canBfConstTimer = null;
            if (_chkCanBfConstant.Checked)
            {
                int constId = ParseHexInt(_txtCanBfConstId.Text, 0x123);
                var constData = new byte[64];
                var constToks = _txtCanBfConstData.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < Math.Min(constToks.Length, 64); i++)
                    byte.TryParse(constToks[i], System.Globalization.NumberStyles.HexNumber, null, out constData[i]);
                var constFrame = BuildCanBruteFrame(constId, constData, dlc, typeIdx);
                int constMs = (int)_nudCanBfConstMs.Value;
                void SendCanConst()
                {
                    try
                    {
                        if (ct.IsCancellationRequested) return;
                        lock (_canLock)
                        {
                            if (_canPort?.IsOpen == true)
                            {
                                _canPort.Write(constFrame, 0, constFrame.Length);
                                Interlocked.Increment(ref _canTxCount);
                            }
                        }
                        CanAddRow("TX", CanDisplayTypeFromBruteIndex(typeIdx), CanFormatId(constId, ext), dlc.ToString(), CanFormatData(constData, typeIdx >= 2, dlc));
                        try { BeginInvoke(CanUpdateStats); } catch { }
                    }
                    catch { }
                }
                SendCanConst();
                _canBfConstTimer = new System.Threading.Timer(_ =>
                {
                    SendCanConst();
                }, null, constMs, constMs);
            }

            Task.Run(() =>
            {
                int done = 0;
                _canBfActive = true;
                lock (_canLock) { if (_canPort?.IsOpen == true) _canPort.DiscardInBuffer(); }
                while (_canBfRxQ.TryDequeue(out _)) { }
                try
                {
                    for (int id2 = idStart; id2 <= idEnd && !ct.IsCancellationRequested; id2++)
                    {
                        int valueStart = sweepPayload ? sweepStart : 0;
                        int valueEnd   = sweepPayload ? 0xFF : 0;
                        int valueStep  = sweepPayload ? step : 1;
                        for (int byteVal = valueStart; byteVal <= valueEnd && !ct.IsCancellationRequested; byteVal += valueStep)
                        {
                            var data2 = sweepPayload
                                ? Enumerable.Repeat((byte)byteVal, dlc).ToArray()
                                : staticData.Take(dlc).ToArray();
                            byte[] f  = BuildCanBruteFrame(id2, data2, dlc, typeIdx);
                            lock (_canLock) { if (_canPort == null || !_canPort.IsOpen) return; _canPort.Write(f, 0, f.Length); Interlocked.Increment(ref _canTxCount); }
                            CanAddRow("TX", CanDisplayTypeFromBruteIndex(typeIdx), CanFormatId(id2, ext), dlc.ToString(), CanFormatData(data2, typeIdx >= 2, dlc));
                            if (delay > 0) Thread.Sleep(delay);

                            bool gotResp = false; string respStr = "";
                            var deadline = DateTime.UtcNow.AddMilliseconds(rxTo);
                            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                            {
                                if (_canBfRxQ.TryDequeue(out var rf))
                                { gotResp = true; respStr = string.Join(" ", rf.Data.Take(rf.ByteLen).Select(b => b.ToString("X2"))); break; }
                                Thread.Sleep(1);
                            }

                            done++;
                            int pct     = (int)(done * 100L / total);
                            string ids  = ext ? id2.ToString("X8") : id2.ToString("X3");
                            string type = CanBruteTypeCode(typeIdx);
                            string pay  = string.Join(" ", data2.Select(b => b.ToString("X2")));
                            try { BeginInvoke(() =>
                            {
                                int r = _grdCanBf.Rows.Add();
                                _grdCanBf.Rows[r].Cells["CBfType"].Value = type;
                                _grdCanBf.Rows[r].Cells["CBfId"].Value   = ids;
                                _grdCanBf.Rows[r].Cells["CBfDlc"].Value  = dlc.ToString();
                                _grdCanBf.Rows[r].Cells["CBfPay"].Value  = pay;
                                _grdCanBf.Rows[r].Cells["CBfAck"].Value  = gotResp ? "YES" : "-";
                                _grdCanBf.Rows[r].Cells["CBfResp"].Value = respStr;
                                if (gotResp) _grdCanBf.Rows[r].DefaultCellStyle.ForeColor = Color.LimeGreen;
                                _pgsCanBf.Value = Math.Min(100,pct);
                                _lblCanBfStatus.Text = sweepPayload ? $"ID=0x{ids}  Byte=0x{byteVal:X2}  {done}/{total}" : $"ID=0x{ids}  {done}/{total}";
                                TryScrollToLastRow(_grdCanBf);
                            }); } catch { return; }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { try { BeginInvoke(() => AddLog($"CAN BF error: {ex.Message}", LogColor.Error)); } catch { } }
                finally
                {
                    _canBfActive = false;
                    _canBfConstTimer?.Dispose(); _canBfConstTimer = null;
                    try { BeginInvoke(() =>
                    {
                        _pgsCanBf.Value = ct.IsCancellationRequested ? _pgsCanBf.Value : 100;
                        _lblCanBfStatus.Text = ct.IsCancellationRequested ? $"Stopped {done}/{total}" : $"Done — {done} frames";
                        _btnCanBfStart.Enabled = true; _btnCanBfStop.Enabled = false;
                        _btnCanSend.Enabled   = _canPort?.IsOpen == true;
                        _btnCanLoop.Enabled   = _canPort?.IsOpen == true;
                        _btnCanMStart.Enabled = _canPort?.IsOpen == true;
                    }); } catch { }
                }
            }, ct);
        }

        void CanBruteStop(object? s, EventArgs e)
        {
            _canBfCts?.Cancel();
            _canBfConstTimer?.Dispose(); _canBfConstTimer = null;
            _canBfReplayTimer?.Dispose(); _canBfReplayTimer = null;
            _btnCanBfReplay.Text = "Replay";
            _btnCanBfStop.Enabled = false;
        }

        void RefreshCanSigCombo()
        {
            _cmbCanBfSig.Items.Clear();
            for (int r = 0; r < _canGrid.Rows.Count; r++)
            {
                if (_canGrid.Rows[r].IsNewRow) continue;
                string type = _canGrid.Rows[r].Cells["CType"].Value?.ToString() ?? "t";
                string id   = _canGrid.Rows[r].Cells["CId"].Value?.ToString() ?? "000";
                string data = _canGrid.Rows[r].Cells["CData"].Value?.ToString() ?? "";
                string preview = data.Length > 23 ? data[..23] + "..." : data;
                _cmbCanBfSig.Items.Add($"[{r}] {type} ID={id}  {preview}");
            }
            if (_cmbCanBfSig.Items.Count > 0) _cmbCanBfSig.SelectedIndex = 0;
        }

        void LoadCanRowIntoBfConst(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _canGrid.Rows.Count) return;
            var row = _canGrid.Rows[rowIndex];
            _txtCanBfConstId.Text     = row.Cells["CId"].Value?.ToString() ?? "000";
            _txtCanBfConstData.Text   = row.Cells["CData"].Value?.ToString() ?? "00 00 00 00 00 00 00 00";
            if (int.TryParse(row.Cells["CMs"].Value?.ToString(), out int ms))
                _nudCanBfConstMs.Value = Math.Max(_nudCanBfConstMs.Minimum, Math.Min(_nudCanBfConstMs.Maximum, ms));
            _chkCanBfConstant.Checked = true;
            AddLog($"Loaded CAN multi row {rowIndex} into brute-force constant signal.", LogColor.Info);
        }

        void LoadCanSigIntoBfConst()
        {
            int sel = _cmbCanBfSig.SelectedIndex;
            if (sel < 0) { RefreshCanSigCombo(); return; }
            int mapped = 0;
            for (int r = 0; r < _canGrid.Rows.Count; r++)
            {
                if (_canGrid.Rows[r].IsNewRow) continue;
                if (mapped == sel) { LoadCanRowIntoBfConst(r); return; }
                mapped++;
            }
        }

        void CanBruteReplaySingle()
        {
            if (_canPort?.IsOpen != true) { AddLog("CAN not connected.", LogColor.Error); return; }
            if (_grdCanBf.CurrentRow?.IsNewRow != false) { AddLog("No CAN brute-force row selected.", LogColor.Warn); return; }

            string type = _grdCanBf.CurrentRow.Cells["CBfType"].Value?.ToString() ?? "t";
            string id   = _grdCanBf.CurrentRow.Cells["CBfId"].Value?.ToString() ?? "000";
            string data = _grdCanBf.CurrentRow.Cells["CBfPay"].Value?.ToString() ?? "";
            int dlc     = int.TryParse(_grdCanBf.CurrentRow.Cells["CBfDlc"].Value?.ToString(), out int dv) ? dv : 8;
            int idVal   = ParseHexInt(id, 0);
            int typeIdx = CanBruteTypeIndexFromCode(type);

            var workData = new byte[64];
            var tokens   = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < Math.Min(tokens.Length, 64); i++)
                byte.TryParse(tokens[i], System.Globalization.NumberStyles.HexNumber, null, out workData[i]);

            var frame = BuildCanBruteFrame(idVal, workData, dlc, typeIdx);
            lock (_canLock)
            {
                if (_canPort?.IsOpen != true) return;
                _canPort.Write(frame, 0, frame.Length);
                Interlocked.Increment(ref _canTxCount);
            }
            CanAddRow("TX", CanDisplayTypeFromBruteIndex(typeIdx), CanFormatId(idVal, typeIdx == 1 || typeIdx == 3 || typeIdx == 5), dlc.ToString(), CanFormatData(workData, typeIdx >= 2, dlc));
            CanUpdateStats();
        }

        void CanBruteReplay(object? s, EventArgs e)
        {
            if (!_chkCanBfReplayLoop.Checked)
            {
                CanBruteReplaySingle();
                return;
            }

            if (_canBfReplayTimer != null)
            {
                _canBfReplayTimer.Dispose(); _canBfReplayTimer = null;
                _canBfConstTimer?.Dispose(); _canBfConstTimer = null;
                _btnCanBfReplay.Text = "Replay";
                AddLog("CAN brute-force replay loop stopped.", LogColor.Warn);
                return;
            }

            var replayRows = _grdCanBf.Rows.Cast<DataGridViewRow>()
                .Where(r => !r.IsNewRow)
                .Select(r =>
                {
                    string typeCode = r.Cells["CBfType"].Value?.ToString() ?? "t";
                    string idText   = r.Cells["CBfId"].Value?.ToString() ?? "000";
                    string dataText = r.Cells["CBfPay"].Value?.ToString() ?? "";
                    int dlcValue    = int.TryParse(r.Cells["CBfDlc"].Value?.ToString(), out int parsedDlc) ? parsedDlc : 8;
                    int bruteType   = CanBruteTypeIndexFromCode(typeCode);
                    var bytes       = new byte[64];
                    var parts       = dataText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < Math.Min(parts.Length, 64); i++)
                        byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i]);
                    return new
                    {
                        TypeCode = typeCode,
                        TypeIdx  = bruteType,
                        IdText   = idText,
                        IdValue  = ParseHexInt(idText, 0),
                        Dlc      = dlcValue,
                        DataText = dataText,
                        Data     = bytes,
                    };
                })
                .ToList();
            if (replayRows.Count == 0) { AddLog("No CAN brute-force rows to replay.", LogColor.Warn); return; }

            int intervalMs = (int)_nudCanBfDelay.Value > 0 ? (int)_nudCanBfDelay.Value : 20;
            _btnCanBfReplay.Text = "Stop Loop";
            AddLog($"CAN brute-force replay loop started: {replayRows.Count} row(s) every {intervalMs}ms", LogColor.Warn);

            _canBfConstTimer?.Dispose(); _canBfConstTimer = null;
            if (_chkCanBfConstant.Checked)
            {
                int constId = ParseHexInt(_txtCanBfConstId.Text, 0x123);
                var constData = new byte[64];
                var constToks = _txtCanBfConstData.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < Math.Min(constToks.Length, 64); i++)
                    byte.TryParse(constToks[i], System.Globalization.NumberStyles.HexNumber, null, out constData[i]);
                int constTypeIdx = replayRows[0].TypeIdx;
                int constDlc = replayRows[0].Dlc;
                var constFrame = BuildCanBruteFrame(constId, constData, constDlc, constTypeIdx);
                int constMs = (int)_nudCanBfConstMs.Value;

                void SendCanConst()
                {
                    try
                    {
                        lock (_canLock)
                        {
                            if (_canPort?.IsOpen != true) return;
                            _canPort.Write(constFrame, 0, constFrame.Length);
                            Interlocked.Increment(ref _canTxCount);
                        }
                        CanAddRow("TX", CanDisplayTypeFromBruteIndex(constTypeIdx), CanFormatId(constId, constTypeIdx == 1 || constTypeIdx == 3 || constTypeIdx == 5), constDlc.ToString(), CanFormatData(constData, constTypeIdx >= 2, constDlc));
                        try { BeginInvoke(CanUpdateStats); } catch { }
                    }
                    catch { }
                }

                SendCanConst();
                _canBfConstTimer = new System.Threading.Timer(_ => SendCanConst(), null, constMs, constMs);
            }

            int replayIndex = -1;
            _canBfReplayTimer = new System.Threading.Timer(_ =>
            {
                var row = replayRows[(Interlocked.Increment(ref replayIndex)) % replayRows.Count];
                var frame = BuildCanBruteFrame(row.IdValue, row.Data, row.Dlc, row.TypeIdx);
                lock (_canLock)
                {
                    if (_canPort?.IsOpen != true) return;
                    _canPort.Write(frame, 0, frame.Length);
                    Interlocked.Increment(ref _canTxCount);
                }
                CanAddRow("TX", CanDisplayTypeFromBruteIndex(row.TypeIdx), row.IdText, row.Dlc.ToString(), row.DataText);
                try { BeginInvoke(CanUpdateStats); } catch { }
            }, null, 0, intervalMs);
        }

        void CanBruteExport(object? s, EventArgs e)
        {
            using var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv|All files|*.*", DefaultExt = "csv", Title = "Export CAN Brute Force" };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            try
            {
                var sb = new StringBuilder(); sb.AppendLine("Type,ID,DLC,Payload,ACK,RespData");
                foreach (DataGridViewRow r in _grdCanBf.Rows)
                {
                    if (r.IsNewRow) continue;
                    sb.AppendLine($"{r.Cells["CBfType"].Value},{r.Cells["CBfId"].Value},{r.Cells["CBfDlc"].Value},{r.Cells["CBfPay"].Value},{r.Cells["CBfAck"].Value},{r.Cells["CBfResp"].Value}");
                }
                File.WriteAllText(dlg.FileName, sb.ToString());
            }
            catch (Exception ex) { AddLog($"CAN BF export: {ex.Message}", LogColor.Error); }
        }

        // ── RX thread ─────────────────────────────────────────────────────────
        void CanRxLoop()
        {
            var sb2 = new StringBuilder(128);
            while (_canRxRun)
            {
                int b;
                try
                {
                    SerialPort? p; lock (_canLock) { p = _canPort; }
                    if (p == null || !p.IsOpen) { Thread.Sleep(20); continue; }
                    b = p.ReadByte();
                }
                catch (TimeoutException) { continue; }
                catch { Thread.Sleep(20); continue; }

                if (b == 0x0D)
                {
                    string line = sb2.ToString(); sb2.Clear();
                    if (line.Length == 0) continue;
                    var frame = SLCAN.ParseFrame(line);
                    if (frame != null)
                    {
                        Interlocked.Increment(ref _canRxCount);
                        if (_canBfActive) { _canBfRxQ.Enqueue(frame); continue; }
                        string idStr = frame.Extended ? frame.Id.ToString("X8") : frame.Id.ToString("X3");
                        string dStr  = string.Join(" ", frame.Data.Take(frame.ByteLen).Select(x => x.ToString("X2")));
                        CanAddRow("RX", frame.Type, idStr, frame.Dlc.ToString("X"), dStr);
                    }
                    else if (line.StartsWith("V") || line.StartsWith("E"))
                        BeginInvoke(() => AddLog($"CAN device: {line}", LogColor.Info));
                }
                else if (b == 0x07)
                    BeginInvoke(() => AddLog("CAN TX NACK.", LogColor.Error));
                else
                { sb2.Append((char)b); if (sb2.Length > 512) sb2.Clear(); }
            }
        }

        // ── Log context-menu actions ──────────────────────────────────────────
        void CanReplaySelected()
        {
            if (_grdCan.CurrentRow?.IsNewRow != false) return;
            var row  = _grdCan.CurrentRow;
            string type = row.Cells["CnType"].Value?.ToString() ?? "";
            string id   = row.Cells["CnId"].Value?.ToString()   ?? "0";
            string dlc  = row.Cells["CnDlc"].Value?.ToString()  ?? "0";
            string data = row.Cells["CnData"].Value?.ToString()  ?? "";
            bool ext = id.Length == 8, fd = type.StartsWith("FD"), brs = type=="FD+BRS", remote = type=="Remote";
            if (!int.TryParse(id,  System.Globalization.NumberStyles.HexNumber, null, out int idV))  return;
            if (!int.TryParse(dlc, System.Globalization.NumberStyles.HexNumber, null, out int dlcV)) return;
            var dat = data.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => { byte.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out byte bv); return bv; }).ToArray();
            byte[] f;
            if      (remote&&!ext) f=SLCAN.RemoteStd(idV,dlcV);
            else if (remote&& ext) f=SLCAN.RemoteExt(idV,dlcV);
            else if (fd&&!ext)     f=SLCAN.SendFdStd(idV,dat,dat.Length,brs);
            else if (fd&& ext)     f=SLCAN.SendFdExt(idV,dat,dat.Length,brs);
            else if (!ext)         f=SLCAN.SendStd(idV,dat,Math.Min(dlcV,8));
            else                   f=SLCAN.SendExt(idV,dat,Math.Min(dlcV,8));
            lock (_canLock) { if (_canPort?.IsOpen==true) { _canPort.Write(f,0,f.Length); Interlocked.Increment(ref _canTxCount); } }
            CanAddRow("TX",type,id,dlc,data);
        }

        void CanLoadRowIntoTx()
        {
            if (_grdCan.CurrentRow?.IsNewRow != false) return;
            var row  = _grdCan.CurrentRow;
            string id   = row.Cells["CnId"].Value?.ToString()  ?? "000";
            string dlc  = row.Cells["CnDlc"].Value?.ToString() ?? "8";
            string data = row.Cells["CnData"].Value?.ToString() ?? "";
            string type = row.Cells["CnType"].Value?.ToString() ?? "Data";
            bool ext = id.Length==8, fd=type.StartsWith("FD"), brs=type=="FD+BRS", remote=type=="Remote";
            _txtCanId.Text = id.TrimStart('0').PadLeft(1,'0'); _txtCanData.Text = data;
            int idx=0;
            if (!fd&&!remote&&!ext) idx=0; else if (!fd&&!remote&&ext) idx=1;
            else if (!fd&&remote&&!ext) idx=2; else if (!fd&&remote&&ext) idx=3;
            else if (fd&&!brs&&!ext) idx=4;  else if (fd&&!brs&&ext)  idx=5;
            else if (fd&&brs&&!ext)  idx=6;  else idx=7;
            _cmbCanFrameType.SelectedIndex = idx;
            if (int.TryParse(dlc, System.Globalization.NumberStyles.HexNumber, null, out int dv))
            { int bl=fd?SLCAN.FdDlcToBytes(dv):dv; for (int i=0;i<_cmbCanDlc.Items.Count;i++) if (_cmbCanDlc.Items[i]?.ToString()==bl.ToString()){_cmbCanDlc.SelectedIndex=i;break;} }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
