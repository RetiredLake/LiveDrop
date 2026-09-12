using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Forms;
using Windows.ApplicationModel.DataTransfer;

// Manual integration fixture: exercises the actual Windows share target, including
// deferred data that fails to materialize. It never sends content to a network peer.
internal sealed class ShareSourceSmoke : Form
{
    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        [return: MarshalAs(UnmanagedType.IInspectable)]
        object GetForWindow(IntPtr window, ref Guid iid);
        void ShowShareUIForWindow(IntPtr window);
    }
    private DataTransferManager _manager;
    private IDataTransferManagerInterop _interop;
    private bool _fail;

    [STAThread]
    private static void Main() { Application.Run(new ShareSourceSmoke()); }

    private ShareSourceSmoke()
    {
        Text = "LiveDrop share regression source";
        Width = 420; Height = 160;
        var share = new Button { Text = "Share test text", Width = 180, Top = 15, Left = 15 };
        var fail = new Button { Text = "Share unavailable data", Width = 180, Top = 55, Left = 15 };
        Controls.Add(share); Controls.Add(fail);
        share.Click += (s, e) => ShowShare(false);
        fail.Click += (s, e) => ShowShare(true);
        Load += (s, e) =>
        {
            _interop = (IDataTransferManagerInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(DataTransferManager));
            var iid = new Guid("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
            _manager = (DataTransferManager)_interop.GetForWindow(Handle, ref iid);
            _manager.DataRequested += (manager, args) =>
            {
                Log("DataRequested fail=" + _fail);
                args.Request.Data.Properties.Title = "LiveDrop regression test";
                if (_fail) args.Request.Data.SetDataProvider(StandardDataFormats.Text, request => { });
                else args.Request.Data.SetText("LiveDrop share regression: caf\u00e9 \u2603");
            };
        };
    }

    private static void Log(string text)
    {
        System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LiveDrop-share-source.log"), DateTime.Now + " " + text + Environment.NewLine);
    }
    private void ShowShare(bool fail)
    {
        try
        {
            _fail = fail;
            Activate();
            Log("ShowShare");
            _interop.ShowShareUIForWindow(Handle);
        }
        catch (Exception ex) { Log(ex.ToString()); }
    }
}
