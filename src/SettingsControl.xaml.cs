using System.Windows.Controls;

namespace Flow.Launcher.Plugin.SpeedTest
{
    public partial class SettingsControl : UserControl
    {
        private readonly PluginInitContext _context;
        private Settings _settings;

        public SettingsControl(PluginInitContext context)
        {
            InitializeComponent();
            _context = context;
            _settings = context.API.LoadSettingJsonStorage<Settings>();
            MaxHistoryTextBox.Text = _settings.MaxHistoryEntries.ToString();
        }

        private void MaxHistoryTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (int.TryParse(MaxHistoryTextBox.Text, out int value) && value > 0)
            {
                _settings.MaxHistoryEntries = value;
                _context.API.SaveSettingJsonStorage<Settings>();
            }
        }
    }
}
