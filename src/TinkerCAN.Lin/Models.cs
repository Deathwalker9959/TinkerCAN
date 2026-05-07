using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TinkerCAN.Lin
{
    // ── Runtime scheduler state ───────────────────────────────────────────────

    public class SigRow
    {
        public byte   Id;
        public byte[] Data       = new byte[8];
        public int    Len;
        public bool   Enhanced;
        public int    IntervalMs;
        public string Modifier   = "";
        public int    GridRow;
        public long   NextMs;
        public long   Count;
        public volatile byte[]? PendingData;
    }

    // ── JSON config ───────────────────────────────────────────────────────────

    public class SignalCfg
    {
        public string Id       { get; set; } = "22";
        public string Data     { get; set; } = "FF FF FF FF FF FF FF FF";
        public int    Len      { get; set; } = 8;
        public bool   Enhanced { get; set; } = true;
        public int    Ms       { get; set; } = 100;
        public string Modifier { get; set; } = "D0=D0+1";
    }

    public class MultiSignalCfg
    {
        public bool   Enabled  { get; set; } = true;
        public string Id       { get; set; } = "00";
        public string Data     { get; set; } = "FF FF FF FF FF FF FF FF";
        public int    Len      { get; set; } = 8;
        public bool   Enhanced { get; set; } = true;
        public int    Ms       { get; set; } = 100;
        public string Modifier { get; set; } = "";
    }

    public class LinBruteResultCfg
    {
        public string Id       { get; set; } = "00";
        public string Pid      { get; set; } = "00";
        public string Payload  { get; set; } = "";
        public string Response { get; set; } = "-";
        public string RespData { get; set; } = "";
    }

    public class LinBruteCfg
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
}
