using Sanford.Multimedia;
using System.Collections.Generic;

namespace MetroDom.Core
{
    public class Song
    {
        public Song(Key key, List<SongNote> songNotes)
        {
            SongKey = key;
            SongNotes = songNotes;
        }

        /// <summary>
        /// Sequential list of song notes
        /// </summary>
        public List<SongNote> SongNotes { get; set; }

        /// <summary>
        /// Key of the song
        /// </summary>
        public Key SongKey { get; set; }
    }


    // TODO: everything below is a temporary hack. Convert to MIDI and fix this.

    public class SongNote
    {
        public Core.Note Note;
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
