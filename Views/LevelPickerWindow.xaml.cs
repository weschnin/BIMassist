using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace BIMassist.Views
{
    public partial class LevelPickerWindow : Window
    {
        private readonly ObservableCollection<LevelOption> _levels;
        private readonly ICollectionView _levelView;

        public ElementId? SelectedLevelId => (LevelListBox.SelectedItem as LevelOption)?.Id;

        public LevelPickerWindow(IEnumerable<LevelOption> levels, string prompt, ElementId? preferredLevelId = null)
        {
            InitializeComponent();

            PromptTextBlock.Text = string.IsNullOrWhiteSpace(prompt)
                ? "Ebene aus dem aktuellen Revit-Dokument auswählen"
                : prompt;

            _levels = new ObservableCollection<LevelOption>(
                (levels ?? Enumerable.Empty<LevelOption>())
                .Where(level => level?.Id != null && !string.IsNullOrWhiteSpace(level.Name))
                .GroupBy(level => level.Id.Value)
                .Select(group => group.First())
                .OrderBy(level => level.Name, NaturalStringComparer.Instance)
                .ThenBy(level => level.Elevation)
                .ToList());

            _levelView = CollectionViewSource.GetDefaultView(_levels);
            _levelView.Filter = FilterLevel;
            LevelListBox.ItemsSource = _levelView;

            Loaded += (_, __) =>
            {
                SearchTextBox.Focus();

                if (preferredLevelId != null)
                {
                    LevelOption? preferred = _levels.FirstOrDefault(level => level.Id == preferredLevelId);
                    if (preferred != null)
                    {
                        LevelListBox.SelectedItem = preferred;
                        LevelListBox.ScrollIntoView(preferred);
                        return;
                    }
                }

                if (_levels.Count > 0)
                    LevelListBox.SelectedIndex = 0;
            };
        }

        private bool FilterLevel(object item)
        {
            if (item is not LevelOption level)
                return false;

            string filter = SearchTextBox?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(filter))
                return true;

            return level.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                || level.ElevationText.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _levelView.Refresh();

            if (LevelListBox.SelectedItem == null && LevelListBox.Items.Count > 0)
                LevelListBox.SelectedIndex = 0;
        }

        private void LevelListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ConfirmSelection();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmSelection();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ConfirmSelection()
        {
            if (SelectedLevelId == null)
            {
                System.Windows.MessageBox.Show(this,
                    "Bitte zuerst eine Ebene auswählen.",
                    "Ebene auswählen",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
            Close();
        }

        private sealed class NaturalStringComparer : IComparer<string>
        {
            public static readonly NaturalStringComparer Instance = new NaturalStringComparer();

            public int Compare(string? x, string? y)
            {
                if (ReferenceEquals(x, y))
                    return 0;
                if (x == null)
                    return -1;
                if (y == null)
                    return 1;

                int ix = 0;
                int iy = 0;

                while (ix < x.Length && iy < y.Length)
                {
                    char cx = x[ix];
                    char cy = y[iy];

                    if (char.IsDigit(cx) && char.IsDigit(cy))
                    {
                        long nx = 0;
                        while (ix < x.Length && char.IsDigit(x[ix]))
                        {
                            nx = (nx * 10) + (x[ix] - '0');
                            ix++;
                        }

                        long ny = 0;
                        while (iy < y.Length && char.IsDigit(y[iy]))
                        {
                            ny = (ny * 10) + (y[iy] - '0');
                            iy++;
                        }

                        int numberCompare = nx.CompareTo(ny);
                        if (numberCompare != 0)
                            return numberCompare;

                        continue;
                    }

                    int charCompare = char.ToUpperInvariant(cx).CompareTo(char.ToUpperInvariant(cy));
                    if (charCompare != 0)
                        return charCompare;

                    ix++;
                    iy++;
                }

                return x.Length.CompareTo(y.Length);
            }
        }

        public sealed class LevelOption
        {
            public LevelOption(ElementId id, string name, double elevation, string elevationText)
            {
                Id = id;
                Name = name;
                Elevation = elevation;
                ElevationText = elevationText;
            }

            public ElementId Id { get; }
            public string Name { get; }
            public double Elevation { get; }
            public string ElevationText { get; }
        }
    }
}
