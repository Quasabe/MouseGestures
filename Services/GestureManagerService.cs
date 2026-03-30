using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using MouseGestures.Models;

namespace MouseGestures.Services
{
    /// <summary>
    /// Service for managing gesture configurations.
    /// </summary>
    public class GestureManagerService
    {
        private readonly List<MouseGesture> _gestures = new List<MouseGesture>();
        private readonly List<MouseGesture> _newGestures = new List<MouseGesture>();
        private readonly string _configFilePath;

        public IReadOnlyList<MouseGesture> Gestures => _gestures.AsReadOnly();

        public GestureManagerService()
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string extensionFolder = Path.Combine(appDataPath, "MouseGestures");
            Directory.CreateDirectory(extensionFolder);
            _configFilePath = Path.Combine(extensionFolder, "gestures.json");

            InitializeDefaultGestures();
        }

        public async Task LoadGesturesAsync()
        {
            if (!File.Exists(_configFilePath))
            {
                await SaveGesturesAsync();
                return;
            }

            try
            {
                string json = File.ReadAllText(_configFilePath);
                var loadedGestures = JsonConvert.DeserializeObject<List<MouseGesture>>(json);

                if (loadedGestures != null)
                {
                    _gestures.Clear();
                    _gestures.AddRange(loadedGestures);
                }
            }
            catch (Exception)
            {
                // If loading fails, keep default gestures
            }
        }

        public async Task SaveGesturesAsync()
        {
            _gestures.AddRange(_newGestures);
            string json = JsonConvert.SerializeObject(_gestures, Formatting.Indented);
            File.WriteAllText(_configFilePath, json);
            await Task.CompletedTask;
        }

        public void AddGesture(MouseGesture gesture)
        {
            // _gestures.Add(gesture);
            _newGestures.Add(gesture);
        }

        public void ClearTmpGestures()
        {
            _newGestures.Clear();
        }

        public void RemoveGesture(Guid gestureId)
        {
            var gesture = _gestures.FirstOrDefault(g => g.Id == gestureId);
            if (gesture != null)
            {
                _gestures.Remove(gesture);
            }

            var newGesture = _newGestures.FirstOrDefault(g => g.Id == gestureId);
            if (newGesture != null)
            {
                _newGestures.Remove(newGesture);
            }
        }

        public void UpdateGesture(MouseGesture updatedGesture)
        {
            var index = _gestures.FindIndex(g => g.Id == updatedGesture.Id);
            if (index >= 0)
            {
                _gestures[index] = updatedGesture;
            }

            var newIndex = _newGestures.FindIndex(g => g.Id == updatedGesture.Id);
            if (newIndex >= 0)
            {
                _newGestures[newIndex] = updatedGesture;
            }
        }

        public MouseGesture FindMatchingGesture(List<GestureDirection> pattern)
        {
            return _gestures
                .Where(g => g.IsEnabled)
                .FirstOrDefault(g => g.MatchesPattern(pattern))
                ??
                _newGestures
                .Where(g => g.IsEnabled)
                .FirstOrDefault(g => g.MatchesPattern(pattern));
        }

        public MouseGesture FindGestureWithSamePattern(List<GestureDirection> pattern, Guid? excludeGestureId = null)
        {
            return
                _gestures
                .Where(g => !excludeGestureId.HasValue || g.Id != excludeGestureId.Value)
                .FirstOrDefault(g => g.MatchesPattern(pattern))
                ??
                _newGestures
                .Where(g => !excludeGestureId.HasValue || g.Id != excludeGestureId.Value)
                .FirstOrDefault(g => g.MatchesPattern(pattern));
        }

        private void InitializeDefaultGestures()
        {
            _gestures.Add(new MouseGesture
            {
                Name = "Navigate Backward",
                Pattern = new List<GestureDirection> { GestureDirection.Left },
                VsCommandId = "View.NavigateBackward",
                VsCommandName = "Navigate Backward"
            });

            _gestures.Add(new MouseGesture
            {
                Name = "Navigate Forward",
                Pattern = new List<GestureDirection> { GestureDirection.Right },
                VsCommandId = "View.NavigateForward",
                VsCommandName = "Navigate Forward"
            });

            _gestures.Add(new MouseGesture
            {
                Name = "Go To Definition",
                Pattern = new List<GestureDirection> { GestureDirection.Down, GestureDirection.Right },
                VsCommandId = "Edit.GoToDefinition",
                VsCommandName = "Go To Definition"
            });

            _gestures.Add(new MouseGesture
            {
                Name = "Find All References",
                Pattern = new List<GestureDirection> { GestureDirection.Down, GestureDirection.Left },
                VsCommandId = "Edit.FindAllReferences",
                VsCommandName = "Find All References"
            });

            _gestures.Add(new MouseGesture
            {
                Name = "Comment Selection",
                Pattern = new List<GestureDirection> { GestureDirection.Down },
                VsCommandId = "Edit.CommentSelection",
                VsCommandName = "Comment Selection"
            });

            _gestures.Add(new MouseGesture
            {
                Name = "Uncomment Selection",
                Pattern = new List<GestureDirection> { GestureDirection.Left, GestureDirection.Up },
                VsCommandId = "Edit.UncommentSelection",
                VsCommandName = "Uncomment Selection"
            });
        }
    }
}