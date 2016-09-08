using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MetroDom.Conductor.Services
{
    public class SerialService : IDisposable
    {
        SerialPort _serialPort = new SerialPort();

        public SerialService()
        {
            string[] ports = SerialPort.GetPortNames();
            if (ports.Length > 0)
            {
                _serialPort = new SerialPort(ports[0], 9600, Parity.None, 8, StopBits.One);
                _serialPort.Open();
            }
        }

        public void Dispose()
        {
            if (_serialPort.IsOpen) _serialPort.Close();
        }

        public void SendMessage(string note)
        {
            if (!_serialPort.IsOpen) return;
            _serialPort.WriteLine(note);
        }
    }
}
