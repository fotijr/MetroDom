using System;
using System.Drawing;
using System.Windows.Forms;

namespace MetroDom.Conductor.Forms
{
    public partial class DrumMachineForm : Form
    {
        public DrumMachineForm()
        {
            InitializeComponent();
        }

        private void DrumMachineForm_Load(object sender, EventArgs e)
        {
            this.MaximizeBox = false;
            this.WindowState = FormWindowState.Maximized;

            var measures = 8;
            var drums = new string[] { "High Hat", "Snare", "Bass" };
            var cellSize = 80;

            var cellStyle = new DataGridViewCellStyle();
            cellStyle.BackColor = Color.Black;
            cellStyle.SelectionBackColor = Color.Transparent;
            cellStyle.SelectionForeColor = Color.Transparent;
            
            //dgvDrumPad.RowHeadersDefaultCellStyle = cellStyle;

            dgvDrumPad.EnableHeadersVisualStyles = false;

            for (int i = 0; i < measures; i++)
            {
                var measureCol = new DataGridViewButtonColumn();
                measureCol.FlatStyle = FlatStyle.Popup;
                measureCol.HeaderCell.Style = cellStyle;
              //  measureCol.DefaultCellStyle.ForeColor = Color.Red;
                measureCol.Width = cellSize;
                dgvDrumPad.Columns.Add(measureCol);
            }

            for (int j = 0; j < drums.Length; j++)
            {
                var drumRow = new DataGridViewRow();
                drumRow.DefaultCellStyle = cellStyle;
                drumRow.Height = cellSize;
                drumRow.HeaderCell.ToolTipText = drums[j];
                drumRow.HeaderCell.Style = cellStyle;
                dgvDrumPad.Rows.Add(drumRow);
            }
        }

        private void dgvDrumPad_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.ColumnIndex < 0) return;
            var cell = dgvDrumPad.Rows[e.RowIndex].Cells[e.ColumnIndex];
            if (cell.ErrorText.Length == 0)
            {
                // turn on beat
                cell.ErrorText = "1";
                cell.Style.BackColor = Color.DarkOrange;
            }
            else
            {
                // turn off beat
                cell.ErrorText = "";
                cell.Style.BackColor = Color.Silver;
            }

            dgvDrumPad.ClearSelection();
        }

        bool[] _painted = { false, false, false };

        private void dgvDrumPad_RowPostPaint(object sender, DataGridViewRowPostPaintEventArgs e)
        {
            //Convert the image to icon, in order to load it in the row header column
            Bitmap resource = null;
            //Set Image dimension - User's choice
            int iconHeight = 40;
            int iconWidth = 40;

            if (e.RowIndex == 0)
            {
                resource = Properties.Resources.HighHat;
            }
            else if (e.RowIndex == 1)
            {
                resource = Properties.Resources.Snare;
            }
            else if (e.RowIndex == 2)
            {
                resource = Properties.Resources.Bass;
            }

            if (resource == null) return;

            Icon instrumentImage = Icon.FromHandle(resource.GetHicon());
            Graphics graphics = e.Graphics;

            //Set x/y position - As the center of the RowHeaderCell
            int xPosition = e.RowBounds.X + (dgvDrumPad.RowHeadersWidth / 2) - 10;
            int yPosition = e.RowBounds.Y +
            ((dgvDrumPad.Rows[e.RowIndex].Height - iconHeight) / 2);

            Rectangle rectangle = new Rectangle(xPosition, yPosition, iconWidth, iconHeight);
            graphics.DrawIcon(instrumentImage, rectangle);
            _painted[e.RowIndex] = true;
        }

        private void toolStripContainer1_TopToolStripPanel_Click(object sender, EventArgs e)
        {

        }
    }
}
