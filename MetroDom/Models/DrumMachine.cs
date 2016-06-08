using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace MetroDom.Models
{
    class DrumMachine
    {
        //Timer _timer = new Timer();
        short _measure = 0;
        short _timeSignature = 4;
        short _noteLength = 8;
        short _bpm = 60;
        public bool Running = false;
        int _msBetweenNotes = 1000;
        bool _metronomeOn = true;
        public List<string> Notes = new List<string> { "Beat 1", "Beat 2", "Beat 3", "Beat 4" };
        BackgroundWorker _bgwBeat = new BackgroundWorker();
        Conductor _conductor;

        public DrumMachine()
        {
            _bgwBeat = new BackgroundWorker();
            _bgwBeat.DoWork += LoopBeat;
            //  _timer.Elapsed += new ElapsedEventHandler(Beat);
        }

        //public void Start(short bpm)
        //{
        //    var msPerSixteenthBeat = BpmToMillisecondsBetweenNotes(bpm, _noteLength);
        //    Debug.WriteLine("bpm: " + bpm);
        //    Debug.WriteLine("msPerBeat: " + msPerSixteenthBeat);
        //    _timer.Interval = msPerSixteenthBeat;
        //    _timer.Enabled = true;
        //    Running = true;
        //}

        public void Start(short bpm)
        {
            _bpm = bpm;
            _msBetweenNotes = BpmToMillisecondsBetweenNotes(bpm, _noteLength);
            _conductor = new Conductor();
            // var msPerSixteenthBeat = BpmToMillisecondsBetweenNotes(bpm, _noteLength);
            Debug.WriteLine("bpm: " + bpm);
            Debug.WriteLine("msPerBeat: " + _msBetweenNotes);
            Running = true;
            _bgwBeat.RunWorkerAsync();
        }

        public void Stop()
        {
            //    _timer.Enabled = false;
            Running = false;
            _measure = 0;
            _conductor.Dispose();
        }

        private void dispatcherTimer_Tick(object sender, EventArgs e)
        {
            Beat(null, null);
        }

        private void LoopBeat(object sender, DoWorkEventArgs e)
        {
            int beepFrequency = 600;
            var notesToPlay = new List<char>();
            do
            {
                beepFrequency = (_measure == 0) ? 800 : 600;

                notesToPlay = new List<char> { Instrument.HighHatHit.Code };
                if (_measure % 4 == 0)
                {
                    notesToPlay.Add(Instrument.BassDrum.Code);
                }
                else if (_measure == 2 || _measure == 6)
                {
                    notesToPlay.Add(Instrument.SnareDrum.Code);
                }
                _conductor.PlayNotes(notesToPlay);
               // Console.Beep(beepFrequency, 25);
                if (_measure < (_noteLength - 1))
                {
                    _measure++;
                }
                else {
                    _measure = 0;
                }
                Thread.Sleep(_msBetweenNotes);
            } while (Running);
        }

        private void Beat(object sender, ElapsedEventArgs e)
        {
            // Debug.WriteLine("Beat " + _measure);
            int tone = (_measure == 1) ? 800 : 600;

            Console.Beep(tone, 25);

            //if (_measure == 1 && _metronomeOn)
            //{
            //    Console.Beep(600, 25);
            //}

            //if (_measure % (_noteLength / 8) == 0)
            //{
            //    // eighth notes
            //}
            //if (_measure % (_noteLength / 4) == 0)
            //{
            //    // quarter notes

            //}m  
            //if (_measure % (_noteLength / 2) == 0)
            //{
            //    // half notes

            //}

            if (_measure < _noteLength)
            {
                _measure++;
            }
            else {
                _measure = 1;
            }
        }

        private int BpmToMillisecondsBetweenNotes(int bpm, short noteLength)
        {
            var crotchet = (1 / (double)noteLength) * _timeSignature;

            var blah = (crotchet * 1000) / ((double)bpm / 60);
            return (int)blah;

            double msBetweenNotes = (double)bpm / 60; // beats per second

            msBetweenNotes = 1000 / msBetweenNotes; // get milliseconds per beat

            msBetweenNotes = msBetweenNotes / noteLength; // get milliseconds per note

            //   return msBetweenNotes;
        }
    }
}
