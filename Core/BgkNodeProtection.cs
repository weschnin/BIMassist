using System.Collections.Generic;

namespace BIMassist.Core
{
    internal static class BgkNodeProtection
    {
        internal static void ApplyToSubtree(Baugruppe? root, bool protect)
        {
            if (root == null) return;

            var pending = new Stack<Baugruppe>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                node.NodeProtection = protect;
                foreach (var child in node.Baugruppen)
                    pending.Push(child);
            }
            root.NotifyEffectiveProtectionChangedForSubtree();
        }

        internal static bool CanEdit(Baugruppe? node)
        {
            if (node == null) return false;
            for (var current = node; current != null; current = current.ParentNode)
            {
                if (current.NodeProtection) return false;
            }
            return true;
        }

        internal static bool CanChangeProtection(Baugruppe? node) =>
            node != null && (node.ParentNode == null || CanEdit(node.ParentNode));

        internal static bool CanMoveOrDelete(Baugruppe? node)
        {
            if (!CanEdit(node)) return false;

            var pending = new Stack<Baugruppe>();
            pending.Push(node!);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current.NodeProtection) return false;
                foreach (var child in current.Baugruppen)
                    pending.Push(child);
            }
            return true;
        }
    }
}
