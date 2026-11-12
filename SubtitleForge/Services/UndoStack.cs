using System;
using System.Collections.Generic;

namespace SubtitleForge.Services
{
    public interface IUndoableAction
    {
        string Description { get; }

        bool CanRedo { get; }

        void Undo();

        void Redo();
    }

    /// <summary>
    /// Undo entry built from delegates — track edits are cheap to describe as
    /// "put this value back", which keeps the history memory-light. The redo delegate is
    /// optional: entries that only know how to roll back cannot be redone.
    /// </summary>
    public class DelegateUndoAction : IUndoableAction
    {
        private readonly Action undo;
        private readonly Action? redo;

        public DelegateUndoAction(string description, Action undo, Action? redo = null)
        {
            Description = description;
            this.undo = undo;
            this.redo = redo;
        }

        public string Description { get; }

        public bool CanRedo
        {
            get { return redo != null; }
        }

        public void Undo()
        {
            undo();
        }

        public void Redo()
        {
            if (redo != null)
            {
                redo();
            }
        }
    }

    /// <summary>
    /// Two-stack history: undone entries move to the redo stack, and any new edit drops
    /// everything on it, which is the behaviour every editor is expected to have.
    /// </summary>
    public class UndoStack
    {
        private readonly List<IUndoableAction> items = new List<IUndoableAction>();
        private readonly List<IUndoableAction> redoItems = new List<IUndoableAction>();

        public event EventHandler? Changed;

        public bool CanUndo
        {
            get { return items.Count > 0; }
        }

        public bool CanRedo
        {
            get { return redoItems.Count > 0; }
        }

        public int Count
        {
            get { return items.Count; }
        }

        public int RedoCount
        {
            get { return redoItems.Count; }
        }

        public string NextDescription
        {
            get { return items.Count == 0 ? string.Empty : items[items.Count - 1].Description; }
        }

        public string NextRedoDescription
        {
            get { return redoItems.Count == 0 ? string.Empty : redoItems[redoItems.Count - 1].Description; }
        }

        public void Push(IUndoableAction action)
        {
            items.Add(action);
            if (redoItems.Count > 0)
            {
                redoItems.Clear();
            }

            RaiseChanged();
        }

        public bool TryUndo(out string description)
        {
            description = string.Empty;
            if (items.Count == 0)
            {
                return false;
            }

            IUndoableAction action = items[items.Count - 1];
            items.RemoveAt(items.Count - 1);
            description = action.Description;
            action.Undo();
            if (action.CanRedo)
            {
                redoItems.Add(action);
            }

            RaiseChanged();
            return true;
        }

        public bool TryRedo(out string description)
        {
            description = string.Empty;
            if (redoItems.Count == 0)
            {
                return false;
            }

            IUndoableAction action = redoItems[redoItems.Count - 1];
            redoItems.RemoveAt(redoItems.Count - 1);
            description = action.Description;
            action.Redo();
            items.Add(action);
            RaiseChanged();
            return true;
        }

        public void Clear()
        {
            if (items.Count == 0 && redoItems.Count == 0)
            {
                return;
            }

            items.Clear();
            redoItems.Clear();
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
