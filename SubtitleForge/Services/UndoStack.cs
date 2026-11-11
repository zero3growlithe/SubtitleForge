using System;
using System.Collections.Generic;

namespace SubtitleForge.Services
{
    public interface IUndoableAction
    {
        string Description { get; }

        void Undo();
    }

    /// <summary>
    /// Undo entry built from a delegate — track edits are cheap to describe as
    /// "put this value back", which keeps the history memory-light.
    /// </summary>
    public class DelegateUndoAction : IUndoableAction
    {
        private readonly Action undo;

        public DelegateUndoAction(string description, Action undo)
        {
            Description = description;
            this.undo = undo;
        }

        public string Description { get; }

        public void Undo()
        {
            undo();
        }
    }

    public class UndoStack
    {
        private readonly List<IUndoableAction> items = new List<IUndoableAction>();

        public event EventHandler? Changed;

        public bool CanUndo
        {
            get { return items.Count > 0; }
        }

        public int Count
        {
            get { return items.Count; }
        }

        public string NextDescription
        {
            get { return items.Count == 0 ? string.Empty : items[items.Count - 1].Description; }
        }

        public void Push(IUndoableAction action)
        {
            items.Add(action);
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
            RaiseChanged();
            return true;
        }

        public void Clear()
        {
            if (items.Count == 0)
            {
                return;
            }

            items.Clear();
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
