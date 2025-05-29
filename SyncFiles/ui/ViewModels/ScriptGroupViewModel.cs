using SyncFiles.Core.Models;
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
        // Parent ViewModel or services needed for actions
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
                    // TODO: Notify parent/main VM to save config if name changes (except for Default group)
                }
            }
        }

        public ObservableCollection<ScriptEntryViewModel> Scripts { get; }

        // --- Commands (to be implemented with DelegateCommand/RelayCommand) ---
        public ICommand AddScriptToGroupCommand { get; private set; }
        public ICommand RenameGroupCommand { get; private set; }
        public ICommand DeleteGroupCommand { get; private set; }

        public bool IsDefaultGroup => Id == ScriptGroup.DefaultGroupId;

        // For ContextMenu Visibility
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

            // Initialize Commands (using placeholder actions for now)
            // AddScriptToGroupCommand = new ScriptEntryViewModel.DelegateCommand(() => _parentViewModel.RequestAddScriptToGroup(this), () => true);
            // RenameGroupCommand = new ScriptEntryViewModel.DelegateCommand(() => _parentViewModel.RequestRenameGroup(this), () => !IsDefaultGroup);
            // DeleteGroupCommand = new ScriptEntryViewModel.DelegateCommand(() => _parentViewModel.RequestDeleteGroup(this), () => !IsDefaultGroup);
        }

        // Method to add a ScriptEntryViewModel (called by parent VM or dialog result)
        public void AddScript(ScriptEntryViewModel scriptVM)
        {
            Scripts.Add(scriptVM);
            _model.Scripts.Add(scriptVM.GetModel()); // Add to underlying model too
            // Consider sorting Scripts collection here
            // TODO: Notify parent/main VM to save config
        }

        // Method to remove a ScriptEntryViewModel
        public bool RemoveScript(ScriptEntryViewModel scriptVM)
        {
            bool removedFromVM = Scripts.Remove(scriptVM);
            bool removedFromModel = _model.Scripts.Remove(scriptVM.GetModel());
            if (removedFromVM || removedFromModel)
            {
                // TODO: Notify parent/main VM to save config
                return true;
            }
            return false;
        }

        public ScriptGroup GetModel() => _model;
    }
}