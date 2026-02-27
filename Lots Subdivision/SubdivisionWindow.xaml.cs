using System;
using System.Windows;
using System.Windows.Controls;

namespace Lots_Subdivision
{
    public partial class SubdivisionWindow : Window
    {
        public class SubdivisionData
        {
            public int ModeIndex { get; set; }
            public double TargetArea { get; set; }
            public double Width { get; set; }
            public double Depth { get; set; }
            public double Angle { get; set; }
            public int NumLots { get; set; }
            public double MinWidth { get; set; }
        }

        public SubdivisionData? SelectionData { get; private set; }

        public SubdivisionWindow()
        {
            InitializeComponent();
            ModeCombo.SelectionChanged += ModeCombo_SelectionChanged;
        }

        private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BatchPanel == null || PriorityPanel == null) return;

            // Toggle Panels based on selection
            BatchPanel.Visibility = (ModeCombo.SelectedIndex == 3) ? Visibility.Visible : Visibility.Collapsed;
            PriorityPanel.Visibility = (ModeCombo.SelectedIndex == 6) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SelectionData = new SubdivisionData
                {
                    ModeIndex = ModeCombo.SelectedIndex,
                    TargetArea = double.Parse(TxtArea.Text),
                    Width = double.Parse(TxtWidth.Text),
                    Depth = double.Parse(TxtDepth.Text),
                    Angle = double.Parse(TxtAngle.Text) * (Math.PI / 180.0), // Deg to Rad
                    NumLots = int.Parse(TxtLots.Text),
                    MinWidth = double.Parse(TxtMinWidth.Text)
                };

                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Please ensure all fields are valid numbers.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
