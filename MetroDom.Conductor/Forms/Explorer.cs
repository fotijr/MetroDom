using System;
using System.Windows.Forms;
using Sanford.Multimedia.Midi;
using System.Diagnostics;

namespace MetroDom.Conductor.Forms
{
    public partial class Explorer : Form
    {
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
                    Debug.WriteLine(song.SequenceType.ToString());
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error: Could not read file from disk. Original error: " + ex.Message);
                }
            }
        }
    }
}
