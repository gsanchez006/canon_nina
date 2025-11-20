using System.Windows;
using System.ComponentModel.Composition;

namespace NINA.Plugin.CanonAstroImage {
    [Export(typeof(ResourceDictionary))]
    public partial class Options : ResourceDictionary {
        public Options() {
            InitializeComponent();
        }
    }
}
