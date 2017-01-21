//using RJCP.IO.Ports;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;

namespace MetroDom.Conductor.Services
{
    public class SerialService
    {
        public List<SerialInstrument> AvailableInstruments { get; set; }

        public async static Task<SerialService> InitializeAsync()
        {
            var serialService = new SerialService();
            var portNames = SerialPort.GetPortNames();
            SerialInstrument instrument;
            serialService.AvailableInstruments = new List<SerialInstrument>();
            foreach (var port in portNames)
            {
                try
                {
                    instrument = new SerialInstrument(port);
                    if (await instrument.IsValidInstrument())
                    {
                        serialService.AvailableInstruments.Add(instrument);
                    }
                }
                catch (Exception ex)
                {
                    // unable to connect to a MetroDom device
                    Console.WriteLine($"Serial device failed: {ex.Message}");
                }
            }
            return serialService;
        }

        private SerialService() { }
    }

    public class SerialInstrument : IDisposable
    {
        private SerialPort _serialPort = new SerialPort();
        //private SerialPortStream _serialPort;
        private string _portName;
        private const int _baudRate = 9600;
        private string _serialInput = "";
        public string Name { get; private set; }
        public Guid Id { get; private set; }
        public List<string> MessagesReceived { get; set; }

        public SerialInstrument(string portName)
        {
            _portName = portName;
            this.MessagesReceived = new List<string>();
        }

        public async Task<bool> IsValidInstrument()
        {
            Open();
            SendMessage("md?");
            var stillLoading = true;
            var waitUntil = DateTime.Now.AddMilliseconds(1200);
            while (stillLoading || DateTime.Now < waitUntil)
            {
                //if (!string.IsNullOrWhiteSpace(this.Name) && (this.Id !=Guid.Empty)){
                if (!string.IsNullOrWhiteSpace(this.Name))
                {
                    stillLoading = false;
                }
                await Task.Delay(100);
            }
            return true;
        }

        public void Open()
        {
            _serialPort = new SerialPort(_portName, _baudRate, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);
            _serialPort.RtsEnable = true;
            _serialPort.Handshake = System.IO.Ports.Handshake.None;
            _serialPort.DataReceived += new SerialDataReceivedEventHandler(MessageReceived);
            _serialPort.Open();
        }

        //public void Open()
        //{
        //    _serialPort = new SerialPortStream(_portName, _baudRate, 8, Parity.None, StopBits.One);
        //    //_serialPort.RtsEnable = true;
        //    //_serialPort.Handshake = System.IO.Ports.Handshake.None;
        //    //_serialPort.DataReceived += new SerialDataReceivedEventHandler(MessageReceived);
        //    _serialPort.Open();
        //}

        public void Close()
        {
            if (_serialPort.IsOpen) _serialPort.Close();
        }

        public void Dispose()
        {
            Close();
        }

        public void SendMessage(string note)
        {
            if (!_serialPort.IsOpen) return;
            _serialPort.Write(note);
        }

        /// <summary>
        /// Processes data received from the serial port
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MessageReceived(object sender, System.IO.Ports.SerialDataReceivedEventArgs e)
        {
            SerialPort sp = (SerialPort)sender;
            string response = "";
            char current;
            _serialInput += sp.ReadExisting();
            
            var toRemove = new Dictionary<int, int>();
            var starting = 0;
            int substringLength;
            for (int i = 0; i < _serialInput.Length; i++)
            {
                current = _serialInput[i];
                if (Environment.NewLine.Contains(current))
                {
                    // don't try to process string if it's consecutive new line characters
                    if (i > starting)
                    {
                        substringLength = (i - starting);
                        response = _serialInput.Substring(starting, substringLength);
                        ProcessSerialMessage(response);
                        toRemove.Add(starting, substringLength);
                    }
                    starting = i + 1;
                }
            }

            foreach (var item in toRemove)
            {
                _serialInput = _serialInput.Remove(item.Key, item.Value);
            }
        }

        private void ProcessSerialMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (message.StartsWith("Name:"))
            {
                this.Name = message.Replace("Name:", "");
                return;
            }
            if (message.StartsWith("Id:"))
            {
                Guid id;
                if (Guid.TryParse(message.Replace("Id:", ""), out id))
                {
                    this.Id = id;
                    return;
                }
            }
            Console.WriteLine("Serial message not processed: " + message);
        }

        //private async Task<bool> LibTestAsync(string portName)
        //{
        //    SerialPortStream hello = new SerialPortStream(portName, _baudRate);
        //    hello.Open();
        //    bool stillReading = true;
        //    int byteCount = 7;
        //    byte[] buffer = new byte[byteCount];

        //    int read = 0;
        //    int count;
        //    while (read < byteCount)
        //    {
        //        count = byteCount - read;
        //        read += await hello.ReadAsync(buffer, read, count);
        //    }

        //    return true;
        //}

        private string Read()
        {
            var line = _serialPort.ReadLine();
            return line;
        }
    }
}
