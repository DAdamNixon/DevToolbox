using System;
using System.Collections.Generic;

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
        private readonly Dictionary<string, string> _expandedCards = new();

        public event EventHandler<CardStateChangedEventArgs> OnStateChanged = delegate { };

        public void ToggleExpand(string cardType, string cardId)
        {
            if (_expandedCards.ContainsKey(cardType))
            {
                if (_expandedCards[cardType] == cardId)
                {
                    // If the same card is clicked, collapse it
                    _expandedCards.Remove(cardType);
                    OnStateChanged.Invoke(this, new CardStateChangedEventArgs(cardType, string.Empty));
                }
                else
                {
                    // If a different card is clicked, expand it
                    _expandedCards[cardType] = cardId;
                    OnStateChanged.Invoke(this, new CardStateChangedEventArgs(cardType, cardId));
                }
            }
            else
            {
                // If no card is expanded, expand this one
                _expandedCards[cardType] = cardId;
                OnStateChanged.Invoke(this, new CardStateChangedEventArgs(cardType, cardId));
            }
        }

        public bool IsExpanded(string cardType, string cardId)
        {
            return _expandedCards.ContainsKey(cardType) && _expandedCards[cardType] == cardId;
        }
    }
} 