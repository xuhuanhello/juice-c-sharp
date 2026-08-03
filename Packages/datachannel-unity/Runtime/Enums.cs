namespace DataChannelUnity
{
    public enum ConnectionState
    {
        New = 0,
        Connecting = 1,
        Connected = 2,
        Disconnected = 3,
        Failed = 4,
        Closed = 5
    }

    public enum GatheringState
    {
        New = 0,
        InProgress = 1,
        Complete = 2
    }

    public enum IceTransportPolicy
    {
        All = 0,
        RelayOnly = 1
    }

    public enum LogLevel
    {
        None = 0,
        Fatal = 1,
        Error = 2,
        Warning = 3,
        Info = 4,
        Debug = 5,
        Verbose = 6
    }
}
