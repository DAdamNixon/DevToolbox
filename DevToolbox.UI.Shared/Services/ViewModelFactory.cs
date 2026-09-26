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
        /// Creates a WorkspaceViewModel from a Workspace domain model.
        /// <paramref name="groupName"/> is what makes the card key unique — workspace ids
        /// repeat across groups, so it cannot be derived from the workspace alone.
        /// </summary>
        public WorkspaceViewModel CreateWorkspaceViewModel(Workspace workspace, string groupName)
        {
            var stateKey = BuildStateKey(groupName, workspace.Name);

            return new WorkspaceViewModel
            {
                Workspace = workspace,
                StateKey = stateKey,
                IsExpanded = _cardStateService.IsExpanded("workspace", stateKey),
                IsSelected = false
            };
        }

        /// <summary>Card identity: group + workspace name.</summary>
        public static string BuildStateKey(string groupName, string workspaceName) =>
            $"{groupName}/{workspaceName}";

        /// <summary>
        /// Creates a WorkspaceGroupViewModel from a WorkspaceGroup domain model
        /// </summary>
        public WorkspaceGroupViewModel CreateWorkspaceGroupViewModel(WorkspaceGroup group) =>
            CreateWorkspaceGroupViewModel(group, group.Workspaces);

        /// <summary>
        /// The same, with the cards in an order the caller chose — the dashboard passes the
        /// pinned-first order from <c>IDashboardLayoutService</c>.
        /// <para>
        /// A separate sequence rather than a sort applied to <c>group.Workspaces</c>: that list is
        /// what gets written back to workspaceGroups.yaml, and pinning a card is not a reason to
        /// rewrite the file.
        /// </para>
        /// </summary>
        public WorkspaceGroupViewModel CreateWorkspaceGroupViewModel(WorkspaceGroup group, IEnumerable<Workspace> workspaces)
        {
            var viewModel = new WorkspaceGroupViewModel
            {
                Group = group,
                IsExpanded = _cardStateService.IsExpanded("group", group.Name),
                Workspaces = new List<WorkspaceViewModel>()
            };

            foreach (var workspace in workspaces)
            {
                viewModel.Workspaces.Add(CreateWorkspaceViewModel(workspace, group.Name));
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
            viewModel.IsExpanded = _cardStateService.IsExpanded("workspace", viewModel.StateKey);
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