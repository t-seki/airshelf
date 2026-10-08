using Airshelf.Core.Model;
using Airshelf.Core.Sources;

namespace Airshelf.Tests;

public sealed class SourceRouterTests
{
    private sealed class StubImporter(string ext, string tag) : ISourceImporter
    {
        public bool CanHandle(RecordingFile file) => file.Extension == ext;
        public ParsedSource Parse(RecordingFile file) => new()
        {
            RawStation = tag,
            RawProgram = tag,
            BroadcastDate = new DateOnly(2020, 1, 1),
        };
    }

    private static RecordingFile Rec(string ext) => new()
    {
        FilePath = "/in/x" + ext,
        FileName = "x" + ext,
        StationFolder = "S",
        ProgramFolder = "P",
        Extension = ext,
    };

    private static SourceRouter Router() =>
        new([new StubImporter(".m4a", "rajireko"), new StubImporter(".mp3", "radikool")]);

    [Fact]
    public void Dispatches_ByExtension()
    {
        Assert.Equal("radikool", Router().Parse(Rec(".mp3")).RawProgram);
        Assert.Equal("rajireko", Router().Parse(Rec(".m4a")).RawProgram);
    }

    [Fact]
    public void CanHandle_TrueIfAnyImporterHandles()
    {
        Assert.True(Router().CanHandle(Rec(".mp3")));
        Assert.False(Router().CanHandle(Rec(".wav")));
    }

    [Fact]
    public void Parse_NoHandler_Throws()
    {
        Assert.Throws<SourceParseException>(() => Router().Parse(Rec(".wav")));
    }
}
