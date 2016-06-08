using System.Timers;
using System.Windows;
using System.Windows.Threading;
using MetroDom.Models;

namespace MetroDom
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        
        bool _running = false;
        DispatcherTimer _dispatcherTimer = new DispatcherTimer();
        Timer _timer = new Timer();
        short _measure = 1;
        short _noteLength = 16;
        DrumMachine _drums;

        public MainWindow()
        {
            InitializeComponent();
            _drums = new DrumMachine();
            this.DataContext = _drums;
        }

        private void btnStartStop_Click(object sender, RoutedEventArgs e)
        {
            if (!_drums.Running)
            {
                short bpm;
                short.TryParse(txtBpm.Text, out bpm);
                _drums.Start(bpm);
                btnStartStop.Content = "Stop";
            }
            else
            {
                _drums.Stop();
                btnStartStop.Content = "Start";
            }
        }
    }
}
