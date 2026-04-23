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
using Microsoft.Win32;
using Newtonsoft.Json;
using System.IO;

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
        private bool _hasUnsavedChanges;

        // Snapshots taken when the dialog opens — used to restore state on discard
        private GestureVisualizationSettings _visualizationSnapshot;
        private List<MouseGesture> _gesturesSnapshot;
        private double _windowOpacity = 1.0;

        public ObservableCollection<MouseGesture> Gestures { get; }
        public ObservableCollection<string> AvailableDirections { get; }
        public ObservableCollection<VsCommandInfo> AvailableCommands { get; }
        public ObservableCollection<VsCommandInfo> FilteredCommands { get; }

        public GestureVisualizationSettings VisualizationSettings { get; }

        public static string AppVersion
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
        }

        public MouseGesture SelectedGesture
        {
            get => _selectedGesture;
            set
            {
                // Unsubscribe from previous gesture's property changes
                if (_selectedGesture != null)
                    _selectedGesture.PropertyChanged -= OnSelectedGesturePropertyChanged;

                _selectedGesture = value;

                // Subscribe to new gesture's property changes
                if (_selectedGesture != null)
                    _selectedGesture.PropertyChanged += OnSelectedGesturePropertyChanged;

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

        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            private set
            {
                _hasUnsavedChanges = value;
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
        public ICommand ResetToDefaultsCommand { get; }
        public ICommand ExportSettingsCommand { get; }
        public ICommand ImportSettingsCommand { get; }

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
            ResetToDefaultsCommand = new RelayCommand(ResetToDefaults);
            ExportSettingsCommand = new RelayCommand(ExportSettings);
            ImportSettingsCommand = new RelayCommand(ImportSettings);

            // Track any change to visualization settings
            VisualizationSettings.PropertyChanged += (s, e) => HasUnsavedChanges = true;

            // Track changes to gesture list items
            Gestures.CollectionChanged += (s, e) => HasUnsavedChanges = true;

            // Take a snapshot of the initial state so we can restore it on discard
            TakeSnapshot();
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

        private void OnSelectedGesturePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            HasUnsavedChanges = true;
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

        private void ResetToDefaults()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var result = MessageBox.Show(
                _ownerWindow,
                "This will delete all your custom gestures and restore default gestures.\n\n" +
                "Do you want to continue?",
                "Reset to Defaults",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                // Reset to defaults
                _gestureManager.ResetToDefaults();
                _gestureManager.DeleteConfigFile();

                // Reload UI
                Gestures.Clear();
                foreach (var gesture in _gestureManager.Gestures)
                {
                    Gestures.Add(gesture);
                }

                SelectedGesture = null;
                HasDuplicatePattern = false;
                DuplicatePatternMessage = string.Empty;

                MessageBox.Show(
                    _ownerWindow,
                    "Gestures have been reset to defaults.",
                    "Reset Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
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
            return !HasDuplicatePattern;
        }

        private async void Save()
        {
            await _gestureManager.SaveGesturesAsync();
            await _gestureManager.SaveVisualizationSettingsAsync(VisualizationSettings);
            TakeSnapshot();
            HasUnsavedChanges = false;
        }

        private void TakeSnapshot()
        {
            _visualizationSnapshot = new GestureVisualizationSettings
            {
                ShowTrail = VisualizationSettings.ShowTrail,
                ShowDirections = VisualizationSettings.ShowDirections,
                TrailColor = VisualizationSettings.TrailColor,
                TrailThickness = VisualizationSettings.TrailThickness,
                MinimumGestureDistance = VisualizationSettings.MinimumGestureDistance
            };

            _gesturesSnapshot = _gestureManager.Gestures
                .Select(g => new MouseGesture
                {
                    Id = g.Id,
                    Name = g.Name,
                    Pattern = new List<GestureDirection>(g.Pattern),
                    VsCommandId = g.VsCommandId,
                    VsCommandName = g.VsCommandName,
                    IsEnabled = g.IsEnabled
                })
                .ToList();
        }

        private void RestoreSnapshot()
        {
            // Restore visualization settings
            VisualizationSettings.ShowTrail = _visualizationSnapshot.ShowTrail;
            VisualizationSettings.ShowDirections = _visualizationSnapshot.ShowDirections;
            VisualizationSettings.TrailColor = _visualizationSnapshot.TrailColor;
            VisualizationSettings.TrailThickness = _visualizationSnapshot.TrailThickness;
            VisualizationSettings.MinimumGestureDistance = _visualizationSnapshot.MinimumGestureDistance;

            // Restore gestures in GestureManagerService
            foreach (var snapshot in _gesturesSnapshot)
                _gestureManager.UpdateGesture(snapshot);

            // Remove gestures that were added during this session
            var snapshotIds = new HashSet<Guid>(_gesturesSnapshot.Select(g => g.Id));
            foreach (var added in _gestureManager.Gestures
                .Where(g => !snapshotIds.Contains(g.Id)).ToList())
                _gestureManager.RemoveGesture(added.Id);
        }

        /// <summary>
        /// Called when the user attempts to close the window.
        /// Returns true if the window may close, false if the user cancelled.
        /// </summary>
        public bool RequestClose()
        {
            if (!HasUnsavedChanges)
                return true;

            var result = MessageBox.Show(
                _ownerWindow,
                "You have unsaved changes. Do you want to discard them and close?",
                "Unsaved Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                RestoreSnapshot();
                return true;
            }

            return false;
        }

        private void ExportSettings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var saveFileDialog = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json",
                AddExtension = true,
                FileName = "mouse-gestures-settings.json",
                Title = "Export Gesture Settings"
            };

            if (saveFileDialog.ShowDialog(_ownerWindow) != true)
                return;

            try
            {
                var exportModel = new GestureSettingsExport
                {
                    Gestures = Gestures.Select(g => new MouseGesture
                    {
                        Id = g.Id,
                        Name = g.Name,
                        Pattern = g.Pattern != null ? new List<GestureDirection>(g.Pattern) : new List<GestureDirection>(),
                        VsCommandId = g.VsCommandId,
                        VsCommandName = g.VsCommandName,
                        IsEnabled = g.IsEnabled
                    }).ToList(),
                    VisualizationSettings = new GestureVisualizationSettings
                    {
                        ShowTrail = VisualizationSettings.ShowTrail,
                        ShowDirections = VisualizationSettings.ShowDirections,
                        TrailColor = VisualizationSettings.TrailColor,
                        TrailThickness = VisualizationSettings.TrailThickness,
                        MinimumGestureDistance = VisualizationSettings.MinimumGestureDistance
                    }
                };

                var json = JsonConvert.SerializeObject(exportModel, Formatting.Indented);
                File.WriteAllText(saveFileDialog.FileName, json);

                MessageBox.Show(
                    _ownerWindow,
                    "Settings were exported successfully.",
                    "Export Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    _ownerWindow,
                    $"Failed to export settings.\n\n{ex.Message}",
                    "Export Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ImportSettings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var openFileDialog = new OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json",
                Title = "Import Gesture Settings"
            };

            if (openFileDialog.ShowDialog(_ownerWindow) != true)
                return;

            var overwriteResult = MessageBox.Show(
                _ownerWindow,
                "Import will overwrite current gestures and visualization settings in this window.\n\nDo you want to continue?",
                "Confirm Import",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (overwriteResult != MessageBoxResult.Yes)
                return;

            try
            {
                var json = File.ReadAllText(openFileDialog.FileName);
                var imported = JsonConvert.DeserializeObject<GestureSettingsExport>(json);

                if (imported == null || imported.Gestures == null || imported.VisualizationSettings == null)
                {
                    MessageBox.Show(
                        _ownerWindow,
                        "The selected file does not contain valid gesture settings.",
                        "Import Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                var importedGestures = imported.Gestures
                    .Where(g => g != null)
                    .Select(g => new MouseGesture
                    {
                        Id = g.Id == Guid.Empty ? Guid.NewGuid() : g.Id,
                        Name = g.Name ?? string.Empty,
                        Pattern = g.Pattern != null ? new List<GestureDirection>(g.Pattern) : new List<GestureDirection>(),
                        VsCommandId = g.VsCommandId ?? string.Empty,
                        VsCommandName = g.VsCommandName ?? string.Empty,
                        IsEnabled = g.IsEnabled
                    })
                    .ToList();

                _gestureManager.ReplaceAllGestures(importedGestures);

                Gestures.Clear();
                foreach (var gesture in importedGestures)
                {
                    Gestures.Add(gesture);
                }

                SelectedGesture = null;

                VisualizationSettings.ShowTrail = imported.VisualizationSettings.ShowTrail;
                VisualizationSettings.ShowDirections = imported.VisualizationSettings.ShowDirections;
                VisualizationSettings.TrailColor = imported.VisualizationSettings.TrailColor;
                VisualizationSettings.TrailThickness = imported.VisualizationSettings.TrailThickness;
                VisualizationSettings.MinimumGestureDistance = imported.VisualizationSettings.MinimumGestureDistance;

                HasDuplicatePattern = false;
                DuplicatePatternMessage = string.Empty;
                HasUnsavedChanges = true;

                MessageBox.Show(
                    _ownerWindow,
                    "Settings were imported successfully.",
                    "Import Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    _ownerWindow,
                    $"Failed to import settings.\n\n{ex.Message}",
                    "Import Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
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