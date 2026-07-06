using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Globalization;
using System.Windows;

namespace BIMassist.Views
{
    public partial class ElementCoordinatesWindow : Window
    {
        private readonly ExternalEvent _externalEvent;
        private readonly IElementCoordinatesRequestHandler _handler;

        public ElementCoordinatesWindow(IElementCoordinatesRequestHandler handler, ExternalEvent externalEvent)
        {
            _handler = handler;
            _externalEvent = externalEvent;
            InitializeComponent();
            ClearElement("Kein Element geladen. Bitte 'Anderes Element auswählen' verwenden oder die Funktion mit selektierter ladbarer Familie/Projektfamilie starten.");
        }

        public double TargetEastingMeters { get; private set; }
        public double TargetNorthingMeters { get; private set; }
        public bool CloseAfterApply { get; private set; }

        public void SetElementData(ElementId elementId, string elementLabel, double currentEastingMeters, double currentNorthingMeters)
        {
            ElementText.Text = $"{elementLabel} (Id {elementId.Value})";
            string eastingText = FormatCoordinate(currentEastingMeters);
            string northingText = FormatCoordinate(currentNorthingMeters);
            CurrentEastingText.Text = eastingText + " m";
            CurrentNorthingText.Text = northingText + " m";
            EastingBox.Text = eastingText;
            NorthingBox.Text = northingText;
            StatusText.Text = "Element geladen. Neue Koordinaten eingeben und 'Neue Koordinaten setzen' klicken.";
            EastingBox.Focus();
            EastingBox.SelectAll();
        }

        public void UpdateCurrentCoordinates(double currentEastingMeters, double currentNorthingMeters)
        {
            string eastingText = FormatCoordinate(currentEastingMeters);
            string northingText = FormatCoordinate(currentNorthingMeters);
            CurrentEastingText.Text = eastingText + " m";
            CurrentNorthingText.Text = northingText + " m";
            EastingBox.Text = eastingText;
            NorthingBox.Text = northingText;
            StatusText.Text = "Element wurde verschoben. Fenster bleibt geöffnet.";
        }

        public void ClearElement(string status)
        {
            ElementText.Text = "-";
            CurrentEastingText.Text = "-";
            CurrentNorthingText.Text = "-";
            EastingBox.Text = string.Empty;
            NorthingBox.Text = string.Empty;
            StatusText.Text = status;
        }

        public void SetStatus(string status)
        {
            StatusText.Text = status;
        }

        private static string FormatCoordinate(double value)
        {
            return value.ToString("F3", CultureInfo.CurrentCulture);
        }

        private static bool TryParseCoordinate(string text, out double value)
        {
            text = (text ?? string.Empty).Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                return true;

            return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private bool TryReadTargetCoordinates()
        {
            if (!TryParseCoordinate(EastingBox.Text, out double easting))
            {
                System.Windows.MessageBox.Show(this, "Bitte einen gültigen Rechtswert eingeben.", "Elementkoordinaten", MessageBoxButton.OK, MessageBoxImage.Warning);
                EastingBox.Focus();
                EastingBox.SelectAll();
                return false;
            }

            if (!TryParseCoordinate(NorthingBox.Text, out double northing))
            {
                System.Windows.MessageBox.Show(this, "Bitte einen gültigen Hochwert eingeben.", "Elementkoordinaten", MessageBoxButton.OK, MessageBoxImage.Warning);
                NorthingBox.Focus();
                NorthingBox.SelectAll();
                return false;
            }

            TargetEastingMeters = easting;
            TargetNorthingMeters = northing;
            return true;
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            if (!TryReadTargetCoordinates())
                return;

            CloseAfterApply = false;
            _handler.RequestApply(this);
            _externalEvent.Raise();
        }

        private void OnApplyAndCloseClick(object sender, RoutedEventArgs e)
        {
            if (!TryReadTargetCoordinates())
                return;

            CloseAfterApply = true;
            _handler.RequestApply(this);
            _externalEvent.Raise();
        }

        private void OnSelectElementClick(object sender, RoutedEventArgs e)
        {
            _handler.RequestSelect(this);
            _externalEvent.Raise();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    public interface IElementCoordinatesRequestHandler
    {
        void RequestApply(ElementCoordinatesWindow window);
        void RequestSelect(ElementCoordinatesWindow window);
    }
}
