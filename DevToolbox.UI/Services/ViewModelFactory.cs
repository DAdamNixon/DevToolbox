using DevToolbox.Services.Models;
using DevToolbox.UI.Models;

namespace DevToolbox.UI.Services
{
    /// <summary>
    /// Provides methods to create view models from domain models
    /// </summary>
    public class ViewModelFactory
    {
        private readonly CardStateService _cardStateService;

        public ViewModelFactory(CardStateService cardStateService)
        {
            _cardStateService = cardStateService;
        }

        /// <summary>
        /// Creates a WorkspaceViewModel from a Workspace domain model
        /// </summary>
        public WorkspaceViewModel CreateWorkspaceViewModel(Workspace workspace)
        {
            return new WorkspaceViewModel
            {
                Workspace = workspace,
                IsExpanded = _cardStateService.IsExpanded("workspace", workspace.Id.ToString()),
                IsSelected = false
            };
        }

        /// <summary>
        /// Creates a WorkspaceGroupViewModel from a WorkspaceGroup domain model
        /// </summary>
        public WorkspaceGroupViewModel CreateWorkspaceGroupViewModel(WorkspaceGroup group)
        {
            var viewModel = new WorkspaceGroupViewModel
            {
                Group = group,
                IsExpanded = _cardStateService.IsExpanded("group", group.Name),
                Workspaces = new List<WorkspaceViewModel>()
            };

            // Convert all workspaces in the group to view models
            foreach (var workspace in group.Workspaces)
            {
                viewModel.Workspaces.Add(CreateWorkspaceViewModel(workspace));
            }

            return viewModel;
        }

        /// <summary>
        /// Creates a CustomOpenOptionViewModel from a CustomOpenOption domain model
        /// </summary>
        public CustomOpenOptionViewModel CreateCustomOpenOptionViewModel(string name, CustomOpenOption option)
        {
            return new CustomOpenOptionViewModel
            {
                Name = name,
                DisplayName = name,
                Option = option,
                IconClass = GetIconClassForOption(option)
            };
        }

        /// <summary>
        /// Updates the expanded state of a workspace view model
        /// </summary>
        public void UpdateWorkspaceExpandedState(WorkspaceViewModel viewModel)
        {
            viewModel.IsExpanded = _cardStateService.IsExpanded("workspace", viewModel.Workspace.Id.ToString());
        }

        /// <summary>
        /// Updates the expanded state of a workspace group view model
        /// </summary>
        public void UpdateGroupExpandedState(WorkspaceGroupViewModel viewModel)
        {
            viewModel.IsExpanded = _cardStateService.IsExpanded("group", viewModel.Group.Name);
            
            // Update all workspaces in the group
            foreach (var workspace in viewModel.Workspaces)
            {
                UpdateWorkspaceExpandedState(workspace);
            }
        }

        /// <summary>
        /// Gets an appropriate icon class for the option
        /// </summary>
        private string GetIconClassForOption(CustomOpenOption option)
        {
            // Determine an appropriate icon based on the option type
            return option.Type switch
            {
                OpenOptionType.Executable => "bi-app",
                OpenOptionType.Command => "bi-terminal",
                _ => "bi-box-arrow-up-right"
            };
        }
    }
} 