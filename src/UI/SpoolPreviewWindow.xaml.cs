using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SpoolTools.Revit.Spooling;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace SpoolTools.UI
{
    /// <summary>
    /// Modeless preview window. Owns the live <see cref="SpoolPreviewSession"/>
    /// for its lifetime — Accept assimilates, Discard rolls back. Window-close
    /// without explicit choice treats as Discard so the open TransactionGroup
    /// never leaks.
    /// </summary>
    public partial class SpoolPreviewWindow : Window
    {
        private readonly UIDocument _uiDoc;
        private SpoolPreviewSession? _session;
        private PreviewControl? _previewControl;

        // Set true once the user explicitly clicks Accept or Discard so the
        // Closed handler doesn't trigger a second finalize.
        private bool _decided;

        public SpoolPreviewVm ViewModel { get; }

        /// <summary>Fires after the session has been finalized. Bool: true if
        /// the user accepted, false if they discarded (or X-closed).</summary>
        public event Action<bool>? Decided;

        public SpoolPreviewWindow(UIDocument uiDoc, SpoolPreviewSession session)
        {
            InitializeComponent();
            _uiDoc      = uiDoc;
            _session    = session;
            ViewModel   = new SpoolPreviewVm
            {
                HintText  = "Pan / zoom in the preview to inspect the spool sheet. " +
                            "Click Accept to keep it or Discard to roll back the entire spool " +
                            "(sheet + views + parameter changes + tags) as one undo step.",
                StatusText = session.Result.Message,
            };
            DataContext = ViewModel;

            Loaded += (_, _) => AttachPreview();
            Closed += (_, _) => OnClosed();
        }

        private void AttachPreview()
        {
            if (_session?.SheetId == null) return;
            try
            {
                _previewControl  = new PreviewControl(_uiDoc.Document, _session.SheetId);
                PreviewHost.Child = _previewControl;

                // Zoom to fit so the entire titleblock is visible. The
                // PreviewControl's internal UIView is only populated AFTER
                // the WPF Loaded event fires — calling ZoomToFit any
                // earlier returns null. Hook the event; if it already
                // fired (rare with this construction order), bounce
                // through the dispatcher as a fallback.
                _previewControl.Loaded += (_, _) => TryZoomToFit();
                Dispatcher.BeginInvoke(
                    new Action(TryZoomToFit),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                PreviewHost.Child = new System.Windows.Controls.TextBlock
                {
                    Text = "Preview unavailable: " + ex.Message,
                    Foreground = System.Windows.Media.Brushes.OrangeRed,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(20),
                };
            }
        }

        private void TryZoomToFit()
        {
            try { _previewControl?.UIView?.ZoomToFit(); } catch { /* not ready yet */ }
        }

        private void DetachPreview()
        {
            if (_previewControl == null) return;
            try { PreviewHost.Child = null; } catch { }
            try { _previewControl.Dispose(); } catch { }
            _previewControl = null;
        }

        private void Accept_Click(object sender, RoutedEventArgs e) => Finalize(accept: true);
        private void Discard_Click(object sender, RoutedEventArgs e) => Finalize(accept: false);

        private void Finalize(bool accept)
        {
            if (_decided) return;
            _decided = true;

            // Hide the window immediately so the user gets visual feedback
            // the moment they click Accept/Discard. Without this, the
            // window stays visible while Decided?.Invoke runs (which can
            // block on a TaskDialog success notification) — Close() only
            // runs AFTER that returns, so the window lingered until the
            // notification was dismissed. Close() at the end still
            // finalizes the destruction; Hide() just decouples visibility
            // from that finalization.
            Hide();

            DetachPreview();
            var session = _session;
            _session = null;

            // Assimilate / Rollback must run on the Revit thread — bounce
            // through SpoolEvent.
            SpoolToolsApp.SpoolHandler!.SetAction(_ =>
            {
                try
                {
                    if (accept) session?.Accept(); else session?.Discard();
                }
                catch { /* defensive — session may already be finalized */ }
                finally { session?.Dispose(); }

                Dispatcher.Invoke(() =>
                {
                    Decided?.Invoke(accept);
                    Close();
                });
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }

        private void OnClosed()
        {
            // Window closed without an explicit decision (X button, Alt-F4,
            // host crash recovery, etc.) — treat as Discard so the open
            // TransactionGroup doesn't leak.
            if (_decided) return;
            _decided = true;

            DetachPreview();
            var session = _session;
            _session = null;

            SpoolToolsApp.SpoolHandler!.SetAction(_ =>
            {
                try { session?.Discard(); } catch { }
                finally { session?.Dispose(); }
                Dispatcher.Invoke(() => Decided?.Invoke(false));
            });
            SpoolToolsApp.SpoolEvent!.Raise();
        }
    }

    public sealed class SpoolPreviewVm : INotifyPropertyChanged
    {
        private string _hintText = string.Empty;
        public string HintText
        {
            get => _hintText;
            set { _hintText = value ?? string.Empty; OnPropertyChanged(); }
        }

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
