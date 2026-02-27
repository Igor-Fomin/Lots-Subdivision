using System;
using System.Windows;

namespace Lots_Subdivision
{
    public static class SubdivisionSettings
    {
        public static double TargetArea = 450.0;
        public static double Width = 15.0;
        public static double Depth = 30.0;
        public static double Angle = 90.0;
        public static bool LockArea = true;
        public static bool LockWidth = false;
        public static bool LockDepth = false;
    }

    public partial class SubdivisionWindow : Window
    {
        public class SubdivisionData
        {
            public bool LockArea { get; set; }
            public bool LockWidth { get; set; }
            public bool LockDepth { get; set; }
            public double TargetArea { get; set; }
            public double Width { get; set; }
            public double Depth { get; set; }
            public double Angle { get; set; }
        }

        public event EventHandler? OnPickParent;
        public event EventHandler? OnPickFrontage;
        public event EventHandler? OnPickAngle;
        public event Action<SubdivisionData>? OnExecute;

        public SubdivisionWindow()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            TxtArea.Text = SubdivisionSettings.TargetArea.ToString();
            TxtWidth.Text = SubdivisionSettings.Width.ToString();
            TxtDepth.Text = SubdivisionSettings.Depth.ToString();
            TxtAngle.Text = SubdivisionSettings.Angle.ToString();
            ChkLockArea.IsChecked = SubdivisionSettings.LockArea;
            ChkLockWidth.IsChecked = SubdivisionSettings.LockWidth;
            ChkLockDepth.IsChecked = SubdivisionSettings.LockDepth;
        }

        private void SaveSettings()
        {
            SubdivisionSettings.TargetArea = double.Parse(TxtArea.Text);
            SubdivisionSettings.Width = double.Parse(TxtWidth.Text);
            SubdivisionSettings.Depth = double.Parse(TxtDepth.Text);
            SubdivisionSettings.Angle = double.Parse(TxtAngle.Text);
            SubdivisionSettings.LockArea = ChkLockArea.IsChecked ?? false;
            SubdivisionSettings.LockWidth = ChkLockWidth.IsChecked ?? false;
            SubdivisionSettings.LockDepth = ChkLockDepth.IsChecked ?? false;
        }

        private void BtnPickParent_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            OnPickParent?.Invoke(this, EventArgs.Empty);
            this.Show();
        }

        private void BtnPickFrontage_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            OnPickFrontage?.Invoke(this, EventArgs.Empty);
            this.Show();
        }

        private void BtnPickAngle_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            OnPickAngle?.Invoke(this, EventArgs.Empty);
            this.Show();
        }

        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveSettings();
                var data = new SubdivisionData
                {
                    LockArea = SubdivisionSettings.LockArea,
                    LockWidth = SubdivisionSettings.LockWidth,
                    LockDepth = SubdivisionSettings.LockDepth,
                    TargetArea = SubdivisionSettings.TargetArea,
                    Width = SubdivisionSettings.Width,
                    Depth = SubdivisionSettings.Depth,
                    Angle = SubdivisionSettings.Angle * (Math.PI / 180.0)
                };
                OnExecute?.Invoke(data);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Please check your numeric inputs.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void UpdateStatus(string status) => TxtStatus.Text = status;
    }
}
