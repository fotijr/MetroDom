using Sanford.Multimedia.Midi;

namespace MetroDom.Core.Interfaces
{
    public interface IInstrument
    {
        /// <summary>
        /// Name of instrument.
        /// </summary>
        string Name { get; set; }

        /// <summary>
        /// Description of instrument.
        /// </summary>
        string Description { get; set; }

        /// <summary>
        /// Stops all music being played.
        /// </summary>
        void Stop();

        /// <summary>
        /// Verifies instrument hardware or software is connected.
        /// </summary>
        /// <returns></returns>
        bool VerifyInstrumentConnected();

        /// <summary>
        /// Loads song to instrument controller.
        /// </summary>
        void LoadSong();

        /// <summary>
        /// Plays song.
        /// </summary>
        /// <param name="song"></param>
        void PlaySong(Song song);

        /// <summary>
        /// Plays specific note.
        /// </summary>
        /// <param name="note"></param>
        void PlayNote(IMidiMessage note);

        // TODO: add some kind of aligning or configuring? (servo start/stop positions, etc)
    }
}
