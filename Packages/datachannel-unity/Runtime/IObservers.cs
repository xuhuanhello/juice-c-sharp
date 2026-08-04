namespace DataChannelUnity
{
    public interface IPeerConnectionObserver
    {
        void OnLocalDescription(string sdp, string type);
        void OnLocalCandidate(string candidate, string mid);
        void OnConnectionStateChange(ConnectionState state);
        void OnGatheringStateChange(GatheringState state);
        void OnDataChannel(DataChannel channel);
    }

    public interface IDataChannelObserver
    {
        void OnOpen();
        void OnClosed();
        void OnError(string message);
        void OnMessage(System.ReadOnlySpan<byte> data);
    }
}
