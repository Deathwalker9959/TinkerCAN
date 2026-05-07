using System.Collections.Generic;
using TinkerCAN.Lin;
using TinkerCAN.Can;

namespace LINGui.Models;

public class AppConfig
{
    public int                  LinBaud      { get; set; } = 19200;
    public SignalCfg            Signal       { get; set; } = new();
    public List<MultiSignalCfg> MultiSignals { get; set; } = new();
    public LinBruteCfg          LinBrute     { get; set; } = new();
    public CanConfigCfg         Can          { get; set; } = new();
}
