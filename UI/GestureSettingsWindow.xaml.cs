using Microsoft.VisualStudio.Shell;
using MouseGestures.ViewModels;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;

namespace MouseGestures.UI
{
    /// <summary>
    /// Interaction logic for GestureSettingsWindow.xaml
    /// </summary>
    public partial class GestureSettingsWindow : Window
    {
        private bool _isUpdatingComboBox = false;

        public GestureSettingsWindow()
        {
            InitializeComponent();
        }

        public GestureSettingsWindow(GestureSettingsViewModel viewModel) : this()
        {
            DataContext = viewModel;
            viewModel.SetOwnerWindow(this);

            // Subscribe to gesture selection changes
            viewModel.GestureSelectionChanged += ViewModel_GestureSelectionChanged;
        }

        private void ViewModel_GestureSelectionChanged(object sender, System.EventArgs e)
        {
            // Manually refresh ComboBox when gesture selection changes
            RefreshCommandComboBox();
        }

        private void RefreshCommandComboBox()
        {
            if (DataContext is GestureSettingsViewModel viewModel && viewModel.SelectedGesture != null)
            {
                _isUpdatingComboBox = true;

                var commandId = viewModel.SelectedGesture.VsCommandId;

                if (!string.IsNullOrEmpty(commandId))
                {
                    // Find matching command in filtered list
                    var matchingCommand = viewModel.FilteredCommands.FirstOrDefault(c => c.CommandId == commandId);

                    if (matchingCommand != null)
                    {
                        CommandComboBox.SelectedItem = matchingCommand;
                    }
                    else
                    {
                        // Command not in filtered list, try to find in all commands
                        matchingCommand = viewModel.AvailableCommands.FirstOrDefault(c => c.CommandId == commandId);
                        if (matchingCommand != null)
                        {
                            // Add to filtered list temporarily
                            if (!viewModel.FilteredCommands.Contains(matchingCommand))
                            {
                                viewModel.FilteredCommands.Insert(0, matchingCommand);
                            }
                            CommandComboBox.SelectedItem = matchingCommand;
                        }
                        else
                        {
                            // Create temporary item for unknown command
                            var tempCommand = new VsCommandInfo(commandId, viewModel.SelectedGesture.VsCommandName);
                            viewModel.FilteredCommands.Insert(0, tempCommand);
                            CommandComboBox.SelectedItem = tempCommand;
                        }
                    }
                }
                else
                {
                    CommandComboBox.SelectedItem = null;
                }

                _isUpdatingComboBox = false;

                Debug.WriteLine($"ComboBox refreshed for gesture: {viewModel.SelectedGesture.Name}, Command: {commandId}");
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            // Open hyperlink in default browser/email client
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open link: {ex.Message}");
            }
        }

        private void DonateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://paypal.me/PetoJanak/5USD") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open donate link: {ex.Message}");
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (DataContext is GestureSettingsViewModel viewModel && viewModel.IsRecordingGesture)
                {
                    // Cancel recording when ESC is pressed
                    ThreadHelper.ThrowIfNotOnUIThread();
                    viewModel.CancelRecordingCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }

        private void CommandComboBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingComboBox) return;

            if (!(sender is ComboBox comboBox)) return;

            if (!(DataContext is GestureSettingsViewModel viewModel)) return;

            // Get the text from the ComboBox's TextBox
            if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is TextBox textBox)
            {
                viewModel.FilterCommands(textBox.Text);

                // Keep the dropdown open if there are results
                if (viewModel.FilteredCommands.Count > 0 && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    comboBox.IsDropDownOpen = true;
                }
            }
        }

        private void CommandComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingComboBox) return;

            if (!(sender is ComboBox)) return;

            if (!(DataContext is GestureSettingsViewModel viewModel) || viewModel.SelectedGesture == null) return;

            // Only update if user selected from dropdown
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is VsCommandInfo selectedCommand)
            {
                viewModel.UpdateGestureCommand(selectedCommand.CommandId, selectedCommand.DisplayName);
                Debug.WriteLine($"User selected command: {selectedCommand.CommandId}");
            }
        }

        private void CommandComboBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox comboBox)
            {
                // Open dropdown when textbox gets focus
                comboBox.IsDropDownOpen = true;
            }
        }

        private void CommandComboBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox comboBox)
            {
                comboBox.IsDropDownOpen = false;
            }
        }

        private void GestureListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
                MainTabControl.SelectedIndex = 0;
        }

        private void PickColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is GestureSettingsViewModel viewModel))
                return;

            using (var dialog = new System.Windows.Forms.ColorDialog())
            {
                dialog.FullOpen = true;

                // Pre-populate with the current color if valid
                var current = viewModel.VisualizationSettings?.TrailColor;
                if (!string.IsNullOrWhiteSpace(current))
                {
                    try
                    {
                        var wpfColor = (Color)ColorConverter.ConvertFromString(current);
                        dialog.Color = System.Drawing.Color.FromArgb(wpfColor.R, wpfColor.G, wpfColor.B);
                    }
                    catch { /* ignore invalid hex, start with black */ }
                }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    var c = dialog.Color;
                    viewModel.VisualizationSettings.TrailColor =
                        $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                }
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (DataContext is GestureSettingsViewModel viewModel && !viewModel.RequestClose())
            {
                e.Cancel = true;
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(System.EventArgs e)
        {
            // Unsubscribe from events
            if (DataContext is GestureSettingsViewModel viewModel)
            {
                viewModel.GestureSelectionChanged -= ViewModel_GestureSelectionChanged;
            }

            base.OnClosed(e);
        }
    }
}