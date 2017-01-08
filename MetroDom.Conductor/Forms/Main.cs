using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace MetroDom.Conductor.Forms
{
    public partial class Main : Form
    {
        private List<Form> _openForms = new List<Form>();
        Form _currentForm;

        public Main()
        {
            InitializeComponent();
        }
        private void Main_Load(object sender, EventArgs e)
        {
            // open default form
            OpenForm<LiveControlForm>();
        }

        private void toolStripButton2_Click(object sender, EventArgs e)
        {
            Application.Exit();
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
            var openForm = _openForms.FirstOrDefault(f => f.GetType() == typeof(T));
            if (openForm != null)
            {
                _currentForm?.Hide();
                openForm.Show();
                openForm.BringToFront();
                return;
            }

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
            //form.MinimumSize = form.Size;
            //form.MaximumSize = form.Size;
            form.Show();
            form.WindowState = FormWindowState.Maximized;
            _openForms.Add(form);
            _currentForm = form;
        }
    }
}
