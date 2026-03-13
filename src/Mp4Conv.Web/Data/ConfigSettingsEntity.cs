namespace Mp4Conv.Web.Data;

public class ConfigSettingsEntity
{
    public int Id { get; set; }

    public string RootPath { get; set; } = string.Empty;

    public int MaxNumberOfConcurrentConversions { get; set; } = 1;

    public bool PauseConversions { get; set; }

    public bool UseHardwareAcceleration { get; set; } = true;

    public long ProcessorAffinityMask { get; set; } = 0;
}
