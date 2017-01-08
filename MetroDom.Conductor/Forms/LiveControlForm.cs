using MetroDom.Conductor.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using MetroDom.Core;
using Sanford.Multimedia;
using Sanford.Multimedia.Midi;

namespace MetroDom.Conductor.Forms
{
    public partial class LiveControlForm : Form
    {
        private InputDevice _selectedInput = null;
        private SerialService _serial;

        public LiveControlForm()
        {
            InitializeComponent();
            LoadMidiInputs();
        }

        /// <summary>
        /// Loads available MIDI inputs
        /// </summary>
        private void LoadMidiInputs()
        {
            MidiInCaps deviceProperties;

            if (InputDevice.DeviceCount == 0)
            {
                ddbMidiInputs.DropDownItems.Add("No MIDI input devices detected.");
                ddbMidiInputs.DropDownItems[0].Enabled = false;
            }

            for (int i = 0; i < InputDevice.DeviceCount; i++)
            {
                deviceProperties = InputDevice.GetDeviceCapabilities(i);
                ddbMidiInputs.DropDownItems.Add(deviceProperties.name);
                ddbMidiInputs.DropDownItems[i].Click += MidiInputSelected;
                ddbMidiInputs.DropDownItems[i].ImageIndex = i; // hack to store MIDI index
            }
        }

        private void MidiInputSelected(object sender, EventArgs e)
        {
            if (_selectedInput != null)
            {
                _selectedInput.StopRecording();
                _selectedInput.Close();
            }
            var menuItem = (ToolStripMenuItem)sender;
            var midiIndex = menuItem.ImageIndex;
            ddbMidiInputs.DropDownItems[midiIndex].Select();
            _serial = new SerialService();
            _selectedInput = new InputDevice(midiIndex);
            _selectedInput.ChannelMessageReceived += ChannelMessageReceived;
            _selectedInput.StartRecording();
        }

        private void ChannelMessageReceived(object sender, ChannelMessageEventArgs e)
        {
            // note value: e.Message.Data1
            // velocity: e.Message.Data2
            if (e.Message.Data2 == 0) return;
            if (e.Message.Data1 == 60)
            {
                _serial.SendMessage("0");
            }
            else if (e.Message.Data1 == 64)
            {
                _serial.SendMessage("4");
            }
            else if (e.Message.Data1 == 67)
            {
                _serial.SendMessage("7");
            }
        }
    }
}
