using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace W10MDashcam
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e) => Show(e.Arguments);

        /// <summary>The app is registered for .mp4 (to write to the SD card); opening a video with it just starts the app.</summary>
        protected override void OnFileActivated(FileActivatedEventArgs args) => Show(null);

        private static void Show(string arguments)
        {
            var rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
            }
            if (rootFrame.Content == null)
            {
                rootFrame.Navigate(typeof(MainPage), arguments);
            }
            Window.Current.Activate();
        }
    }
}
