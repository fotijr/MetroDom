using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MetroDom.Core
{
    public class Song
    {
        public Song(List<SongNote> songNotes)
        {
            SongNotes = songNotes;
        }

        /// <summary>
        /// Sequential list of song notes
        /// </summary>
        public List<SongNote> SongNotes { get; set; }
    }


    // TODO: everything below is a temporary hack. Convert to MIDI and fix this.

    public class SongNote
    {
        public Note Note;
        public int Length;

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
