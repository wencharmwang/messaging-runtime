namespace MessagingRuntime.Providers.WeChat;

public static class WeChatParser
{
    public static Message Parse(string provider, string conversation, string raw)
    {
        var direction = MessageDirection.Unknown;
        var body = raw;
        if (raw.StartsWith("【我】", StringComparison.Ordinal)) { direction = MessageDirection.Outgoing; body = raw[3..]; }
        else if (raw.StartsWith("【对方】", StringComparison.Ordinal)) { direction = MessageDirection.Incoming; body = raw[4..]; }
        // Driver media labels are observations, not downloaded attachments. Do not invent senders/times.
        var type = body.StartsWith("[图片]", StringComparison.Ordinal) ? MessageType.Image
            : body.StartsWith("[语音", StringComparison.Ordinal) ? MessageType.Audio
            : body.StartsWith("[视频]", StringComparison.Ordinal) ? MessageType.Video
            : body.StartsWith("[文件]", StringComparison.Ordinal) ? MessageType.File : MessageType.Text;
        return new("", provider, conversation, null, direction, type, body, Attachments: Array.Empty<Attachment>());
    }
}
