namespace Mp4Conv.Web.Models;

public class ConversionSettingsModel
{
    public static readonly VideoCodec VideoCopyCodec = new("Copy", "copy");
    public static readonly AudioCodec AudioCopyCodec = new("Copy", "copy");

    public bool DeleteMkvFiles { get; set; } = true;

    public bool OverwriteMp4Files { get; set; } = true;

    public VideoCodec VideoCodec { get; set; } = ConversionSettingsModel.VideoCopyCodec;

    public AudioCodec AudioCodec { get; set; } = ConversionSettingsModel.AudioCopyCodec;

    public int? VideoQuality { get; set; }

    public string? AudioBitrate { get; set; }
}
