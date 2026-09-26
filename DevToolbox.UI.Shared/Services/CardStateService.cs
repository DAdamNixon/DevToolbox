using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;

namespace DevToolbox.UI.Services
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

        /// <summary>
        /// What a group card looks like before anyone has touched it — the "Expand groups by
        /// default" setting, loaded from ui_settings.yaml at startup.
        /// <para>
        /// A default only. Every toggle writes an entry into the dictionary, and an entry always
        /// wins, so changing this never overrules a card the user has already opened or closed.
        /// Workspace cards are deliberately unaffected: a group holds a handful of cards, and a
        /// card can hold a great many locations.
        /// </para>
        /// </summary>
        public bool DefaultGroupsExpanded { get; set; }

        public void ToggleExpand(string type, string id)
        {
            var key = $"{type}_{id}";

            // Default(type), not false: with "Expand groups by default" on, an untouched card is
            // already open, and reading it as closed made the first click on it set expanded=true
            // — a click that visibly did nothing.
            var isExpanded = _expandedStates.GetValueOrDefault(key, Default(type));
            
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
            return _expandedStates.GetValueOrDefault($"{type}_{id}", Default(type));
        }

        /// <summary>The state a card of this type starts in, having never been touched.</summary>
        private bool Default(string type) => type == "group" && DefaultGroupsExpanded;

        /// <summary>
        /// Forces every named card shut in one go, for the toolbar's Collapse All.
        /// <para>
        /// The names have to be passed in: the dictionary only holds cards somebody has already
        /// touched, so "collapse everything" cannot be done by walking it — with the expand-by-default
        /// setting on, the untouched cards are precisely the open ones and precisely the ones missing
        /// from the dictionary. Writing an explicit entry for every card on screen also means the
        /// result outlasts the default, which is what makes the button stick.
        /// </para>
        /// <para>
        /// Takes <paramref name="expanded"/> rather than being a Collapse method because the state
        /// it writes is the one thing that matters here, and a method that can only write one value
        /// invites a second method beside it that writes the other.
        /// </para>
        /// </summary>
        public void SetAllExpanded(bool expanded, IEnumerable<string> groupNames, IEnumerable<string> workspaceKeys)
        {
            foreach (var name in groupNames)
            {
                _expandedStates[$"group_{name}"] = expanded;
            }

            foreach (var key in workspaceKeys)
            {
                _expandedStates[$"workspace_{key}"] = expanded;
            }

            OnStateChanged.Invoke(this, new CardStateChangedEventArgs(string.Empty, string.Empty));
        }

        public void SetSearchExpanded(bool isSearching, IEnumerable<string> groupNames)
        {
            if (isSearching)
            {
                // Expand all provided group IDs
                foreach (var name in groupNames)
                {
                    var key = $"group_{name}";
                    _expandedStates[key] = true;
                }
            }
            else
            {
                // Clearing the search puts the groups back to their default rather than flatly
                // collapsing them: with "Expand groups by default" on, collapsing every card the
                // moment a search box empties reads as the setting having been ignored.
                var groupKeys = _expandedStates.Keys.Where(k => k.StartsWith("group_")).ToList();
                foreach (var key in groupKeys)
                {
                    _expandedStates[key] = DefaultGroupsExpanded;
                }
            }
            OnStateChanged.Invoke(this, new CardStateChangedEventArgs(string.Empty, string.Empty));
        }
    }
} 