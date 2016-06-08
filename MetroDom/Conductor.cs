using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO.Ports;
using MetroDom.Models;
using System.Threading;

namespace MetroDom
{
    public class Conductor : IDisposable
    {
        SerialPort _serialPort = new SerialPort();

        public Conductor()
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

        public void PlayNotes(List<char> instrumentCodes)
        {
            if (!_serialPort.IsOpen) return;
            _serialPort.WriteLine(string.Join("", instrumentCodes));

            // thread code temporary for turning off LEDS, can be removed once they are replaced by actuators
            Thread t = new Thread(
                         () =>
                         {
                             Thread.Sleep(20);
                             _serialPort.WriteLine(string.Join("", instrumentCodes));
                         }
                     );
            t.Start();
        }

        private void PlayNote(Instrument instrument)
        {
            if (!_serialPort.IsOpen) return;
            _serialPort.WriteLine(instrument.Code.ToString());
        }
    }
}
