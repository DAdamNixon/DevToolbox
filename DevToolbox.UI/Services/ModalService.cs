using System;
using DevToolbox.Services.Models;

namespace DevToolbox.UI.Services
{
    public class ModalService
    {
        public event Action<Workspace>? OnShowAddLocationModal;
        public event Action<Workspace>? OnShowMoveToGroupModal;
        public event Action? OnHideAddLocationModal;
        public event Action? OnHideMoveToGroupModal;
        public event Action<WorkspaceLocation>? OnLocationAdded;

        private Workspace? currentWorkspace;

        public void ShowAddLocationModal(Workspace workspace)
        {
            currentWorkspace = workspace;
            OnShowAddLocationModal?.Invoke(workspace);
        }

        public void ShowMoveToGroupModal(Workspace workspace)
        {
            currentWorkspace = workspace;
            OnShowMoveToGroupModal?.Invoke(workspace);
        }

        public void HideAddLocationModal()
        {
            OnHideAddLocationModal?.Invoke();
        }

        public void HideMoveToGroupModal()
        {
            OnHideMoveToGroupModal?.Invoke();
        }

        public void HandleLocationAdded(WorkspaceLocation location)
        {
            if (currentWorkspace != null)
            {
                currentWorkspace.Locations.Add(location);
                OnLocationAdded?.Invoke(location);
            }
        }
    }
} 