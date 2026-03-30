using MouseGestures.Models;
using MouseGestures.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using MouseGesture = MouseGestures.Models.MouseGesture;

namespace MouseGestures.ViewModels
{
    /// <summary>
    /// ViewModel for gesture settings dialog.
    /// </summary>
    public class GestureSettingsViewModel : INotifyPropertyChanged
    {
        private readonly GestureManagerService _gestureManager;
        private readonly GestureOrchestratorService _orchestrator;
        private MouseGesture _selectedGesture;
        private bool _isEditing;
        private bool _isRecordingGesture;
        private Window _ownerWindow;
        private bool _hasDuplicatePattern;
        private string _duplicatePatternMessage;

        private double _windowOpacity = 1.0;

        public ObservableCollection<MouseGesture> Gestures { get; }
        public ObservableCollection<string> AvailableDirections { get; }
        public ObservableCollection<VsCommandInfo> AvailableCommands { get; }
        public ObservableCollection<VsCommandInfo> FilteredCommands { get; }

        public GestureVisualizationSettings VisualizationSettings { get; }

        public string AppVersion
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }

        public MouseGesture SelectedGesture
        {
            get => _selectedGesture;
            set
            {
                _selectedGesture = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsGestureSelected));

                // Clear duplicate warning when switching gestures
                HasDuplicatePattern = false;

                // Notify that gesture selection changed - window will handle ComboBox refresh
                OnGestureSelectionChanged();

