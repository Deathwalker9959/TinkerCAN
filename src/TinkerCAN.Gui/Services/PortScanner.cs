using System.Collections.Generic;
using System.IO.Ports;

namespace LINGui.Services;

public static class PortScanner
{
    public static IReadOnlyList<string> List() => SerialPort.GetPortNames();
}
