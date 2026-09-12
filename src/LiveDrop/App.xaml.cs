using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LiveDrop
{
    sealed partial class App : Application
    {

        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
            UnhandledException += OnUnhandledException;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            NavigateToMainPage();
            var page = (Window.Current.Content as Frame)?.Content as MainPage;
            page?.BeginRegularSession();
            Window.Current.Activate();
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            base.OnActivated(args);
        }

        protected override async void OnShareTargetActivated(ShareTargetActivatedEventArgs args)
        {
            try
            {
                NavigateToMainPage();
                Window.Current.Activate();
                var page = (Window.Current.Content as Frame)?.Content as MainPage;
                if (page != null)
                {
                    page.BeginShareTargetSession();
                    await page.ReceiveShareAsync(args.ShareOperation);
                }
            }
            catch (Exception ex)
            {
                try { args.ShareOperation.ReportError("LiveDrop could not open this share: " + ex.Message); } catch { }
            }
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

        private void OnSuspending(object sender, SuspendingEventArgs e) { }

        private void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
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
