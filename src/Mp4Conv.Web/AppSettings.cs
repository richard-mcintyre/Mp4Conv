namespace Mp4Conv.Web;

public class AppSettings
{
    #region Construction

    public AppSettings()
    {
    }

    #endregion

    #region Properties

    public string RootPath { get; set; } = string.Empty;

    public VideoCodec[] VideoCodecs { get; set; } = Array.Empty<VideoCodec>();

    public AudioCodec[] AudioCodecs { get; set; } = Array.Empty<AudioCodec>();

    #endregion
}

public record VideoCodec(string Name, string Codec);

public record AudioCodec(string Name, string Codec);


