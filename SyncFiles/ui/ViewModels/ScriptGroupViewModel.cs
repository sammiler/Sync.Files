using SyncFiles.Core.Models;
using SyncFiles.UI.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel; // For ObservableCollection
using System.Linq;
using System.Windows.Input;
namespace SyncFiles.UI.ViewModels
{
    public class ScriptGroupViewModel : ViewModelBase
    {
        private readonly ScriptGroup _model;
        private readonly SyncFilesToolWindowViewModel _parentViewModel;
        public string Id => _model.Id;
        private string _name;
        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                {
                    _model.Name = value; // Update underlying model
                }
            }
        }
        private bool _isExpanded = true; // 默认展开

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value))
                {
                    // 如果需要其他操作，可以在这里添加
                }
            }
        }
        public ObservableCollection<ScriptEntryViewModel> Scripts { get; }
        public ICommand AddScriptToGroupCommand { get; private set; }
        public ICommand RenameGroupCommand { get; private set; }
        public ICommand DeleteGroupCommand { get; private set; }

        private string _folderIconPath;
        public string FolderIconPath { get => _folderIconPath; set => SetProperty(ref _folderIconPath, value); }

        public bool IsDefaultGroup => Id == ScriptGroup.DefaultGroupId;
        public bool IsScriptEntry => false;
        public bool IsScriptGroup => true;
        public bool IsScriptGroupAndNotDefault => IsScriptGroup && !IsDefaultGroup;
        public ScriptGroupViewModel(
            ScriptGroup model,
            string pythonExecutablePath,
            string pythonScriptBasePath,
            Dictionary<string, string> environmentVariables,
            string projectBasePath,
            SyncFilesToolWindowViewModel parentViewModel)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _parentViewModel = parentViewModel; // Store parent for calling its methods
            _name = model.Name; // Initialize from model
            Scripts = new ObservableCollection<ScriptEntryViewModel>(
                model.Scripts.Select(s => new ScriptEntryViewModel(s, pythonExecutablePath, pythonScriptBasePath, environmentVariables, projectBasePath, parentViewModel))
            );
            RenameGroupCommand = new RelayCommand(RequestRenameGroup, () => !IsDefaultGroup && parentViewModel != null);
            DeleteGroupCommand = new RelayCommand(RequestDeleteGroup, () => !IsDefaultGroup && parentViewModel != null);

        }
        public void AddScript(ScriptEntryViewModel scriptVM)
        {
            Scripts.Add(scriptVM);
            _model.Scripts.Add(scriptVM.GetModel()); // Add to underlying model too
        }
        private void RequestRenameGroup()
        {
            if (IsDefaultGroup) return;
            string newName = _parentViewModel.ShowInputDialog("Enter new group name:", Name);
            if (newName != null && !string.IsNullOrWhiteSpace(newName) && newName.Trim() != Name)
            {
                // Check for duplicate name in parent ViewModel before setting
                if (!_parentViewModel.IsGroupNameDuplicate(newName.Trim(), Id))
                {
                    Name = newName.Trim(); // This should update _model.Name via its setter
                    _parentViewModel.RequestSaveSettings();
                }
                else
                {
                    _parentViewModel.ShowErrorMessage("Error", "Group name already exists.");
                }
            }
        }

        private void RequestDeleteGroup()
        {
            if (IsDefaultGroup) return;
            _parentViewModel.RequestDeleteGroup(this);
        }
        public bool RemoveScript(ScriptEntryViewModel scriptVM)
        {
            bool removedFromVM = Scripts.Remove(scriptVM);
            bool removedFromModel = _model.Scripts.Remove(scriptVM.GetModel());
            if (removedFromVM || removedFromModel)
            {
                return true;
            }
            return false;
        }
        public ScriptGroup GetModel() => _model;
    }
}