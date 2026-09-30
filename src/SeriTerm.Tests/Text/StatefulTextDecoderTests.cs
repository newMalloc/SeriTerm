using SeriTerm.Core.Text;

namespace SeriTerm.Tests.Text;

public class StatefulTextDecoderTests
{
    [Fact]
    public void GB2312_汉字被切包后仍应正确解码()
    {
        // "中" 在 GB2312 中为 D6 D0
        var decoder = new StatefulTextDecoder("GB2312");

        var first = decoder.Decode([0xD6]);
        var second = decoder.Decode([0xD0]);

        Assert.Equal(string.Empty, first);
        Assert.Equal("中", second);
    }

    [Fact]
    public void UTF8_三字节汉字被切包后仍应正确解码()
    {
        // "中" 的 UTF-8 为 E4 B8 AD
        var decoder = new StatefulTextDecoder("UTF-8");

        Assert.Equal(string.Empty, decoder.Decode([0xE4]));
        Assert.Equal(string.Empty, decoder.Decode([0xB8]));
        Assert.Equal("中", decoder.Decode([0xAD]));
    }

    [Fact]
    public void 逐块新建解码器会乱码_证明有状态解码的必要性()
    {
        var first = new StatefulTextDecoder("GB2312").Decode([0xD6]);
        var second = new StatefulTextDecoder("GB2312").Decode([0xD0]);

        // 无状态时第一块无法还原（输出替换字符），这正是"中文乱码"的来源
        Assert.NotEqual("中", first + second);
    }

    [Fact]
    public void 完整多字节内容应一次性解码()
    {
        var decoder = new StatefulTextDecoder("GB2312");
        var bytes = System.Text.Encoding.GetEncoding("GB2312").GetBytes("串口调试");

        Assert.Equal("串口调试", decoder.Decode(bytes));
    }

    [Fact]
    public void 切换编码应生效()
    {
        var decoder = new StatefulTextDecoder("UTF-8");
        decoder.SetEncoding("GB2312");

        var bytes = System.Text.Encoding.GetEncoding("GB2312").GetBytes("测试");
        Assert.Equal("测试", decoder.Decode(bytes));
    }

    [Fact]
    public void 非法编码名应降级为UTF8()
    {
        var decoder = new StatefulTextDecoder("不存在的编码");

        Assert.Equal("utf-8", decoder.EncodingName);
        Assert.Equal("AB", decoder.Decode("AB"u8.ToArray()));
    }

    [Fact]
    public void Reset_应丢弃挂起的半个字符()
    {
        var decoder = new StatefulTextDecoder("GB2312");
        decoder.Decode([0xD6]);

        decoder.Reset();

        // 重置后再喂入后半个字节，不应拼出汉字
        Assert.NotEqual("中", decoder.Decode([0xD0]));
    }

    [Fact]
    public void 可选编码列表应包含常用中文编码()
    {
        Assert.Contains("GB2312", StatefulTextDecoder.SupportedEncodingNames);
        Assert.Contains("UTF-8", StatefulTextDecoder.SupportedEncodingNames);
        Assert.Contains("BIG5", StatefulTextDecoder.SupportedEncodingNames);
    }
}
