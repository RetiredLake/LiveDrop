using System;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LiveDrop
{
    sealed partial class App : Application
    {
        internal ShareTargetActivatedEventArgs PendingShare { get; private set; }

        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
            UnhandledException += OnUnhandledException;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            NavigateToMainPage();
            Window.Current.Activate();
        }

        protected override void OnShareTargetActivated(ShareTargetActivatedEventArgs args)
        {
            PendingShare = args;
            NavigateToMainPage();
            Window.Current.Activate();
            var page = (Window.Current.Content as Frame)?.Content as MainPage;
            if (page != null) page.ConsumePendingShare();
        }

        private static void NavigateToMainPage()
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }
            if (frame.Content == null) frame.Navigate(typeof(MainPage));
        }

        internal ShareTargetActivatedEventArgs TakePendingShare()
        {
            var share = PendingShare;
            PendingShare = null;
            return share;
        }

        private void OnSuspending(object sender, SuspendingEventArgs e) { }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "startup-error.txt"),
                    e.Exception.ToString());
            }
            catch { }
        }
    }
}

