using Autodesk.Revit.DB;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace SpoolTools.UI
{
    public partial class LeaderSettingsDialog : Window
    {
        public LeaderSettingsVm Vm { get; }

        public LeaderSettingsDialog(bool placeLeader, LeaderEndCondition end,
            double leaderLengthFt, double tagOffsetInches)
        {
            InitializeComponent();
            Vm = new LeaderSettingsVm
            {
                PlaceLeader      = placeLeader,
                LeaderAttached   = end == LeaderEndCondition.Attached,
                LeaderFree       = end == LeaderEndCondition.Free,
                LeaderLengthText = leaderLengthFt > 0
                    ? (leaderLengthFt * 12.0).ToString("0.##")
                    : string.Empty,
                TagOffsetText = (tagOffsetInches > 0 ? tagOffsetInches : 1.0).ToString("0.##"),
            };
            DataContext = Vm;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public sealed class LeaderSettingsVm : INotifyPropertyChanged
    {
        private bool _placeLeader;
        public bool PlaceLeader
        {
            get => _placeLeader;
            set
            {
                if (_placeLeader != value)
                {
                    _placeLeader = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowLeaderLength));
                    OnPropertyChanged(nameof(ShowTagOffset));
                }
            }
        }

        private bool _leaderAttached = true;
        public bool LeaderAttached
        {
            get => _leaderAttached;
            set
            {
                if (_leaderAttached != value)
                {
                    _leaderAttached = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowLeaderLength));
                }
            }
        }

        private bool _leaderFree;
        public bool LeaderFree
        {
            get => _leaderFree;
            set
            {
                if (_leaderFree != value)
                {
                    _leaderFree = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowLeaderLength));
                }
            }
        }

        private string _leaderLengthText = string.Empty;
        public string LeaderLengthText
        {
            get => _leaderLengthText;
            set { if (_leaderLengthText != value) { _leaderLengthText = value ?? string.Empty; OnPropertyChanged(); } }
        }

        private string _tagOffsetText = "1";
        public string TagOffsetText
        {
            get => _tagOffsetText;
            set { if (_tagOffsetText != value) { _tagOffsetText = value ?? "1"; OnPropertyChanged(); } }
        }

        public bool ShowLeaderLength => _placeLeader && _leaderFree;
        public bool ShowTagOffset    => _placeLeader;

        public LeaderEndCondition LeaderEnd =>
            _leaderFree ? LeaderEndCondition.Free : LeaderEndCondition.Attached;

        public double LeaderLengthFt
        {
            get
            {
                if (double.TryParse(_leaderLengthText, out var inches) && inches > 0)
                    return inches / 12.0;
                return 0.0;
            }
        }

        public double TagOffsetInches
        {
            get
            {
                if (double.TryParse(_tagOffsetText, out var inches) && inches > 0)
                    return inches;
                return 1.0;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
