using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MouseGestures.Models
{
    /// <summary>
    /// Represents a mouse gesture pattern.
    /// </summary>
    public class MouseGesture : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _vsCommandId = string.Empty;
        private string _vsCommandName = string.Empty;
        private bool _isEnabled = true;

        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                OnPropertyChanged();
            }
        }

        public List<GestureDirection> Pattern { get; set; } = new List<GestureDirection>();

        public string VsCommandId
        {
            get => _vsCommandId;
            set
            {
                _vsCommandId = value;
                OnPropertyChanged();
            }
        }

        public string VsCommandName
        {
            get => _vsCommandName;
            set
            {
                _vsCommandName = value;
                OnPropertyChanged();
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                _isEnabled = value;
                OnPropertyChanged();
            }
        }

        public string PatternDescription => string.Join(" ", Pattern.Select(Utils.Utils.GetDirectionArrow));

        public bool MatchesPattern(List<GestureDirection> detectedPattern)
        {
            if (detectedPattern.Count != Pattern.Count)
                return false;

            return Pattern.SequenceEqual(detectedPattern);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}