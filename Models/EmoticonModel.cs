namespace BiliVoxLive.Models;

public class EmoticonPackage
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public List<Emoticon> Emoticons { get; set; } = new();
}

public class Emoticon
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;  // 发送时使用的文本
    public string Description { get; set; } = string.Empty;  // 表情描述
    public string Package { get; set; } = string.Empty;  // 所属包名
    public string emoticon_unique { get; set; } = string.Empty;  // 修改为小写
}