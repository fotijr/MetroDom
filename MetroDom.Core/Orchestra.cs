using MetroDom.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MetroDom.Core
{
    public class Orchestra
    {
        private ICollection<IInstrument> _instruments;

        public Orchestra(ICollection<IInstrument> instruments)
        {
            _instruments = instruments;
        }

        /// <summary>
        /// Start playing song with all instruments.
        /// </summary>
        /// <param name="song"></param>
        public void Start(Song song)
        {
            if (song == null) throw new ArgumentNullException("No song provided.");
            if (_instruments == null || _instruments.Count == 0) return;

            // TODO: actually play song here

        }
    }
}
