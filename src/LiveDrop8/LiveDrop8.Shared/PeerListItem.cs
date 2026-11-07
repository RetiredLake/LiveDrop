using System;
using LiveDrop;
using LiveDrop.Models;
namespace LiveDrop8
{
    internal sealed class PeerListItem : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly bool _showProtocol;
        private bool _selected;
        public Windows.UI.Xaml.Media.Brush SelectionBrush
        { get { return new Windows.UI.Xaml.Media.SolidColorBrush(_selected ? Windows.UI.Colors.White : Windows.UI.Colors.Gray); } }
        internal void SetSelected(bool selected)
        {
            if (_selected == selected) return;
            _selected = selected;
            var changed = PropertyChanged;
            if (changed != null) changed(this, new System.ComponentModel.PropertyChangedEventArgs("SelectionBrush"));
        }
        internal PeerDescriptor Peer { get; private set; }
        internal PeerListItem(PeerDescriptor peer, bool showProtocol) { Peer = peer; _showProtocol = showProtocol; }
        internal string Key { get { return GetKey(Peer); } }
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        internal void Update(PeerDescriptor peer)
        {
            if (peer == null) return;
            Peer = peer;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("DisplayText"));
        }
        internal static string GetKey(PeerDescriptor peer)
        {
            if (peer == null) return string.Empty;
            var identity = !string.IsNullOrWhiteSpace(peer.Address) ? peer.Address.ToLowerInvariant() : peer.StableId;
            return identity;
        }
        internal bool Matches(PeerDescriptor peer)
        {
            if (peer == null || Peer == null) return false;
            if (Peer.Transport == peer.Transport && !string.IsNullOrWhiteSpace(Peer.StableId) && !string.IsNullOrWhiteSpace(peer.StableId) &&
                string.Equals(Peer.StableId, peer.StableId, StringComparison.Ordinal)) return true;
            return !string.IsNullOrWhiteSpace(Peer.Address) && string.Equals(Peer.Address, peer.Address, StringComparison.OrdinalIgnoreCase);
        }
        public string DisplayText
        {
            get { return Peer.DisplayName + (_showProtocol ? " - " + (Peer.Transport == ShareTransport.GoogleQuickShare ? AppText.Get("QuickShareName") : AppText.Get("MicrosoftNearbyShare")) : ""); }
        }
        public override string ToString() { return DisplayText; }
    }
}
