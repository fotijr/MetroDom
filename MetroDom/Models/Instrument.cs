using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MetroDom.Models
{
    public sealed class Instrument
    {
        public readonly string Name;
        public readonly char Code;

        private Instrument(string name, char code)
        {
            Name = name;
            Code = code;
        }

        public static Instrument BassDrum = new Instrument("Bass Drum", 'b');
        public static Instrument SnareDrum = new Instrument("Snare Drum", 's');
        public static Instrument HighHatHit = new Instrument("High Hat Hit", 'h');
    }
}
