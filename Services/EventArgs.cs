namespace BiliVoxLive;

public class AudioDataEventArgs : EventArgs
{
    public long RoomId { get; init; }
    public byte[] Data { get; init; }
    public DateTime Timestamp { get; }

    public AudioDataEventArgs(byte[] audioData)
    {
        Data = audioData;
        Timestamp = DateTime.Now;
    }

    // 添加一个带 RoomId 的构造函数
    public AudioDataEventArgs(byte[] audioData, long roomId) : this(audioData)
    {
        RoomId = roomId;
    }
}