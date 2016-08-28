using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MetroDom.Conductor
{
    public partial class DrumPanelForm : Form
    {
        private Dictionary<Point, bool> _padState = new Dictionary<Point, bool>();

        public DrumPanelForm()
        {
            InitializeComponent();
            LoadDrumPanels();
        }

        public void LoadDrumPanels()
        {
            var beats = 8;
            var drums = 3;
            for (int r = 1; r <= drums; r++)
            {
                for (int i = 1; i <= beats; i++)
                {
                    var drumPad = new Panel()
                    {
                        BackColor = Color.Black,
                        Dock = DockStyle.Fill,
                        Size = new Size(135, 151)
                    };
                    var currentPoint = new Point(i, r);
                    this.drumPanelLayoutTable.Controls.Add(drumPad, i, r);
                    drumPad.Click += (sender, e) => PadClick(sender, e, currentPoint);
                    _padState.Add(currentPoint, false);
                    Debug.WriteLine(currentPoint);
                }
            }
        }

        private void PadClick(object sender, EventArgs e, Point point)
        {
            Panel panel = (Panel)sender;
            bool currentState = !_padState[point]; // get current value from dictionary, then flip it for new value
            panel.BackColor = currentState ? Color.DarkOrange : Color.Black;
            _padState[point] = currentState;
            Debug.WriteLine("{0}: {1}", point, currentState);
        }
    }
}