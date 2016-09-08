using MetroDom.Conductor.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace MetroDom.Conductor.Forms
{
    public partial class LiveControlForm : Form
    {
        List<SongNote> _jingleBells = new List<SongNote>
                                    {
                                        new SongNote(Note.E, 350),
                                        new SongNote(Note.E, 350),
                                        new SongNote(Note.E, 700),
                                        new SongNote(Note.E, 350),
                                        new SongNote(Note.E, 350),
                                        new SongNote(Note.E, 700),
                                        new SongNote(Note.E, 350),
                                        new SongNote(Note.G, 350),
                                        new SongNote(Note.C, 350),
                                        new SongNote(Note.D, 350),
                                        new SongNote(Note.E, 1050),
                                        new SongNote(Note.F, 350),
                                        new SongNote(Note.F, 350),
                                        new SongNote(Note.F, 350),
                                        new SongNote(Note.F, 350),
                                        new SongNote(Note.E, 350),
                                        new SongNote(Note.E, 350),
                                        new SongNote(Note.E, 350),
                                        new SongNote(Note.E, 350),
                                        new SongNote(Note.E, 350),
                                        new SongNote(Note.D, 350),
                                        new SongNote(Note.D, 350),
                                        new SongNote(Note.E, 350),
                                        new SongNote(Note.D, 700),
                                        new SongNote(Note.G, 700)
                                    };

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
                foreach (var note in _jingleBells)
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
            }
        }
    }

    public class SongNote
    {
        public Note Note;
        public int Length;

        // a - 22

        public SongNote(Note note, int length)
        {
            Note = note;
            Length = length;
        }
    }

    public sealed class Note
    {
        public string Letter;
        public int Position;

        public static Note A = new Note("A", 22);
        public static Note B = new Note("B", 40);
        public static Note C = new Note("C", 55);
        public static Note D = new Note("D", 64);
        public static Note E = new Note("E", 75);
        public static Note F = new Note("F", 92);
        public static Note G = new Note("G", 103);
        public static Note AHigh = new Note("A", 112);
        public static Note BHigh = new Note("B", 127);
        public static Note CHigh = new Note("C", 143);

        private Note(string letter, int position)
        {
            Letter = letter;
            Position = position;
        }
    }
}
