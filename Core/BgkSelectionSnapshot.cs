using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMassist.Core
{
    /// <summary>
    /// Merkt die letzte Projektauswahl, wenn Revit beim Fokuswechsel zum BGK-Fenster
    /// die aktive Selektion entfernt. Eine explizite Abwahl im Modell verwirft sie.
    /// </summary>
    internal sealed class BgkSelectionSnapshot<TDocument, TId> where TDocument : class
    {
        private TDocument? _document;
        private IReadOnlyList<TId> _ids = Array.Empty<TId>();

        internal void Update(TDocument document, IEnumerable<TId> selectedIds, bool paneHasFocus)
        {
            if (_document == null || !_document.Equals(document))
            {
                _document = document;
                _ids = Array.Empty<TId>();
            }
            TId[] current = selectedIds.Distinct().ToArray();
            if (current.Length > 0 || !paneHasFocus)
                _ids = current;
        }

        internal IReadOnlyList<TId> Resolve(TDocument document, IEnumerable<TId> selectedIds)
        {
            TId[] current = selectedIds.Distinct().ToArray();
            if (current.Length > 0)
                return current;
            return _document != null && _document.Equals(document) ? _ids : Array.Empty<TId>();
        }

        internal void Clear()
        {
            _document = null;
            _ids = Array.Empty<TId>();
        }
    }
}