                ((RelayCommand)EditGestureCommand).RaiseCanExecuteChanged();
                ((RelayCommand)DeleteGestureCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ToggleEnabledCommand).RaiseCanExecuteChanged();
                ((RelayCommand)RecordGestureCommand).RaiseCanExecuteChanged();
                ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
            }
        }

        public event EventHandler GestureSelectionChanged;

        private void OnGestureSelectionChanged()
        {
            GestureSelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateGestureCommand(string commandId, string commandName)
        {
            if (SelectedGesture == null)
                return;

            SelectedGesture.VsCommandId = commandId;
            SelectedGesture.VsCommandName = commandName;

            _gestureManager.UpdateGesture(SelectedGesture);
            OnPropertyChanged(nameof(Gestures));

            System.Diagnostics.Debug.WriteLine($"Command updated: {commandId} - {commandName}");
        }

        public bool IsGestureSelected => _selectedGesture != null;

        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                _isEditing = value;
                OnPropertyChanged();
            }
        }

        public bool IsRecordingGesture
        {
            get => _isRecordingGesture;
            set
            {
                _isRecordingGesture = value;
                OnPropertyChanged();
                ((RelayCommand)RecordGestureCommand).RaiseCanExecuteChanged();
            }
        }

        public bool HasDuplicatePattern
        {
            get => _hasDuplicatePattern;
            set
            {
                _hasDuplicatePattern = value;
                OnPropertyChanged();
                ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
            }
        }

        public string DuplicatePatternMessage
        {
            get => _duplicatePatternMessage;
            set
            {
                _duplicatePatternMessage = value;
                OnPropertyChanged();
            }
        }

        public double WindowOpacity
        {
            get => _windowOpacity;
            set
            {
                _windowOpacity = value;
                OnPropertyChanged();
            }
        }

        // Commands
        public ICommand AddGestureCommand { get; }

        public ICommand EditGestureCommand { get; }
        public ICommand DeleteGestureCommand { get; }
        public ICommand ToggleEnabledCommand { get; }
        public ICommand RecordGestureCommand { get; }
        public ICommand CancelRecordingCommand { get; }
        public ICommand SaveCommand { get; }

        public GestureSettingsViewModel(GestureManagerService gestureManager, GestureOrchestratorService orchestrator)
        {
            _gestureManager = gestureManager;
            _orchestrator = orchestrator;
            VisualizationSettings = orchestrator.VisualizationSettings;

            Gestures = new ObservableCollection<MouseGesture>(_gestureManager.Gestures);
            _gestureManager.ClearTmpGestures();

            AvailableDirections = new ObservableCollection<string>(
                Enum.GetNames(typeof(GestureDirection)));
            AvailableCommands = new ObservableCollection<VsCommandInfo>(Utils.Utils.GetCommonVsCommands());
            FilteredCommands = new ObservableCollection<VsCommandInfo>(AvailableCommands);

            AddGestureCommand = new RelayCommand(AddGesture);
            EditGestureCommand = new RelayCommand(EditGesture, () => SelectedGesture != null);
            DeleteGestureCommand = new RelayCommand(DeleteGesture, () => SelectedGesture != null);
            ToggleEnabledCommand = new RelayCommand(ToggleEnabled, () => SelectedGesture != null);
            RecordGestureCommand = new RelayCommand(RecordGesture, CanRecordGesture);
            CancelRecordingCommand = new RelayCommand(CancelRecording, () => IsRecordingGesture);
            SaveCommand = new RelayCommand(Save, CanSave);
        }

        public void SetOwnerWindow(Window window)
        {
            _ownerWindow = window;
        }

        public void FilterCommands(string searchText)
        {
            FilteredCommands.Clear();

            if (string.IsNullOrWhiteSpace(searchText))
            {
                // Show all commands if search is empty
                foreach (var command in AvailableCommands)
                {
                    FilteredCommands.Add(command);
                }
            }
            else
            {
                // Filter commands by search text (case-insensitive)
                var filtered = AvailableCommands.Where(cmd =>
                    cmd.DisplayName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cmd.CommandId.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0);

                foreach (var command in filtered)
                {
                    FilteredCommands.Add(command);
                }
            }
        }

        private void AddGesture()
        {
            var newGesture = new MouseGesture
            {
                Name = "New Gesture",
                Pattern = new List<GestureDirection>(),
                VsCommandId = string.Empty,
                VsCommandName = string.Empty
            };

            _gestureManager.AddGesture(newGesture);
            Gestures.Add(newGesture);
            SelectedGesture = newGesture;
            IsEditing = true;
        }

        private void EditGesture()
        {
            if (SelectedGesture != null)
            {
                IsEditing = true;
            }
        }

        private void DeleteGesture()
        {
            if (SelectedGesture != null)
            {
                _gestureManager.RemoveGesture(SelectedGesture.Id);
                Gestures.Remove(SelectedGesture);
                SelectedGesture = null;
            }
        }

        private void ToggleEnabled()
        {
            if (SelectedGesture != null)
            {
                SelectedGesture.IsEnabled = !SelectedGesture.IsEnabled;
                _gestureManager.UpdateGesture(SelectedGesture);
                OnPropertyChanged(nameof(Gestures));
            }
        }

        private bool CanRecordGesture()
        {
            // Allow recording if gesture is selected
            // Button is always enabled when gesture is selected (for both start and cancel)
            return SelectedGesture != null;
        }

        private void RecordGesture()
        {
            if (SelectedGesture == null)
                return;

            if (IsRecordingGesture)
            {
                // Cancel recording (second click)
                CancelRecording();
            }
            else
            {
                // Clear any previous duplicate warning
                HasDuplicatePattern = false;
                DuplicatePatternMessage = string.Empty;

                // Start recording
                IsRecordingGesture = true;
                _orchestrator.StartGestureRecording(OnGestureRecorded, OnGestureStartedCallback);
            }
        }

        private void OnGestureStartedCallback()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Hide window completely when gesture starts
            WindowOpacity = 0.2;
        }

        private void CancelRecording()
        {
            if (!IsRecordingGesture)
                return;

            IsRecordingGesture = false;
            WindowOpacity = 1.0; // Reset opacity in case it was changed
            _orchestrator.StopGestureRecording();
        }

        private void OnGestureRecorded(List<GestureDirection> recordedPattern)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Show window again
            WindowOpacity = 1.0;

            // Stop recording mode
            IsRecordingGesture = false;
            _orchestrator.StopGestureRecording();

            // Only update pattern if recording completed successfully with a valid pattern
            if (SelectedGesture != null && recordedPattern != null && recordedPattern.Count > 0)
            {
                // Check for duplicate pattern
                var existingGesture = _gestureManager.FindGestureWithSamePattern(recordedPattern, SelectedGesture.Id);

                if (existingGesture != null)
                {
                    // Set duplicate warning - don't update the pattern yet
                    HasDuplicatePattern = true;
                    DuplicatePatternMessage = $"⚠ This pattern is already used by gesture '{existingGesture.Name}'";

                    // Create a NEW list to avoid reference issues
                    SelectedGesture.Pattern = new List<GestureDirection>(recordedPattern);

                    OnPropertyChanged(nameof(SelectedGesture));
                    OnPropertyChanged(nameof(Gestures));
                }
                else
                {
                    // No duplicate - clear any warnings and update pattern
                    HasDuplicatePattern = false;
                    DuplicatePatternMessage = string.Empty;

                    // Create a NEW list to avoid reference issues
                    SelectedGesture.Pattern = new List<GestureDirection>(recordedPattern);
                    _gestureManager.UpdateGesture(SelectedGesture);

                    // Refresh the UI to show new pattern
                    OnPropertyChanged(nameof(SelectedGesture));
                    OnPropertyChanged(nameof(Gestures));
                }
            }
        }

        private bool CanSave()
        {
            // Disable save if there's a duplicate pattern
            return !HasDuplicatePattern;
        }

        private async void Save()
        {
            await _gestureManager.SaveGesturesAsync();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class VsCommandInfo
    {
        public string CommandId { get; set; }
        public string DisplayName { get; set; }

        public VsCommandInfo(string commandId, string displayName)
        {
            CommandId = commandId;
            DisplayName = displayName;
        }
    }
}