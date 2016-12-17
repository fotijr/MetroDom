using System;
using System.Windows.Forms;
using Sanford.Multimedia.Midi;
using Newtonsoft.Json;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace MetroDom.Conductor.Forms
{
    public partial class Explorer : Form
    {
        private JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented
        };
        private Sequence _song = new Sequence();
        private Sequencer _player = new Sequencer();
        private OutputDevice _outDevice;
        private Dictionary<int, bool> _channels;

        public Explorer()
        {
            InitializeComponent();
        }


        private void Explorer_Load(object sender, EventArgs e)
        {
            if (OutputDevice.DeviceCount == 0)
            {
                MessageBox.Show("No MIDI output devices available. You won't be able to hear anything.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Stop);
            }
            else
            {
                try
                {
                    var outDeviceId = 0;
                    _outDevice = new OutputDevice(outDeviceId);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Error!", MessageBoxButtons.OK, MessageBoxIcon.Stop);

                    Close();
                }
            }
        }

        private void btnOpen_Click(object sender, EventArgs e)
        {
            OpenFileDialog openDialog = new OpenFileDialog();
            openDialog.Title = "Open Text File";
            openDialog.Filter = "MIDI files|*mid";

            if (openDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    _song.Load(openDialog.FileName);
                    var output = new StringBuilder();
                    output.AppendLine($"Song loaded from {openDialog.FileName}");
                    output.AppendLine($"Track count: {_song.Count}");

                    MidiEvent midiEvent;
                    ChannelMessage channelMsg;
                    MetaMessage metaMsg;
                    string message;
                    _channels = new Dictionary<int, bool>();
                    clbChannels.Items.Clear();
                    foreach (var track in _song)
                    {
                        output.AppendLine($"------ Track - { track.Count } events ------");
                        for (int i = 0; i < track.Count; i++)
                        {
                            midiEvent = track.GetMidiEvent(i);
                            output.AppendLine($"Message type: { midiEvent.MidiMessage.GetType().ToString() }");
                            if (midiEvent.MidiMessage is ChannelMessage)
                            {
                                channelMsg = (ChannelMessage)midiEvent.MidiMessage;
                                if (!_channels.ContainsKey(channelMsg.MidiChannel)) _channels.Add(channelMsg.MidiChannel, true);
                            }
                            else if (midiEvent.MidiMessage is MetaMessage)
                            {
                                metaMsg = (MetaMessage)midiEvent.MidiMessage;
                                message = Encoding.Default.GetString(metaMsg.GetBytes());
                                output.AppendLine($"{ metaMsg.MetaType.ToString() } : {message}");
                            }
                        }
                    }

                    foreach (var channel in _channels.OrderBy(c => c.Key))
                    {
                        clbChannels.Items.Add(channel.Key, true);
                    }

                    rtbOutput.Text = output.ToString();
                    btnPlay.Enabled = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading MIDI file:  {ex.Message}");
                }
            }
        }

        private void SequenceChannelMessagePlayed(object sender, ChannelMessageEventArgs e)
        {
            // TODO: improve this
            // this stops both on AND off messages, so if channel is toggled mid-play, notes can hang
            if (_channels[e.Message.MidiChannel]) _outDevice.Send(e.Message);
        }

        private void SequenceChased(object sender, ChasedEventArgs e)
        {
            foreach (ChannelMessage message in e.Messages)
            {
                _outDevice.Send(message);
            }
        }

        private void SequenceStopped(object sender, StoppedEventArgs e)
        {
            foreach (ChannelMessage message in e.Messages)
            {
                _outDevice.Send(message);
            }
        }

        private void btnPlay_Click(object sender, EventArgs e)
        {
            _player.Sequence = _song;
            _player.ChannelMessagePlayed += SequenceChannelMessagePlayed;
            _player.Chased += SequenceChased;
            _player.Stopped += SequenceStopped;
            _player.Start();
            btnStop.Enabled = true;
            btnPlay.Enabled = false;
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            _player.Stop();
            btnPlay.Enabled = true;
            btnStop.Enabled = false;
        }

        private void clbChannels_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (e.Index < 0) return;
            int key = int.Parse(clbChannels.Items[e.Index].ToString());
            _channels[key] = (e.NewValue == CheckState.Checked);
        }

        private void Explorer_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_outDevice != null) _outDevice.Dispose();
        }
    }
}
