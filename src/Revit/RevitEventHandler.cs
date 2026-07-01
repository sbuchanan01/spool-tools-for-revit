using Autodesk.Revit.UI;
using System;

namespace SpoolTools.Revit
{
    /// <summary>
    /// Generic external event handler that lets a modeless WPF dialog safely
    /// call back into the Revit API. Store an action via
    /// <see cref="SetAction"/>, then call <see cref="ExternalEvent.Raise"/> —
    /// Revit invokes <see cref="Execute"/> on the main thread.
    /// </summary>
    public class RevitEventHandler : IExternalEventHandler
    {
        private volatile Action<UIApplication>? _action;

        public void SetAction(Action<UIApplication> action) => _action = action;

        public void Execute(UIApplication app)
        {
            var action = _action;
            _action = null;
            action?.Invoke(app);
        }

        public string GetName() => "Hanger Layout";
    }
}
