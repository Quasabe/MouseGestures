using System.Collections.Generic;

namespace MouseGestures.Models
{
    /// <summary>
    /// Container model for exporting/importing gestures and visualization settings.
    /// </summary>
    public class GestureSettingsExport
    {
        public List<MouseGesture> Gestures { get; set; } = new List<MouseGesture>();
        public GestureVisualizationSettings VisualizationSettings { get; set; } = new GestureVisualizationSettings();
    }
}