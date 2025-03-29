using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace DevToolbox.Services.Services
{
    public class CardStateChangedEventArgs : EventArgs
    {
        public string CardType { get; }
        public string CardId { get; }

        public CardStateChangedEventArgs(string cardType, string cardId)
        {
            CardType = cardType;
            CardId = cardId;
        }
    }

    public class CardStateService
    {
        private readonly ConcurrentDictionary<string, bool> _expandedStates = new();

        public event EventHandler<CardStateChangedEventArgs> OnStateChanged = delegate { };

        public void ToggleExpand(string type, string id)
        {
            var key = $"{type}_{id}";
            var isExpanded = _expandedStates.GetValueOrDefault(key, false);
            
            // If expanded and not focused, collapse on click
            if (isExpanded)
            {
                _expandedStates[key] = false;
                OnStateChanged.Invoke(this, new CardStateChangedEventArgs(type, string.Empty));
                return;
            }

            // Otherwise toggle normally
            _expandedStates[key] = !isExpanded;
            OnStateChanged.Invoke(this, new CardStateChangedEventArgs(type, isExpanded ? string.Empty : id));
        }

        public bool IsExpanded(string type, string id)
        {
            return _expandedStates.GetValueOrDefault($"{type}_{id}", false);
        }
    }
} 