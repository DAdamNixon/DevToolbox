using System;
using System.Collections.Concurrent;

namespace DevToolbox.UI.Services
{
    /// <summary>
    /// Service for centrally managing modal visibility and error states
    /// </summary>
    public class ModalStateService
    {
        private readonly ConcurrentDictionary<string, bool> _modalVisibility = new();
        private readonly ConcurrentDictionary<string, string?> _errorMessages = new();

        public event EventHandler<string>? ModalStateChanged;

        /// <summary>
        /// Shows a modal with the given key
        /// </summary>
        /// <param name="modalKey">Unique identifier for the modal</param>
        public void ShowModal(string modalKey)
        {
            _modalVisibility[modalKey] = true;
            ClearError(modalKey);
            ModalStateChanged?.Invoke(this, modalKey);
        }

        /// <summary>
        /// Hides a modal with the given key
        /// </summary>
        /// <param name="modalKey">Unique identifier for the modal</param>
        public void HideModal(string modalKey)
        {
            _modalVisibility[modalKey] = false;
            ClearError(modalKey);
            ModalStateChanged?.Invoke(this, modalKey);
        }

        /// <summary>
        /// Checks if a modal is visible
        /// </summary>
        /// <param name="modalKey">Unique identifier for the modal</param>
        /// <returns>True if the modal is visible, false otherwise</returns>
        public bool IsModalVisible(string modalKey)
        {
            return _modalVisibility.GetValueOrDefault(modalKey, false);
        }

        /// <summary>
        /// Sets an error message for a modal
        /// </summary>
        /// <param name="modalKey">Unique identifier for the modal</param>
        /// <param name="errorMessage">Error message to display, or null to clear</param>
        public void SetError(string modalKey, string? errorMessage)
        {
            _errorMessages[modalKey] = errorMessage;
            ModalStateChanged?.Invoke(this, modalKey);
        }

        /// <summary>
        /// Clears any error message for a modal
        /// </summary>
        /// <param name="modalKey">Unique identifier for the modal</param>
        public void ClearError(string modalKey)
        {
            _errorMessages.TryRemove(modalKey, out _);
        }

        /// <summary>
        /// Gets the current error message for a modal
        /// </summary>
        /// <param name="modalKey">Unique identifier for the modal</param>
        /// <returns>The error message, or null if there is none</returns>
        public string? GetError(string modalKey)
        {
            return _errorMessages.GetValueOrDefault(modalKey);
        }

        /// <summary>
        /// Generates a unique modal key based on a prefix and ID
        /// </summary>
        /// <param name="prefix">Prefix for the modal key</param>
        /// <param name="id">ID to append to the prefix</param>
        /// <returns>A unique modal key</returns>
        public string GetModalKey(string prefix, string id)
        {
            return $"{prefix}_{id}";
        }
    }
} 