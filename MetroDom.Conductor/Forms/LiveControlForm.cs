using MetroDom.Conductor.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using MetroDom.Core;
using Sanford.Multimedia;

namespace MetroDom.Conductor.Forms
{
    public partial class LiveControlForm : Form
    {
        Song _jingleBells = new Song(
                                    Key.EMajor,
                                    new List<SongNote>
                                    {
                                        new SongNote(Core.Note.E, 350),
                                        new SongNote(Core.Note.E, 350),
                                        new SongNote(Core.Note.E, 700),
                                        new SongNote(Core.Note.E, 350),
                                        new SongNote(Core.Note.E, 350),
                                        new SongNote(Core.Note.E, 700),
                                        new SongNote(Core.Note.E, 350),
                                        new SongNote(Core.Note.G, 350),
                                        new SongNote(Core.Note.C, 350),
                                        new SongNote(Core.Note.D, 350),
                                        new SongNote(Core.Note.E, 1050),
                                        new SongNote(Core.Note.F, 350),
                                        new SongNote(Core.Note.F, 350),
                                        new SongNote(Core.Note.F, 350),
                                        new SongNote(Core.Note.F, 350),
                                        new SongNote(Core.Note.E, 350),
                                        new SongNote(Core.Note.E, 350),
                                        new SongNote(Core.Note.E, 350),
                                        new SongNote(Core.Note.E, 350),
                                        new SongNote(Core.Note.E, 350),
                                        new SongNote(Core.Note.D, 350),
                                        new SongNote(Core.Note.D, 350),
                                        new SongNote(Core.Note.E, 350),
                                        new SongNote(Core.Note.D, 700),
                                        new SongNote(Core.Note.G, 700)
                                    });

        public LiveControlForm()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            using (var serial = new SerialService())
            {

                var afterMoveRest = 300;
                var afterNoteRest = 250;
                foreach (var note in _jingleBells.SongNotes)
                {
                    serial.SendMessage($"{note.Note.Position}m");
                    Thread.Sleep(afterMoveRest);
                    serial.SendMessage("p");
                    afterNoteRest = Math.Max(150, note.Length / 2);
                    afterMoveRest = note.Length - afterNoteRest;
                    Thread.Sleep(afterNoteRest);
                }
            }
        }

        private void button2_Click(object sender, EventArgs e)
        {
            using (var serial = new SerialService())
            {
                serial.SendMessage($"{txtNote.Text}m");
                Thread.Sleep(250);
                serial.SendMessage("p");

                // TODO: need to convert the above into MIDI/library friendly code
                //throw new NotImplementedException("Sorry, still working on converting to MIDI.");
            }
        }
    }
}
