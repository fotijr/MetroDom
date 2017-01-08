using System;
using System.IO.Ports;

namespace MetroDom.Conductor.Services
{
    public class SerialService : IDisposable
    {
        SerialPort _serialPort = new SerialPort();
        const int _baudRate = 9600;

        public SerialService()
        {
            string[] ports = SerialPort.GetPortNames();
            foreach (var port in ports)
            {
                try
                {
                    _serialPort = new SerialPort(ports[0], _baudRate, Parity.None, 8, StopBits.One);
                    _serialPort.Open();
                    // TODO: add some type of question and response between device and host
                    // to verify device is compatible
                }
                catch (Exception)
                {
                    // unable to connect to a MetroDom device
                    throw;
                }
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
