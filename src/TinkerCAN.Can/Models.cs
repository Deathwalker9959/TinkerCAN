using System.Collections.Generic;

namespace TinkerCAN.Can
{
    // ── Runtime scheduler state ───────────────────────────────────────────────

    public class CanSigRow
    {
        public int    TypeIdx;
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

    // ── JSON config ───────────────────────────────────────────────────────────

    public class CanSignalCfg
    {
        public int    FrameTypeIndex { get; set; } = 0;
        public string Id             { get; set; } = "123";
        public string Dlc            { get; set; } = "8";
        public string Data           { get; set; } = "DE AD BE EF 00 00 00 00";
        public int    Ms             { get; set; } = 100;
        public string Modifier       { get; set; } = "";
    }

    public class CanMultiSignalCfg
    {
        public bool   Enabled  { get; set; } = true;
        public string Type     { get; set; } = "t";
        public string Id       { get; set; } = "000";
        public string Dlc      { get; set; } = "8";
        public string Data     { get; set; } = "FF FF FF FF FF FF FF FF";
        public int    Ms       { get; set; } = 100;
        public string Modifier { get; set; } = "";
    }

    public class CanBruteResultCfg
    {
        public string Type     { get; set; } = "t";
        public string Id       { get; set; } = "000";
        public string Dlc      { get; set; } = "8";
        public string Payload  { get; set; } = "";
        public string Ack      { get; set; } = "-";
        public string RespData { get; set; } = "";
    }

    public class CanBruteCfg
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

    public class CanConfigCfg
    {
        public int                     NomRateIndex   { get; set; } = SLCANProtocol.DefaultNomIdx;
        public int                     FdRateIndex    { get; set; } = SLCANProtocol.DefaultFdIdx;
        public bool                    Silent         { get; set; }
        public bool                    AutoRetransmit { get; set; }
        public CanSignalCfg            Signal         { get; set; } = new();
        public List<CanMultiSignalCfg> MultiSignals   { get; set; } = new();
        public CanBruteCfg             Brute          { get; set; } = new();
    }
}
