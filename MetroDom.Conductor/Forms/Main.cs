using System;
using System.Windows.Forms;

namespace MetroDom.Conductor.Forms
{
    public partial class Main : Form
    {
        public Main()
        {
            InitializeComponent();
        }
        private void Main_Load(object sender, EventArgs e)
        {
            // open default form
            OpenForm<Explorer>();
        }

        private void toolStripButton2_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void toolStripButton1_Click(object sender, EventArgs e)
        {
            OpenForm<DrumPanelForm>();
        }

        private void toolStripButton3_Click(object sender, EventArgs e)
        {
            OpenForm<DrumPanelForm>();
        }

        private void btnLive_Click(object sender, EventArgs e)
        {
            OpenForm<LiveControlForm>();
        }

        private void toolStripButton4_Click(object sender, EventArgs e)
        {
            OpenForm<Explorer>();
        }

        private void OpenForm<T>() where T:Form, new()
        {
            MdiClient ctlMDI;

            // Loop through all of the form's controls looking
            // for the control of type MdiClient.
            foreach (Control ctl in this.Controls)
            {
                try
                {
                    // Attempt to cast the control to type MdiClient.
                    ctlMDI = (MdiClient)ctl;

                    // Set the BackColor of the MdiClient control.
                    ctlMDI.BackColor = this.BackColor;
                }
                catch (InvalidCastException exc)
                {
                    // Catch and ignore the error if casting failed.
                }
            }

            var form = new T();
            form.MdiParent = this;
            form.Show();
        }
    }
}
