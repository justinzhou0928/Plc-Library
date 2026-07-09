using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.Tests.DriverDomain;

public class KeyValueConnectionStringTests
{
    [Fact]
    public void Parse_StandardFormat_ReturnsDictionary()
    {
        var dict = KeyValueConnectionString.Parse("host:192.168.1.1;port:102;rack:0");
        Assert.Equal("192.168.1.1", dict["host"]);
        Assert.Equal("102", dict["port"]);
        Assert.Equal("0", dict["rack"]);
    }

    [Fact]
    public void Parse_CaseInsensitive()
    {
        var dict = KeyValueConnectionString.Parse("HOST:192.168.1.1;Port:102");
        Assert.Equal("192.168.1.1", dict["host"]);
        Assert.Equal("102", dict["PORT"]);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmpty()
    {
        var dict = KeyValueConnectionString.Parse("");
        Assert.Empty(dict);
    }

    [Fact]
    public void Parse_Null_ReturnsEmpty()
    {
        var dict = KeyValueConnectionString.Parse(null!);
        Assert.Empty(dict);
    }

    [Fact]
    public void Parse_TrimsKeysAndValues()
    {
        var dict = KeyValueConnectionString.Parse(" host : 192.168.1.1 ; port : 102 ");
        Assert.Equal("192.168.1.1", dict["host"]);
        Assert.Equal("102", dict["port"]);
    }

    [Fact]
    public void Parse_SkipsMalformedEntries()
    {
        var dict = KeyValueConnectionString.Parse("host:192.168.1.1;bad;port:102");
        Assert.Equal(2, dict.Count);
        Assert.Equal("192.168.1.1", dict["host"]);
        Assert.Equal("102", dict["port"]);
    }
}
