using System;
using System.Windows.Forms;
using Sanford.Multimedia.Midi;
using Newtonsoft.Json;
using System.Text;

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

        public Explorer()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            OpenFileDialog openDialog = new OpenFileDialog();
            openDialog.Title = "Open Text File";
            openDialog.Filter = "MIDI files|*mid";

            var song = new Sequence();
            if (openDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    song.Load(openDialog.FileName);
                    var output = new StringBuilder();
                    output.AppendLine("Song Output-----------------------------------");
                    output.AppendLine(JsonConvert.SerializeObject(song, _jsonSettings));

                    foreach (var track in song)
                    {
                        output.AppendLine("Track Output-----------------------------------");
                        output.AppendLine(JsonConvert.SerializeObject(track, _jsonSettings));

                        output.AppendLine($"Track Events ({ track.Count })-----------------------------------");
                        for (int i = 0; i < track.Count; i++)
                        {
                            output.AppendLine(JsonConvert.SerializeObject(track.GetMidiEvent(i), _jsonSettings));
                        }
                    }

                    rtbOutput.Text = output.ToString();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading MIDI file:  {ex.Message}");
                }
            }
        }
    }
}
