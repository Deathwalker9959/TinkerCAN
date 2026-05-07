using System;
using System.IO.Ports;
using System.Threading;

namespace TinkerCAN.Lin
{
    /// <summary>
    /// Serial transport for the LINTest-MI dongle (CH340, 460800 baud).
    /// Handles DTR probe, ring-buffer receive, and 16-byte frame alignment.
    /// </summary>
    public sealed class LINPort : IDisposable
    {
        readonly SerialPort _sp;

        // Ring buffer (64 KB). GUI uses 1 MB; 64 KB is sufficient for non-GUI use.
        readonly byte[] _rxBuf = new byte[65536];
        int _rxCount;
        int _rxSave;

        public string PortName => _sp.PortName;

        public LINPort(string port, int baudRate = 460800)
        {
            // Probe: toggles DTR to wake CH340 (mirrors Form1.cs init sequence)
            try
            {
                using var probe = new SerialPort(port, baudRate, Parity.None, 8, StopBits.One);
                probe.DtrEnable = true;
                probe.Open();
                Thread.Sleep(50);
            }
            catch { /* non-fatal */ }

            _sp = new SerialPort(port, baudRate, Parity.None, 8, StopBits.One)
            {
                Encoding = System.Text.Encoding.Latin1,
                DtrEnable = true,
                ReadTimeout = 500,
                ReceivedBytesThreshold = 16,
            };
            _sp.Open();
            _sp.DiscardInBuffer();
        }

        public void Send(byte[] frame)
        {
            if (frame.Length != 16) throw new InvalidOperationException("Frame must be 16 bytes");
            _sp.Write(frame, 0, 16);
            _sp.BaseStream.Flush();
        }

        /// <summary>
        /// Block until one validated 16-byte response frame arrives, or timeout.
        /// Mirrors the Timing_Receive_USB_Task ring-buffer logic from the original GUI.
        /// </summary>
        public bool TryReadFrame(out byte[] frame, int timeoutMs = 1000)
        {
            frame = Array.Empty<byte>();
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                int avail = _sp.BytesToRead;
                if (avail > 0)
                {
                    int space = _rxBuf.Length - _rxCount;
                    if (avail > space) avail = space;
                    if (avail > 0) _rxCount += _sp.Read(_rxBuf, _rxCount, avail);
                }

                int chunks = (_rxCount - _rxSave) / 16;
                for (int i = 0; i < chunks; i++)
                {
                    var candidate = new byte[16];
                    Buffer.BlockCopy(_rxBuf, _rxSave + i * 16, candidate, 0, 16);

                    if (LINProtocol.ValidateResponseFrame(candidate))
                    {
                        _rxSave = _rxCount / 16 * 16;
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
                Thread.Sleep(1);
            }
            return false;
        }

        public void Dispose() => _sp.Dispose();
    }
}
